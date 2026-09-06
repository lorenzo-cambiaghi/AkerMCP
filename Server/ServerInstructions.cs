namespace AkerMcp.Server
{
    /// <summary>
    /// What the model reads before its first call.
    ///
    /// The handshake text goes out in the MCP <c>initialize</c> response, so every
    /// client gets it with no rules file to install; it stays short because it rides
    /// in the model's context on every turn. The full playbook is the
    /// <c>aker://guide</c> resource, fetched only when a client wants it. Both are
    /// engine-agnostic: the same words hold for Unity, Godot and Stride, and neither
    /// needs the engine to be connected.
    ///
    /// Before this file the same rules lived in every tool description, repeated, and
    /// in a 250-line template in the README that users were asked to copy into a
    /// rules file. Stated once here, the descriptions can say only what each tool does.
    /// </summary>
    public static class ServerInstructions
    {
        public const string GuideUri = "aker://guide";

        public const string Handshake =
            "AkerMCP drives the running game editor (Unity, Godot or Stride); the engine answers " +
            "on its main thread. Work in the order inspect, modify, verify: `inspect` a scene path " +
            "('/Player') or a type name ('Rigidbody') before touching it, and never guess property or " +
            "component names. Change one property with `set_property`; do bulk or complex work in one " +
            "`execute` script. Then confirm with `get_property` or `inspect`, and with `take_screenshot` " +
            "when the change is visual (placement, materials, lighting, UI). " +
            "Property paths are dot notation: transform properties need no prefix ('position', " +
            "'localScale'), other components take a type prefix ('Rigidbody.mass'), nested paths work " +
            "('MeshRenderer.material.color.r'), structs are JSON objects ({\"x\":1,\"y\":2,\"z\":3}). " +
            "Scene paths are case-sensitive, forward slashes from the root; `query` finds objects by " +
            "name pattern, type or tag. " +
            "`execute` runs C# through Roslyn: state does not persist between calls, always return a " +
            "value, put `using` lines at the top, default timeout 5 s (`timeout_ms`). After writing or " +
            "editing a script call `refresh_scripts`, then `get_compile_errors`; never assume it compiled. " +
            "If a call times out while the editor shows a modal dialog, `list_windows` names it and " +
            "`focus_window` followed by `send_input` ({ESC} or {ENTER}) dismisses it. " +
            "Read the `aker://guide` resource for the full playbook.";

        public const string Guide = @"# AkerMCP guide

You are driving a C# game editor (Unity, Godot or Stride) through the AkerMCP tools. The
engine executes every call on its main thread and answers with the real state, so treat
the answers as truth and your assumptions as guesses to be checked.

## Core workflow: inspect, modify, verify

1. Inspect first. Before modifying anything, `inspect` the object (a scene path such as
   `/Player`) or the type (`Rigidbody`) to see the components, properties and current
   values. Never guess a property or component name.
2. Modify. `set_property` for a single change (undoable), `execute` for bulk or complex
   work, `create` / `delete` / `call_method` for the obvious cases.
3. Verify. `get_property` or `inspect` again to confirm the change. `get_console_logs`
   when something looks wrong.
4. Look, when it matters. For anything the user would see (placement, materials,
   lighting, UI layout, scale, procedural content) call `take_screenshot` after the
   change. It catches what values cannot: an object inside another, a material that
   renders pink, a UI element clipped off-screen. Do not screenshot after every
   micro-change; once at the end of a logical edit.

```
Bad:  set_property /Player mass 5            (mass lives on Rigidbody; the path fails)
Good: inspect /Player, see Rigidbody.mass, then set_property /Player Rigidbody.mass 5
```

## Property paths

Dot notation, resolved by reflection:

```
position                     Transform.position (Transform is searched first)
position.x                   float
Rigidbody.mass               a specific component, type prefix
Rigidbody.useGravity         bool
MeshRenderer.material.color  nested
mesh.vertices[0]             array index
```

Transform properties (`position`, `rotation`, `localScale`, `eulerAngles`) need no
prefix; every other component needs its type as a prefix. Structs are JSON objects:
`{""x"": 1.0, ""y"": 2.0, ""z"": 3.0}` for a vector, `{""r"": 1, ""g"": 0, ""b"": 0, ""a"": 1}`
for a colour, `{""x"": 0, ""y"": 0, ""z"": 0, ""w"": 1}` for a quaternion.

## Which tool

| Need | Tool |
|---|---|
| Change one property | `set_property` |
| Read one value | `get_property` |
| Find objects | `query` (name pattern, type, tag) |
| Create one object | `create`, or `add_primitive` for a placeholder mesh |
| Modify many objects, generate content, reach editor APIs, create assets | `execute` |
| Confirm a visual result, or show the user the scene | `take_screenshot` |
| Exercise gameplay | `enter_play`, `send_input`, `sample_state`, `assert_state`, or `playtest` for a scripted run |
| Ship | `list_platforms`, `set_platform_settings`, `switch_build_target`, `build_player` |

## Writing `execute` scripts

Globals available without setup (Unity names shown; Godot and Stride expose the same
over their own node and entity types): `selectedObject`, `Find(""name"")`,
`FindAll<T>()`, `Create(""name"")`, `Log(message)`. Pre-imported: `System`,
`System.Collections.Generic`, `System.Linq` and the engine's namespaces. Anything else:
put `using ...;` lines at the top of the snippet; they are hoisted to file scope.

State does not persist between calls. Each script compiles and runs on its own, so
re-acquire what you need inside the script:

```csharp
// wrong: 'player' came from a previous call
return player.transform.position;
// right
var player = Find(""Player"");
return player.transform.position;
```

Always `return` something meaningful; a script that returns nothing gives you no
feedback. The default timeout is 5 seconds (`timeout_ms` to extend). The timeout only
stops the wait: a running script keeps running on the main thread, so avoid unbounded
loops and check the scene after a timeout.

## After writing or editing a script

Call `refresh_scripts` (the engine recompiles), then `get_compile_errors`. Fix and
repeat until clean. Never assume a change compiled.

## Finding your way

Scene paths use forward slashes from the root and are case-sensitive: `/Player`,
`/Environment/Trees/Oak_01`. When you do not know the path, `query` by name pattern
(`Player*`), by type (`Camera`) or by tag; the `scene://hierarchy` resource returns the
whole tree.

## When the editor stops answering

A modal dialog in the editor (""Scene(s) Have Been Modified"" and friends) blocks its
main thread, and every call then times out. `list_windows` shows the dialog by its
title; `focus_window` brings it to the front and `send_input` with `{ESC}` cancels it
or `{ENTER}` accepts the default. Keep the open scene clean before opening another,
entering play or building: save what is part of the work, discard what was a probe.

## Anti-patterns

| Instead of | Do |
|---|---|
| Guessing property names | `inspect` first |
| `execute` for a single property | `set_property` (undoable) |
| Ignoring compile results | `refresh_scripts`, then `get_compile_errors` |
| Building complex objects one property at a time | one `execute` script |
| An `execute` without a return | return a string that says what happened |
| Trusting values for a visual change | `take_screenshot` |

## When a call fails

Read the error; it usually names the cause. `inspect` the target: the object may not
exist or the property may be named differently. `get_console_logs` with an error filter
shows runtime errors, `get_compile_errors` the exact file, line and column.
";
    }
}
