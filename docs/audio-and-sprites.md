# Placeholder sprites, sound and music

Why AkerMCP makes its own placeholder assets, and what they sound like. Every parameter of `create_sprite`, `create_sound` and `create_music` is in [Scene and 2D authoring](tools.md#scene-and-2d-authoring).

Then there is the asset gap. A model writes code, not pixels. `create_sprite` closes it: the model describes the shape in JSON, with ellipses, rectangles, polygons and SVG path data, plus linear gradients and per-shape opacity, and AkerMCP rasterises that on your machine to an RGBA PNG at 4x supersampling before the plugin imports it as a sprite. The vector never reaches the editor, so Godot and Stride need no SVG support of their own to get the same placeholder Unity gets. The prototype stops waiting for someone to draw the bird.

**And it stops waiting for someone to record the gunshot.** `create_sound` is a full sfxr voice — pitched noise, a resonant low-pass, duty cycle, arpeggio, punch, retrigger — and `create_music` is a tracker over that same voice, so a melody inherits the filters instead of reimplementing them. Both hand the engine a finished WAV. Chiptune rather than General MIDI on purpose: the small GM SoundFont everyone reaches for first is GPL, and its samples end up inside the audio produced, which for a tool whose output lands in someone else's game is the wrong thing to inherit. Square waves cost nothing, carry no licence, and are the idiom a prototype is expected to sound like.

When a prototype outgrows square waves, the same score plays on sampled instruments: `"engine": "soundfont"` renders it through MeltySynth and FluidR3 (MIT), so `lead` becomes a flute and `pad` becomes strings without rewriting a note. The 151 MB font is neither in this repository nor in the release package — it is fetched once on first use, pinned by SHA-256, and cached per user; `AKER_MCP_SOUNDFONT` points at your own instead. See [the licence notice](FluidR3-LICENSE.txt): its terms cover redistributing sound libraries, not the audio you generate.

Background music for a level is one call. Four bars of A minor, a token per eighth note, looping under play:

```jsonc
{
  "name": "level-theme",
  "spec": {
    "bpm": 108, "steps_per_beat": 2,
    "channels": [
      { "instrument": "lead",  "notes": "E5 . . . D5 . C5 . A4 . C5 . D5 - - . E5 . G5 . E5 . D5 . C5 - - . B4 . . ." },
      { "instrument": "pad",   "notes": "A4*8 F4*8 C5*8 G4*8", "volume": 0.5 },
      { "instrument": "bass",  "notes": "A2 . A2 . E3 . A2 . F2 . F2 . C3 . F2 . C3 . C3 . G3 . C3 . G2 . G2 . D3 . G2 ." },
      { "instrument": "drums", "notes": "k . h . s . h . k . h . s . h . k . h . s . h . k . h . s . h o" }
    ]
  },
  "scene_path": "/Music", "loop": true, "auto_play": true
}
```

The melody rests where the bass walks to the next chord, which is what keeps a short loop from nagging after the third pass. One pass through the score is enough — `"loop": true` leaves the repeating to the engine, which is both smaller and seamless; the score's own `repeat` is for when you want the piece itself to be longer. Add `"engine": "soundfont"` and swap the instrument names — `ocarina`, `strings`, `acoustic_bass` — and those same notes come back played rather than bleeped. Both versions are on the page below.

**▶ [Listen to what it generates](https://lorenzo-cambiaghi.github.io/AkerMCP/audio-samples.html)** — that level theme both ways, gunshots, lasers, footsteps and half a dozen other pieces, each next to the spec that produced it. (GitHub strips `<audio>` from markdown and shows an HTML file in the repository as source, so the samples play on that page, served by GitHub Pages; `docs/audio-samples.html` plays the same from a checkout.)
