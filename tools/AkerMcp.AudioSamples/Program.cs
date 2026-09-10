using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using AkerMcp.Server;

namespace AkerMcp.AudioSamples
{
    /// <summary>
    /// Writes the audio sample gallery: the WAV files and the page that plays them, both from
    /// the specs below.
    ///
    /// <para>One source, two outputs, on purpose. A gallery whose specs are retyped beside the
    /// files drifts the first time a default changes, and then it teaches the wrong recipe —
    /// worse than having no gallery, because it looks authoritative. Here the spec that plays
    /// is the spec that rendered.</para>
    ///
    /// <para>It is a page rather than the README because GitHub strips &lt;audio&gt; from
    /// markdown: a sound library nobody can hear is a list of adjectives. The page is plain
    /// HTML with relative paths, so it works opened from disk and served from GitHub Pages
    /// without changing anything.</para>
    ///
    ///   dotnet run --project tools/AkerMcp.AudioSamples -- docs
    /// </summary>
    public static class Program
    {
        private record Sample(string Id, string Title, string Note, string Spec, bool IsMusic = false);

        private static readonly Sample[] Samples =
        {
            new("gun-pistol", "Pistol", "A gunshot is a transient, not a tone: nearly all of it is the first 20 ms. Punch for the crack, a low-pass diving to take the fizz away.",
                "{\"wave\":\"noise\",\"freq\":900,\"lowpass\":2600,\"lowpass_sweep\":-9,\"highpass\":220,\"punch\":1,\"attack\":0.001,\"sustain\":0.012,\"decay\":0.19,\"volume\":0.9}"),

            new("gun-shotgun", "Shotgun", "The same shape with a bigger chamber: coarser grain, longer tail, the filter falls further.",
                "{\"wave\":\"noise\",\"freq\":420,\"lowpass\":1800,\"lowpass_sweep\":-7,\"highpass\":120,\"punch\":1,\"attack\":0.001,\"sustain\":0.03,\"decay\":0.45,\"volume\":0.95}"),

            new("laser-shot", "Laser", "The opposite of a gunshot: pure pitch falling fast. The resonance is what gives it the whistle.",
                "{\"wave\":\"saw\",\"freq\":1800,\"freq_sweep\":-4200,\"lowpass\":3000,\"resonance\":0.5,\"attack\":0.002,\"sustain\":0.04,\"decay\":0.2,\"volume\":0.8}"),

            new("laser-heavy", "Heavy laser", "Square instead of saw, a slower fall, and a little vibrato wobble.",
                "{\"wave\":\"square\",\"freq\":900,\"freq_sweep\":-1400,\"duty\":0.35,\"lowpass\":2200,\"resonance\":0.6,\"vibrato_depth\":0.05,\"vibrato_rate\":40,\"attack\":0.003,\"sustain\":0.08,\"decay\":0.28,\"volume\":0.85}"),

            new("footstep-dirt", "Footstep, dirt", "Footsteps live or die on the low-pass: a step is a dull thud, not a hiss.",
                "{\"wave\":\"noise\",\"freq\":420,\"lowpass\":760,\"punch\":0.35,\"attack\":0.001,\"sustain\":0.008,\"decay\":0.085,\"volume\":0.6}"),

            new("footstep-metal", "Footstep, metal", "Three numbers away from the one above — finer grain, resonance, a high-pass — and it is another surface.",
                "{\"wave\":\"noise\",\"freq\":1500,\"lowpass\":3200,\"resonance\":0.7,\"highpass\":600,\"punch\":0.5,\"attack\":0.001,\"sustain\":0.006,\"decay\":0.16,\"volume\":0.6}"),

            new("bullet-impact", "Bullet impact", "Shorter and duller than the shot, so the two read as cause and effect.",
                "{\"wave\":\"noise\",\"freq\":700,\"lowpass\":1500,\"lowpass_sweep\":-6,\"punch\":0.8,\"attack\":0.001,\"sustain\":0.01,\"decay\":0.12,\"volume\":0.75}"),

            new("reload-click", "Reload click", "Very short, band-limited, no tail: a mechanism, not a sound.",
                "{\"wave\":\"noise\",\"freq\":2400,\"lowpass\":4200,\"highpass\":900,\"punch\":0.7,\"attack\":0.0005,\"sustain\":0.004,\"decay\":0.035,\"volume\":0.7}"),

            new("jump", "Jump", "The classic: a square sweeping up, kept short.",
                "{\"wave\":\"square\",\"freq\":480,\"freq_sweep\":1400,\"duty\":0.4,\"duration\":0.15,\"lowpass\":4000}"),

            new("coin", "Coin", "A pickup is a pitch jump, which is what arpeggio does in one parameter.",
                "{\"wave\":\"square\",\"freq\":900,\"arpeggio\":1.5,\"arpeggio_delay\":0.06,\"decay\":0.2,\"duration\":0.3,\"lowpass\":5000}"),

            new("explosion", "Explosion", "Low grain, a filter collapsing over most of a second, and all the punch there is.",
                "{\"wave\":\"noise\",\"freq\":120,\"lowpass\":900,\"lowpass_sweep\":-3,\"punch\":1,\"decay\":0.7,\"duration\":0.9}"),

            new("powerup", "Power-up", "repeat retriggers the envelope, so one spec becomes a run of notes.",
                "{\"wave\":\"square\",\"freq\":400,\"freq_sweep\":600,\"arpeggio\":1.5,\"repeat\":6,\"duration\":0.7,\"lowpass\":4000}"),

            new("music-overworld", "Overworld", "Lead, bass and drums. '-' holds a note, '.' is a rest, 'C3*4' spans four steps.",
                "{\"bpm\":132,\"channels\":[" +
                "{\"instrument\":\"lead\",\"notes\":\"C5 . E5 . G5 - E5 . F5 . D5 - C5 - - .\"}," +
                "{\"instrument\":\"bass\",\"notes\":\"C3*4 A2*4 F2*4 G2*4\"}," +
                "{\"instrument\":\"drums\",\"notes\":\"k . h . s . h . k . h . s . h .\"}]}", IsMusic: true),

            new("music-menu", "Menu", "Slower, no drums: a pad underneath and a pluck picking out the melody.",
                "{\"bpm\":88,\"channels\":[" +
                "{\"instrument\":\"pad\",\"notes\":\"C4*8 F4*8\"}," +
                "{\"instrument\":\"pluck\",\"notes\":\"C5 . G4 . E5 . G4 . F5 . C5 . A4 . C5 .\"}," +
                "{\"instrument\":\"bass\",\"notes\":\"C2*8 F2*8\"}]}", IsMusic: true),

            new("music-boss", "Boss", "Fast, minor, and a busy drum line.",
                "{\"bpm\":168,\"channels\":[" +
                "{\"instrument\":\"lead\",\"notes\":\"C4 C4 D#4 F4 C4 C4 F4 D#4 C4 C4 D#4 F4 G4 - F4 D#4\"}," +
                "{\"instrument\":\"bass\",\"notes\":\"C2 C2 C2 C2 C2 C2 C2 C2 A#1 A#1 A#1 A#1 G1 G1 G1 G1\"}," +
                "{\"instrument\":\"drums\",\"notes\":\"k h s h k h s h k h s h k s s s\"}]}", IsMusic: true),

            new("music-victory", "Victory", "A four-chord fanfare, over in a second and a half.",
                "{\"bpm\":140,\"channels\":[" +
                "{\"instrument\":\"lead\",\"notes\":\"C5 E5 G5 C6*4\"}," +
                "{\"instrument\":\"arp\",\"notes\":\"C4 E4 G4 C5*4\"}," +
                "{\"instrument\":\"drums\",\"notes\":\"k s k o*4\"}]}", IsMusic: true),

            // The one in the README: a loop meant to run under play for minutes without
            // wearing out. Four bars of Am–F–C–G, a melody that leaves gaps, and a bass that
            // walks to the next chord instead of sitting on the root.
            new("level-loop", "Level theme (the README example)",
                "Written at two steps per beat, so a token is an eighth note and a bar is eight tokens. The melody rests where the bass moves, which is what keeps a short loop from nagging.",
                "{\"bpm\":108,\"steps_per_beat\":2,\"channels\":[" +
                "{\"instrument\":\"lead\",\"notes\":\"E5 . . . D5 . C5 . A4 . C5 . D5 - - . E5 . G5 . E5 . D5 . C5 - - . B4 . . .\"}," +
                "{\"instrument\":\"pad\",\"notes\":\"A4*8 F4*8 C5*8 G4*8\",\"volume\":0.5}," +
                "{\"instrument\":\"bass\",\"notes\":\"A2 . A2 . E3 . A2 . F2 . F2 . C3 . F2 . C3 . C3 . G3 . C3 . G2 . G2 . D3 . G2 .\"}," +
                "{\"instrument\":\"drums\",\"notes\":\"k . h . s . h . k . h . s . h . k . h . s . h . k . h . s . h o\"}]}",
                IsMusic: true),

            new("level-loop-soundfont", "Level theme — SoundFont",
                "The same four bars with sampled instruments: the notes below are identical, only the instrument names and one word changed.",
                "{\"engine\":\"soundfont\",\"bpm\":108,\"steps_per_beat\":2,\"channels\":[" +
                "{\"instrument\":\"ocarina\",\"notes\":\"E5 . . . D5 . C5 . A4 . C5 . D5 - - . E5 . G5 . E5 . D5 . C5 - - . B4 . . .\"}," +
                "{\"instrument\":\"strings\",\"notes\":\"A4*8 F4*8 C5*8 G4*8\",\"volume\":0.5}," +
                "{\"instrument\":\"acoustic_bass\",\"notes\":\"A2 . A2 . E3 . A2 . F2 . F2 . C3 . F2 . C3 . C3 . G3 . C3 . G2 . G2 . D3 . G2 .\"}," +
                "{\"instrument\":\"drums\",\"notes\":\"k . h . s . h . k . h . s . h . k . h . s . h . k . h . s . h o\"}]}",
                IsMusic: true),

            // The A/B: identical score, one word different. Worth more than either alone.
            new("engine-chiptune", "The same score — chiptune", "Square waves and noise. Nothing to download, nothing to licence.",
                "{\"bpm\":100,\"channels\":[" +
                "{\"instrument\":\"lead\",\"notes\":\"E5 . D5 . C5 - D5 . E5 . E5 . E5 - - .\"}," +
                "{\"instrument\":\"pad\",\"notes\":\"C4*8 G3*8\"}," +
                "{\"instrument\":\"bass\",\"notes\":\"C3*4 C3*4 G2*4 G2*4\"}," +
                "{\"instrument\":\"drums\",\"notes\":\"k . h . s . h . k . h . s . h .\"}]}", IsMusic: true),

            new("engine-soundfont", "The same score — SoundFont", "One word changed, plus instrument names: flute and strings instead of lead and pad. The notes are untouched.",
                "{\"engine\":\"soundfont\",\"bpm\":100,\"channels\":[" +
                "{\"instrument\":\"flute\",\"notes\":\"E5 . D5 . C5 - D5 . E5 . E5 . E5 - - .\"}," +
                "{\"instrument\":\"strings\",\"notes\":\"C4*8 G3*8\"}," +
                "{\"instrument\":\"bass\",\"notes\":\"C3*4 C3*4 G2*4 G2*4\"}," +
                "{\"instrument\":\"drums\",\"notes\":\"k . h . s . h . k . h . s . h .\"}]}", IsMusic: true),

            new("sf-adventure", "Adventure theme — SoundFont", "Trumpet, strings and timpani, from the same grammar.",
                "{\"engine\":\"soundfont\",\"bpm\":124,\"channels\":[" +
                "{\"instrument\":\"trumpet\",\"notes\":\"G4 . C5 . E5 - D5 . C5 . G4 - - . . .\"}," +
                "{\"instrument\":\"strings\",\"notes\":\"C4*16\",\"volume\":0.6}," +
                "{\"instrument\":\"timpani\",\"notes\":\"C2 . . . G2 . . . C2 . . . G2 . . .\"}," +
                "{\"instrument\":\"drums\",\"notes\":\"k . . . s . . . k . k . s . . .\"}]}", IsMusic: true),
        };

