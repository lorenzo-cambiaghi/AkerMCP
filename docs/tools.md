# MCP tools and resources

## MCP Tools

Every tool definition rides in your client's context on every turn, so the set is layered. `core` (14 tools, about 2,300 tokens of definitions) is inspect, edit, execute, screenshot, logs and compile. `standard` (27 tools, about 4,100 tokens, the default) adds scripts, scenes, play control, the engine pin and the OS window tools that unblock a modal dialog. `full` (42 tools, about 8,100 tokens) adds sprite, sound and music authoring, the verification tools, `playtest`, the build pipeline and `ui_action`. Pick one with `--profile core` in the server's arguments or `AKER_MCP_PROFILE=core` in its environment; `AKER_MCP_TOOLS_INCLUDE=playtest,build_player` adds single tools to a profile and `AKER_MCP_TOOLS_EXCLUDE` removes them. A call to a hidden tool answers with the profile that hid it and how to load it, and the handshake instructions list the hidden ones, so the model asks instead of guessing.

Every tool carries the four MCP hints (read-only, destructive, idempotent, open-world), so a client can auto-approve the reads and ask before `delete`, `execute`, `call_method`, `send_input`, `ui_action`, `write_script`, `new_scene`, `open_scene`, `switch_build_target` and `build_player`.

### Scene

| Tool | Profile | What it does |
|------|---------|--------------|
| `inspect` | core | Components, properties, methods and children of a scene object or of a type; `depth`, `include_methods`, `filter` |
| `get_property` | core | Read one property by dot path (`position`, `Rigidbody.mass`, `MeshRenderer.material.color.r`) |
| `set_property` | core | Set one property by dot path; structs as JSON objects. Undoable on Stride; on Unity and Godot not yet |
| `call_method` | core | Invoke a method on a scene object or a static member of a type; string arguments, converted |
| `query` | core | Find objects by type, name glob or regex, tag or property values |
| `create` | core | Add an object with a type, name, optional parent and initial properties, with undo |
| `delete` | core | Remove an object, with undo; `recursive: false` keeps its children |
| `select` | core | Select an object in the editor; it becomes `selectedObject` in `execute` |
| `get_selection` | core | What the user has selected: path, components, property summary |

### Development workflow

| Tool | Profile | What it does |
|------|---------|--------------|
| `refresh_scripts` | core | Compile pending script changes and return errors and warnings; blocks through Unity's domain reload |
| `get_compile_errors` | core | The last compilation's result without recompiling |
| `get_console_logs` | core | Recent console entries, filtered by level or text |
| `execute` | core | Run any C# on the engine's main thread through Roslyn and return its value; globals `selectedObject`, `Find`, `FindAll<T>`, `Create`, `Log` |
| `take_screenshot` | core | JPEG of the Game view (default) or the Scene view with gizmos |
| `write_script` | standard | Write a source file into the project by a path relative to its root, wherever the server runs |
| `engine_status` | standard | Which engine answers, which others run (with pid and project), and a pin by engine, pid or project that survives reconnects |

### Scene and 2D authoring

