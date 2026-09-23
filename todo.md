# todo

What is left after the ui_action work, and why each item is still open. Written so that
picking one up does not require reconstructing the reasoning first.

## macOS: written, never executed

The whole macOS half of `ui_action` compiles and has never run on a Mac. It is not a
stub — `MacInputInjector` (Quartz `CGEvent`), plus window geometry, by-title capture,
window listing and popup composition in `MacScreenCapture` — but nothing there has been
observed working.

**Start here:** `dotnet run --project tools/AkerMcp.UiProbe -- coords "<window>"`. It moves
the pointer to points read off a screenshot and asks the OS where it landed. Zero means the
capture and the coordinate conversion agree, and only the input path is left to check. A
non-zero number means the picture is the problem, not the keycodes — which is the mistake
that cost the most time on Windows.

Then, in order of how likely they are to be wrong:

- [ ] **The ANSI keycode table behind `key`.** Virtual keycodes are keyboard *positions*.
      The table assumes a US layout, which is the convention shortcuts are written in, but
      it is a table of magic numbers nobody has exercised.
- [ ] **The click-state field for double clicks.** A double click on macOS is not two clicks
      close together: it is one click whose `kCGMouseEventClickState` says 2. If double
      clicks behave as two single clicks, this is why.
- [ ] **Focus through System Events.** `FocusWindowByTitle` shells out to `osascript`
      because window ordering belongs to the owning app and CoreGraphics will not do it.
      Needs Accessibility permission; check the failure message is the one that appears.
- [ ] **Popup composition.** `CGWindowListCreateImageFromArray` should put a window and its
      open menus in one image, bounded by the window's own rect. `shot` with a menu open
      answers this at a glance: if the menu is missing, the picture lies exactly the way the
      Windows one did before it was fixed.
- [ ] **The fixes made from reading alone (2026-09-23).** Scrolling moved to the
      non-variadic `CGEventCreateScrollWheelEvent2` (the variadic one reads its second wheel
      off the stack on Apple Silicon); `AXIsProcessTrusted` and the key-down flag are marshalled
      as one byte; a `send_input` button press happens where the pointer really is
      (`CGEventGetLocation`), and a move with a button held posts a drag; `osascript` gets a
      real 4-second timeout and names the Automation permission. A scroll in both directions
      and a `send_input` press/move/release drag check the first two at a glance.
- [ ] **Whether the client/window offset exists at all.** It should not: the capture is at
      nominal resolution and the bounds are in points. That is an assumption, and `coords`
      is what turns it into a measurement.

Both **Accessibility** and **Screen Recording** permission are required. The tools name the
right settings pane when they are missing.

## Known limits, decided rather than pending

Listed so nobody spends an afternoon rediscovering them as bugs.

- **Consent belongs to the connected engine.** It governs every OS-level call whatever
  window that call names. So a second editor's window is refused while this one has revoked
  — the safe direction — but is not protected by *its* policy, which this server never asked
  for. Pin the engine whose rules should apply.
- **Popups are only composited while their process is frontmost.** They are identified by
  owner, but their pixels come off the screen, so a background window could paste another
  application's content into the picture. A background window has no menu worth capturing,
  so the safe case and the useful case coincide.
- **No automated test covers the client-area invariant.** The bug that produced it was not
  arithmetic — that part was right — but the size of the bitmap handed to `PrintWindow`.
  A test over the arithmetic would have passed then and would pass now. `UiProbe coords` is
  the real check, and it has to be run by hand.
- **The consent tests cost ~10 s** of the loopback run, waiting for the 5-second freshness
  window to lapse twice. Worth it for testing an expiry that actually expires; worth
  revisiting if the suite gets slow.
- **`ui_action` asks for consent once per call, not per step.** A revocation during a
  twelve-step sequence takes effect at the next call rather than the next step. A sequence
  is seconds long, so this is a bound worth knowing rather than a hole worth plumbing.
- **`coords` re-reads a point that disagrees.** Touching the physical mouse mid-run moves
  the pointer after the tool has placed it, which reads as an error of hundreds of pixels;
  a real conversion error is deterministic and survives the retry. If a point reports a
  non-zero error twice, believe it.

## The SoundFont, once

- [ ] Upload `FluidR3_GM_GS.sf2` as the `soundfont-v1` release asset, so the default URL
      resolves. Until then, `engine: "soundfont"` works only with `AKER_MCP_SOUNDFONT` set to
      a local copy — which is what the error message says.

The file is 151,001,312 bytes with SHA-256
`545B2833936F15F04DF5F0C5C4096B3BA6CED46EC7031F61991CAE46F8681986`, already pinned in
`SoundFontLibrary`, so the upload has to be *that* file: any other download is rejected.
Source: https://archive.org/download/fluidr3-gm-gs/FluidR3_GM_GS.sf2 — the FluidR3 GM+GS
bank published on archive.org by Frank Wen, MIT, with the notice in
`docs/FluidR3-LICENSE.txt`. It downloads in seconds, so there is no need to keep a copy
around waiting for the upload.

It goes in a release rather than the repository for the same reason the binaries do — and
GitHub refuses a file over 100 MB in a checkout regardless.