        private static bool NeedsSoundFont(Sample s) => s.Spec.Contains("\"engine\":\"soundfont\"");

        public static int Main(string[] args)
        {
            var docsDir = args.Length > 0 ? args[0] : "docs";
            var wavDir = Path.Combine(docsDir, "samples");
            Directory.CreateDirectory(wavDir);

            var page = new StringBuilder();
            Header(page);

            Section(page, "Sound effects", "create_sound", Samples, music: false);
            Section(page, "Music", "create_music", Samples, music: true);

            Footer(page);

            bool haveFont = SoundFontLibrary.IsAvailableLocally();
            if (!haveFont)
                Console.WriteLine($"  (no SoundFont locally: set {SoundFontLibrary.PathVariable} to include " +
                                  "those samples; the existing files are left alone)\n");

            foreach (var s in Samples)
            {
                // Regenerating without a font must not delete the samples already committed:
                // a gallery that empties itself on the wrong machine is worse than a stale one.
                if (NeedsSoundFont(s) && !haveFont) { Console.WriteLine($"  {s.Id,-18} skipped"); continue; }

                var spec = JsonSerializer.Deserialize<JsonElement>(s.Spec);
                var wav = s.IsMusic ? MusicSequencer.RenderToWav(spec) : SoundSynthesizer.RenderToWav(spec);
                File.WriteAllBytes(Path.Combine(wavDir, s.Id + ".wav"), wav);
                Console.WriteLine($"  {s.Id,-18} {wav.Length / 1024,4} KB");
            }

            var pagePath = Path.Combine(docsDir, "audio-samples.html");
            File.WriteAllText(pagePath, page.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"\n  {Samples.Length} samples + {pagePath}");
            return 0;
        }

