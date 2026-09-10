# tools/

Benches and generators. Nothing here ships: `build-package.bat` publishes the server
explicitly and never sweeps the solution, so these are only built by `dotnet build`.

## AkerMcp.AudioSamples

Writes `docs/audio-samples.html` and the WAVs it plays, both from one list of specs.

```
dotnet run --project tools/AkerMcp.AudioSamples -- docs
```

One source, two outputs, deliberately. A gallery whose specs are retyped beside the files
drifts the first time a default changes, and then it teaches the wrong recipe — worse than
no gallery, because it looks authoritative. Here the spec that plays is the spec that
rendered. Re-run it after touching `SoundSynthesizer` or `MusicSequencer`, and listen: these
are the only tests for the half of "does it sound right" that no measurement covers.

It is a page rather than the README because **GitHub strips `<audio>` from markdown**. Plain
HTML with relative paths, so it works the same opened from a checkout and served from GitHub
Pages.

## AkerMcp.UiProbe

Measures `ui_action` against the operating system, which is the only witness that does not
share the code's assumptions.

That distinction is the whole point. `ui_action` can always be checked against itself —
convert a coordinate, convert it back, watch the two agree — and that check passed while
every click landed a title bar low. `PrintWindow` renders the *whole* window; the capture was
being cut to the size of the client area and then described as if it started there, so the
image carried 31 px of title bar that the anchor did not know about. The arithmetic was exact
and pointed at the wrong pixels. Only asking the OS where the pointer actually went could tell
the difference, and that is what `coords` does.

The second thing a unit test will not tell you: a dropdown is a window of its own, so a plain
window capture renders the interface with its menus missing. `shot` is for looking.

```
dotnet run --project tools/AkerMcp.UiProbe -- coords "SkelForge"
dotnet run --project tools/AkerMcp.UiProbe -- shot   "SkelForge" out.jpg
dotnet run --project tools/AkerMcp.UiProbe -- run    calls.json
```

The window is matched on a substring of its title, exactly as the tools do. `--server <path>`
picks a different binary (the default is the repo's Debug build — check the first line of
output, because measuring the wrong build is the easiest way to waste an afternoon).
`--profile` defaults to `full`, so `ui_action` is loaded.

**`coords`** takes a screenshot, then sends the pointer to five points read off it and asks
the OS where it ended up. Exit code 0 means every point was exact. A maximized window can
honestly show a few pixels of error at its very first pixels — its client area starts
off-screen and the pointer clamps to the desktop edge — but anywhere else a non-zero number
means the image and the anchor disagree.

**`shot`** saves the capture. Look for two things: no title bar at the top (the image is the
client area, not the window), and any menu left open drawn in place.

**`run`** replays a JSON array of tool calls, so a gesture can be scripted without a client.
Images land next to the file as `run-1.jpg`, `run-2.jpg`, …

```json
[
  {"name": "ui_action", "arguments": {"action": "screenshot", "window_title": "SkelForge"}},
  {"name": "ui_action", "arguments": {"steps": [
      {"action": "left_click", "x": 135, "y": 171},
      {"action": "type", "text": "player"}
  ]}}
]
```

### Measuring the typing rate

Not a command, because it needs a target that shows the text back. The recipe: open a text
editor on a file, script a `run` that clicks into it, types a known string and saves, then
read the file. Compare with `AKER_MCP_TYPE_GAP_MS` set to different values — it is how the
30 ms default was chosen (12 ms dropped a character in Windows 11 Notepad; 25, 40 and 60 were
exact). A window's tolerance for synthetic text is a property of that window, so it is worth
re-measuring against a new target that misbehaves.

### On macOS

`coords` reads the pointer through `CGEventGetLocation`, so it works there too, and it is the
first thing to run: the macOS input path compiles but has never been executed. Four things to
expect trouble from, in order — the ANSI keycode table behind `key`, the click-state field
that makes a double click a double click, whether activating the owning process through System
Events reliably brings the window forward, and whether `CGWindowListCreateImageFromArray`
composes a menu into the capture the way the Windows path does (`shot` with a menu open
answers that one in a glance). Both Accessibility and Screen Recording permission are
required, and the tools say so when they are missing.

The capture there is taken at nominal resolution and the bounds are in points, so the
client/window offset that bit on Windows should not exist — but that is an assumption, not a
measurement. `coords` is how it stops being one.
