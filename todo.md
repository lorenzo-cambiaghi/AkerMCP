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
- [ ] **Whether the client/window offset exists at all.** It should not: the capture is at
      nominal resolution and the bounds are in points. That is an assumption, and `coords`
      is what turns it into a measurement.

Both **Accessibility** and **Screen Recording** permission are required. The tools name the
right settings pane when they are missing.

## Plugins still ship the 1.6.0 contract

- [ ] Regenerate the bundled DLLs (`copy-dlls`) so Unity, Godot and Stride carry
      `AkerMcp.Shared`/`AkerMcp.Client` 1.7.0.

Not urgent and not broken: a 1.6.0 plugin talks to a 1.7.0 server, verified in the field —
the handshake says *"compatible. Anything added after the plugin's version answers
NOT_SUPPORTED."* Until they are regenerated those engines simply cannot declare a consent
policy, which is a capability staying off rather than a failure.

Must be done with the editors closed: `copy-dlls` runs `dotnet clean -c Release`, and a
running Unity holds those files. The bundles are gitignored, so this is a local step, not a
commit.

## Release

- [ ] Product version is still 1.5.0 while the IPC protocol is 1.7.0.

The two numbers are independent on purpose (`ServerVersion.cs` explains it), and in this
repo the product version is stamped when a release is cut, not when work lands. So this is
only a to-do if the next thing is a release.

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