| Tool | Profile | What it does |
|------|---------|--------------|
| `new_scene` | standard | A fresh scene, 2D by default, optionally saved to an asset path |
| `open_scene` | standard | Open a scene by its engine asset path |
| `save_scene` | standard | Save the edited scene in place or to a path |
| `create_sprite` | full | Author a flat, geometric shape-spec; AkerMCP rasterises it locally to a PNG and imports it as a sprite on any engine |
| `create_sound` | full | Synthesise a short sfxr-style sound and import it as an audio clip ([samples](https://lorenzo-cambiaghi.github.io/AkerMCP/audio-samples.html)) |
| `create_music` | full | Sequence a chiptune loop over the same voice and import it as an audio clip |
| `add_primitive` | full | Write a vetted gameplay script (2D platformer controller, auto-runner, camera follow, kill zone, score overlay) |

> **Engine support:** `create_sprite` imports + places a sprite on **Unity** and **Godot**; on **Stride** it creates a sprite sheet asset (`.sdsheet`, honouring pixels-per-unit and pivot) in the package and, when you pass `scene_path` or `position`, adds an entity with a `SpriteComponent` to the open scene through its undoable hierarchy, so it is saved with the scene. `create_sound` works on **Unity** and **Godot**; **Stride** has no sound importer yet and says so. `add_primitive` carries Unity variants only for now, and when it cannot serve your engine it names the ones a given primitive exists for. `new_scene`/`open_scene`/`save_scene` work on **Unity** and **Godot** (file-on-disk scenes) and on **Stride** (package-managed `SceneAsset` via the editor). `write_script` works on all three. *(Stride's scene creation is verified live in Game Studio; its sprite-sheet import and placement are build-verified only so far.)*

**`add_primitive` next to `write_script`**: `write_script` writes the source you hand it, so the file is whatever the model composed. `add_primitive` writes a file the model did not compose. The server keeps five gameplay scripts that already work, picks the variant for the connected engine and sends it down the same write path. Call it with no `id` and it lists the catalog; call it with one and it reports the file it wrote, the public fields you can set, and what to do next.

| `id` | What it is | Public fields |
|---|---|---|
| `platformer_controller_2d` | Move and jump with ground check | `moveSpeed`, `jumpForce`, `gravityScale`, `groundTag`, `jumpKey` |
| `auto_runner_2d` | Constant forward run, jump on input | `forwardSpeed`, `jumpForce`, `gravityScale`, `groundTag`, `jumpKey` |
| `camera_follow_2d` | Smoothed follow with offset | `target`, `offset`, `smooth`, `followY` |
| `killzone_2d` | Kill on contact with a tag | `deadlyTag`, `reloadOnDeath` |
| `score_overlay` | On-screen score counter | `score`, `label` |

So: `add_primitive` for the piece every 2D prototype needs and nobody wants rewritten from scratch, `write_script` for the part that is yours. The Unity variants read input through the legacy Input Manager. Either way the file lands on disk, so `refresh_scripts` comes next, then attaching the component and setting the fields.

**`create_sprite` shape-spec**: drawn in order (painter's): `ellipse`, `rect` (with `rx` for rounded corners), `polygon`, `line`/`polyline`, and `path` (an SVG path-data subset). Each shape takes a `fill` (hex or linear `gradient`), optional `stroke`/`strokeWidth`, and `opacity`. Example (a flat bird placeholder):

```json
{
  "name": "bird", "pixels_per_unit": 64, "pivot": {"x":0.5,"y":0.5},
  "scene_path": "/World", "position": {"x":-3,"y":0,"z":0},
  "spec": { "width":64, "height":64, "shapes": [
    {"type":"ellipse","cx":31,"cy":34,"rx":23,"ry":21,"fill":"#FFC107","stroke":"#C98A00","strokeWidth":2},
    {"type":"polygon","points":[[52,29],[64,33],[52,38]],"fill":"#FF8C00"} ] }
}
```

> Keep placeholders flat and geometric: recognizable silhouette over detail. For arbitrary SVG (boolean paths, filters, tracing) a dedicated vector tool would be the right home; `create_sprite` deliberately targets the clean-prototype niche.

### Runtime loop

| Tool | Profile | What it does |
|------|---------|--------------|
| `enter_play` / `exit_play` | standard | Run the project and stop it; Unity's domain reload and reconnect are handled |
| `get_play_state` | standard | Playing, paused, time, frame counter; read twice to tell a frozen game from a live one |
| `set_play_pause` | standard | Pause or resume |
| `send_input` | standard | Inject key, mouse button and mouse move events into the running game (`window_title` to send them OS-level to a named window: a game in its own window, an editor window, a dialog; `coords: "image"` to give mouse positions in screenshot pixels) |
| `play_step` | full | Advance frames while paused |
| `capture_sequence` | full | Several screenshots at an interval, returned as a strip, to see motion |

> **Engine support:** the runtime loop is backed by the optional `IPlayModeController` (play control) and the optional `IInputSimulator` (in-process input, with an OS-level fallback). Support is honest per engine:
> - **Unity**, full: Play Mode runs in the Game View, so `take_screenshot`/`capture_sequence` capture the live game; pause/step supported. Reuses the domain-reload reconnect from `refresh_scripts`.
> - **Godot**, partial: the game runs in a **separate window** (screenshot it with `capture_window`, drive it with `send_input`'s OS-level path + `window_title`); no editor-side pause/step.
> - **Stride**: Game Studio has no plugin-controllable Play Mode; `enter_play` reports this and points you to `build_player` + running the produced executable.
>
> `send_input` prefers an engine's in-process `IInputSimulator`, otherwise focuses the game/engine window and injects via **OS-level injection** (Windows `SendInput`, macOS Quartz `CGEvent`; Linux reports unsupported). On **Unity** the in-process path drives the **new Input System** (`com.unity.inputsystem`) directly, resolved via reflection, so there is no hard package dependency; projects on the legacy Input Manager (or without the package) fall back to OS-level automatically. On **Godot/Stride** the game is a separate window, so pass `window_title` (the game window's title) or the OS-level path targets the editor. An explicit `window_title` always takes the OS-level path: the in-process path can reach no window but the engine's own game view, and on Unity it used to swallow a click meant for an editor window and report success. The `action` event type is reserved but not yet injectable; drive the key/mouse controls the action is bound to.

### Verify and iterate

| Tool | Profile | What it does |
|------|---------|--------------|
| `sample_state` | full | Evaluate C# expressions in the running game and return their values |
| `assert_state` | full | Compare runtime expressions to expected values with `==`, `<`, `approx`, `truthy` and friends, polled until they hold |
| `playtest` | full | One call: enter play, run a timed list of input, wait, capture, assert and sample steps, check the final criteria, exit play; returns one verdict with frames and evidence |

### Platform and build

| Tool | Profile | What it does |
|------|---------|--------------|
| `list_platforms` | full | The build platforms the engine knows, flagged active and buildable |
| `get_platform_settings` / `set_platform_settings` | full | Read and change a platform's build and player settings as a key-value map |
| `switch_build_target` | full | Make a platform the active target |
| `build_player` | full | Build the project for a platform and return a report |

> Engine differences are handled gracefully: e.g. Godot and Stride have no global active target, so `switch_build_target` reports that and you pass the platform directly to `build_player`.

### Windows on your machine

| Tool | Profile | What it does |
|------|---------|--------------|
| `list_windows` | standard | Visible top-level windows: title, process, pid; works with no engine connected |
| `capture_window` | standard | Screenshot any window by a title substring, occluded or not, without stealing focus; reports the image's size, scale and screen origin |
| `focus_window` | standard | Bring a window to the foreground, restoring it if minimised |
| `ui_action` | full | Click, drag, scroll and type in a window, in the pixels of the screenshot it returns |

The first three are how the model recovers from a modal dialog that blocks the editor's main thread, and how it screenshots a Godot game that runs in its own window.

#### `ui_action`: driving an interface the way a person would

One call is one step of look, act, look: name an action (`screenshot`, `mouse_move`, `left_click`, `right_click`, `middle_click`, `double_click`, `triple_click`, `left_click_drag`, `scroll`, `type`, `key`, `wait`) and a point, and the answer carries the screenshot taken after it.

Coordinates are **pixels of the last screenshot**, measured from its top-left — never screen coordinates. The server holds the mapping: where the window's client area sits on the virtual screen and the scale the image was shrunk by on the way out (anything over 1920 px is resized). That mapping is what makes a click land where the model was looking; measured against the OS cursor it is exact to the pixel, on a scaled capture and on a second monitor alike. Two guards come with it: a point outside the image is refused rather than clamped, and if the window has been **resized** since the screenshot the call is refused too, because the layout no longer matches the picture the coordinates came from. A window that was merely **moved** is followed silently.

```jsonc
{"action": "screenshot", "window_title": "Unity"}      // look, and anchor to that window
{"action": "left_click", "x": 1420, "y": 305}          // a point read off that image
{"action": "type", "text": "3.5"}                      // into whatever the click focused
{"action": "key", "keys": "ctrl+s"}
```

**Menus are in the picture.** A dropdown is a window of its own, floating above the one that owns it, so a plain window capture renders the interface with the menu missing — the click that opened it looks like it did nothing. Popups belonging to the same process are composited onto the capture where they really are: on Windows by pasting them in from a screen read (a layered, composited menu comes back blank from `PrintWindow`), on macOS by handing Quartz the window and its popups together (`CGWindowListCreateImageFromArray`). An unrelated application overlapping the window is never pasted in, and a covered window still captures. Focusing is skipped when that process already owns the foreground, because raising a window that is already in front closes the menu you were halfway through. Input needed no change: a click is sent in screen coordinates and already lands on whatever is topmost there.

It is the last resort, not the way to edit a scene: `set_property` changes a value whatever the inspector looks like, while a click depends on the window's size, its layout and which panel has focus. Use it for what no API exposes — a modal dialog, an import wizard, a plugin's own window, a preference the engine only offers in its UI — and remember the two costs: it needs the foreground, so it takes the mouse from whoever is at the machine, and every step puts another screenshot in the context. It ships in the `full` profile; to add it to `standard` alone, set `AKER_MCP_TOOLS_INCLUDE=ui_action`.

Typing goes in one character at a time with a pause between them, because a window's tolerance for synthetic text is a property of that window: measured against Windows 11 Notepad, 12 ms dropped a character and 25 ms and above were exact, so the default is 30 ms. `AKER_MCP_TYPE_GAP_MS` overrides it for a slower target.

**Platforms.** On Windows this is `SendInput` against the virtual desktop, measured exact to the pixel on a scaled capture and on a second monitor. On macOS it is Quartz `CGEvent`, and it needs **Accessibility** permission (System Settings → Privacy & Security → Accessibility) on top of the Screen Recording permission captures already need — without it `CGEventPost` discards events silently, so the tool checks first and says which switch to flip instead of reporting a click that never happened. The macOS path compiles but has not been run on a Mac yet: the parts to check first are the ANSI keycode table used by `key`, the click-state field that makes a double click a double click, and whether activating the owning process through System Events reliably brings the target window forward. Linux reports that OS-level input is not implemented.

#### When the editor can say no

An editor whose user grants and revokes the agent's access has a problem the server used to make
worse. Its refusals go through the adapter, and an adapter refuses a screenshot by returning no
image — which is also what an adapter with no screen capture returns. The server, reading "no
image", did the helpful thing and grabbed the window with `PrintWindow` instead. The user revoked
the session and got a photograph of their screen anyway, taken by the half of the system their
revocation never reached. `ui_action` would have widened that from looking to clicking.

`IAgentConsent` closes it by letting the engine say *refused* where it could only say *nothing*:

```csharp
protected override IAgentConsent? CreateAgentConsent() => new MyConsent(access);

public ConsentState GetConsent() => new ConsentState
{
    Granted = access.SessionOpen,
    Reason  = access.SessionOpen ? null : "the user has not allowed a session",
};
```

The server asks before every call that reaches around the engine — the screenshot fallback,
`list_windows`, `capture_window`, `focus_window`, `ui_action`, and `send_input`'s OS-level path —
and declines with the engine's own sentence. It never asks on the ordinary inspect/modify/verify
path, which the adapter already governs.

Three properties make it cheap and safe to leave on:

- **An engine without it pays one round trip per connection.** The NOT_SUPPORTED answer is final,
  so Unity, Godot and Stride are asked once and never again.
- **An answer is cached for five seconds**, so a twelve-step `ui_action` is not twelve IPC calls.
- **Silence keeps the last answer.** A revocation already heard survives a frozen editor; an engine
  that never answered was not claiming a policy. Without this, an editor stuck behind a modal dialog
  could not be unblocked from outside — which is the one thing the OS-level tools are for. There is
  also no consent call at all when no engine is connected: asking would queue behind the reconnect
  grace period, and it once turned a three-step `ui_action` into 22 seconds.

`GetConsent` is read off the pipe thread, deliberately, so it must be a cheap read of current state
that any thread can make — never a prompt, never a wait on the main thread. That is the whole point:
it is asked when the main thread may be the thing that is stuck.

#### How `take_screenshot` works

The tool follows a **hybrid capture strategy** that prefers quality but always succeeds:

1. **Engine-internal path** *(implemented by all three adapters)*: captures the Scene view directly from the editor's render buffer **including gizmos** (Unity `GrabPixels`, Godot viewport, Stride editor back-buffer via `Texture.Save`). Works even when the editor window is occluded or partially off-screen. Highest quality.
2. **OS-level fallback** *(automatic, cross-platform on Windows + macOS)*: captures the engine's main window without stealing foreground focus. Works for any C# engine without requiring adapter code. Per-OS implementation is selected at runtime:
   - **Windows**: Win32 `PrintWindow(PW_RENDERFULLCONTENT)` via `user32.dll`
   - **macOS**: Quartz `CGWindowListCreateImage` via `CoreGraphics.framework` + `ImageIO.framework`. Window discovery: enumerates on-screen windows owned by the engine PID; among those, prefers any whose title contains the engine name (anywhere; it matches both "Unity 6000…" and "… Godot Engine") and within that subset picks the largest by area. If no title contains the engine name, falls back to the largest PID-owned window
   - **Linux**: not implemented; the engine adapter must implement `IScreenCapture`

Output is automatically (cross-platform via ImageSharp):
- **Resized** to a maximum of 1920px on the longest side
- **Re-encoded as JPEG** (quality 85)

Typical output size: **~150-400 KB**, comfortably under Claude API image limits (~5 MB).

**Parameters:**

```json
{ "view": "game" }   // default: captures the Game View
{ "view": "scene" }  // captures the active Scene View with gizmos (Unity / Godot / Stride)
```

**Example:**

```
→ set_property {"object_path": "/Player", "property_path": "Light.color", "value": {"r":1,"g":0,"b":0,"a":1}}
← Property 'Light.color' set successfully on /Player

→ take_screenshot {"view": "scene"}
← [JPEG image, 1920×1080, 287 KB]   // AI now sees the red light
```

#### macOS: Screen Recording permission

On macOS 10.15+, capturing windows from another process requires **Screen Recording permission** for the binary running the AkerMcp server. This affects only the OS-level path (`list_windows`, `capture_window`, `ui_action` and the `take_screenshot` fallback), and `ui_action` needs Accessibility permission on top of it; the engine-internal `IScreenCapture` path (implemented by all three engine adapters) works without any permission grant.

**First-time setup:**

1. The first time the OS-level fallback is invoked, macOS shows a permission prompt for the binary running the server (typically `dotnet`).
2. If you miss the prompt or denied it, open: **System Settings → Privacy & Security → Screen Recording**
3. Add (or enable the toggle for) the binary running AkerMcp:
   - If you launch via `dotnet run --project Server` → the entry is `dotnet` (or `dotnet [version]`)
   - If you ship a self-contained build → the entry is your published executable
4. **Restart the server.** macOS caches the denial decision until the process restarts; granting alone is not enough.

**Verification:**

```bash
# Trigger a screenshot from your AI client. If permission is missing, the tool returns:
#   "macOS denied the screen capture (CGWindowListCreateImage returned NULL)..."
# Follow the steps above and try again after restarting the server.
```

**Why no permission is needed for Unity (and most engines):** The Unity adapter implements `IScreenCapture` using its own Camera/SceneView render buffer. That happens entirely *inside* the Unity process, so macOS doesn't treat it as cross-process screen capture and no permission is required. Only when no adapter capture exists does AkerMcp fall back to the OS-level path that triggers the permission flow.

### Dynamic Code Execution (`execute`)

The `execute` tool runs arbitrary C# code inside the live editor, **Unity, Godot or Stride**, using Roslyn. This is the most powerful tool: it can do anything that engine's editor API allows, with no fixed tool surface.

**What it enables:**

- Procedural scene generation (spawn 100 objects in a grid, create terrain, etc.)
- Bulk property modifications across many objects
- Asset manipulation (create materials, import textures, modify prefabs)
- Complex queries that go beyond what `query` supports
- Editor automation (menu items, build pipeline, custom importers)
- Anything you can do in an editor script for that engine

**Built-in globals** available in your code *(shown for the Unity adapter; the Godot and Stride adapters expose equivalent globals over their own `Node` / `Entity` types)*:

| Global | Type | Description |
|--------|------|-------------|
| `selectedObject` | `GameObject?` | Currently selected GameObject in the editor |
| `Find(name)` | `GameObject?` | Shortcut for `GameObject.Find(name)` |
| `FindAll<T>()` | `T[]` | Find all objects of type T |
| `Create(name)` | `GameObject` | Create a new empty GameObject |
| `Log(message)` | `void` | Log to the Unity console |

**Imported namespaces** (no `using` needed): `System`, `System.Collections.Generic`, `System.Linq`, plus the engine's namespaces: `UnityEngine`/`UnityEditor` (Unity), `Godot` (Godot), `Stride.Engine`/`Stride.Core.Mathematics` (Stride).

Need another namespace? Add `using ...;` directives at the **top** of the snippet; they are hoisted to file scope automatically (e.g. `using System.IO;`).

**Declaring types works too.** A snippet is wrapped as a method body, but `class`, `struct`, `interface`, `enum`, `record` and `delegate` declarations are lifted to file scope before compiling, so helper classes, fake implementations of an interface, callback receivers and `MonoBehaviour`s you then `AddComponent` all work directly, with no reflection workarounds. Access modifiers are adjusted for you (`private class Foo` is accepted), and local functions inside the body keep working as usual.

**Examples** (Unity-flavored; the same patterns apply with each engine's API):

```csharp
// Create a grid of cubes
for (int x = 0; x < 5; x++)
    for (int z = 0; z < 5; z++) {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = $"Cube_{x}_{z}";
        cube.transform.position = new Vector3(x * 2, 0, z * 2);
    }
```

```csharp
// Set all enemies to red
var enemies = FindAll<Renderer>()
    .Where(r => r.gameObject.name.StartsWith("Enemy"))
    .ToArray();
foreach (var r in enemies)
    r.material.color = Color.red;
return $"Colored {enemies.Length} enemies";
```

```csharp
// Return scene stats
var objects = FindAll<GameObject>();
var types = objects
    .SelectMany(go => go.GetComponents<Component>())
    .Where(c => c != null)
    .GroupBy(c => c.GetType().Name)
    .Select(g => $"{g.Key}: {g.Count()}")
    .ToArray();
return string.Join("\n", types);
```

> **Note:** Each `execute` call is compiled and run independently: variables do **not** persist between calls, so every script must be self-contained. The evaluator runs on Unity's main thread with full Editor API access; `Debug.Log` output produced during the run is captured and returned in the `output` field.

### How property paths work

Properties are accessed via **dot-notation paths** resolved at runtime through reflection:

```
transform.position.x          → float
Rigidbody.mass                → float (targets a specific component)
MeshRenderer.material.color   → Color
```

When a property name is ambiguous (e.g. `enabled` exists on multiple components), prefix it with the component type: `Rigidbody.enabled`, `MeshRenderer.enabled`.

---

## MCP Resources

| URI | Description |
|-----|-------------|
| `aker://guide` | The usage playbook as markdown: workflow, property paths, `execute` rules, screenshots, recovery. Answered by AkerMCP itself, no engine needed |
| `scene://hierarchy` | Full scene tree with components listed per object |
| `project://info` | Engine name/version, project path, active scene |
| `editor://logs` | Recent console entries |
| `editor://compile_status` | Compilation status, error/warning counts |
| `engine://types` | Registered engine type names |

---