        private static void Section(StringBuilder p, string title, string tool, Sample[] all, bool music)
        {
            p.Append($"<h2>{title}</h2>\n");
            p.Append($"<p class=\"lead\">Every sample below was produced by <code>{tool}</code> from the spec beside it. " +
                     "No samples, no recordings, no asset packs: the server renders the WAV and hands it to the engine.</p>\n");
            p.Append("<div class=\"grid\">\n");
            foreach (var s in all)
            {
                if (s.IsMusic != music) continue;
                p.Append("<figure>\n");
                p.Append($"  <figcaption>{Escape(s.Title)}</figcaption>\n");
                p.Append($"  <audio controls preload=\"none\" src=\"samples/{s.Id}.wav\"></audio>\n");
                p.Append($"  <p class=\"note\">{Escape(s.Note)}</p>\n");
                p.Append($"  <pre><code>{Escape(Pretty(s.Spec))}</code></pre>\n");
                p.Append("</figure>\n");
            }
            p.Append("</div>\n");
        }

        /// <summary>Wrap a long spec so the card does not scroll sideways on a phone.</summary>
        private static string Pretty(string spec)
        {
            var outp = new StringBuilder();
            int width = 0;
            foreach (var part in spec.Split(','))
            {
                if (width > 46) { outp.Append(",\n"); width = 0; }
                else if (outp.Length > 0) { outp.Append(", "); width += 2; }
                outp.Append(part.Trim());
                width += part.Trim().Length;
            }
            return outp.ToString();
        }

        private static string Escape(string s) => s
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static void Header(StringBuilder p) => p.Append("""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>AkerMCP — audio the model writes</title>
<style>
  :root { color-scheme: dark; --bg:#12141a; --card:#191c24; --line:#2a2f3a;
          --ink:#e6e8ee; --dim:#9aa3b2; --accent:#7fd6c1; }
  * { box-sizing: border-box; }
  body { margin:0; padding:32px 16px 64px; background:var(--bg); color:var(--ink);
         font:15px/1.6 ui-sans-serif, system-ui, -apple-system, Segoe UI, Roboto, sans-serif; }
  main { max-width: 1100px; margin: 0 auto; }
  h1 { font-size: clamp(24px, 4vw, 34px); margin:0 0 8px; letter-spacing:-0.02em; }
  h2 { font-size: 20px; margin: 44px 0 4px; }
  .sub { color:var(--dim); margin:0 0 8px; }
  .lead { color:var(--dim); margin:0 0 20px; max-width:70ch; }
  .grid { display:grid; gap:16px; grid-template-columns:repeat(auto-fill, minmax(310px, 1fr)); }
  figure { margin:0; background:var(--card); border:1px solid var(--line); border-radius:10px; padding:14px; }
  figcaption { font-weight:600; margin-bottom:10px; }
  audio { width:100%; height:34px; }
  .note { color:var(--dim); font-size:13px; margin:10px 0 8px; }
  pre { margin:0; background:#0e1015; border:1px solid var(--line); border-radius:7px;
        padding:10px; overflow-x:auto; }
  code { font:12px/1.5 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; color:var(--accent); }
  a { color:var(--accent); }
  footer { color:var(--dim); margin-top:48px; font-size:13px; max-width:70ch; }
</style>
</head>
<body>
<main>
<h1>Audio the model writes</h1>
<p class="sub">Placeholder sound and music for a prototype, generated on the spot from a spec —
no asset packs, no downloads, nothing to licence.</p>

""");

        private static void Footer(StringBuilder p) => p.Append("""
<footer>
<p><strong>Why this is here.</strong> A prototype stops for want of a jump sound as surely as for
want of a jump. The synthesiser is sfxr's — pitched noise, a resonant low-pass, duty cycle,
arpeggio, punch, retrigger — and the music is a tracker over the same voice, so a melody inherits
the filters rather than reimplementing them. Chiptune rather than General MIDI on purpose: the
small GM SoundFont everyone reaches for first is GPL, and its samples end up inside the audio
produced, which for a tool whose output lands in someone else's game is the wrong thing to
inherit.</p>
<p>Copy any spec into <code>create_sound</code> or <code>create_music</code> and it produces
exactly the file you just played.</p>
</footer>
</main>
</body>
</html>
""");
    }
}
