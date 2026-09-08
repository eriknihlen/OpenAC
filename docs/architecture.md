# Architecture

OpenAC is organized by dependency layer. Lower layers never reference higher
ones, and the game's state lives in one place regardless of whether a window
is open.

```
                 ┌──────────────────────┐   ┌──────────────────────┐
                 │   AcDream.App        │   │   AcDream.Headless   │
                 │ Vulkan renderer,     │   │ no-window host,      │
                 │ game UI, audio,      │   │ bots, multi-session  │
                 │ input                │   │ scheduler            │
                 └──────────┬───────────┘   └──────────┬───────────┘
                            └──────────┬───────────────┘
                              ┌────────▼────────┐
                              │ AcDream.Runtime │  GameRuntime: session,
                              │                 │  entities, inventory,
                              └────────┬────────┘  movement, physics, magic
                    ┌──────────────────┼──────────────────┐
           ┌────────▼───────┐ ┌────────▼───────┐ ┌────────▼────────┐
           │ AcDream.Core.  │ │ AcDream.       │ │ AcDream.Platform│
           │ Net            │ │ Content        │ │ paths, RID,     │
           │ UDP, ISAAC,    │ │ DAT reader,    │ │ OS services     │
           │ messages       │ │ prepared pak   │ │                 │
           └────────┬───────┘ └────────┬───────┘ └─────────────────┘
                    └─────────────────┬┘
                             ┌────────▼────────┐
                             │  AcDream.Core   │  gameplay, physics,
                             │                 │  anim, world logic
                             └─────────────────┘
```

## Projects

| Project | Responsibility | Depends on |
|---|---|---|
| `AcDream.Platform` | Portable application paths (XDG on Linux, LocalAppData on Windows), runtime identity, process services. | BCL |
| `AcDream.Core` | Gameplay logic: physics and collision, movement, animation, terrain, scenery, spells, world rules. Pure logic, no window or GPU. | Plugin.Abstractions |
| `AcDream.Content` | Reads the game's DAT files and the machine-local prepared package `acdream.pak`. No graphics dependency. | Core |
| `AcDream.Core.Net` | The wire protocol: UDP transport, ISAAC cipher, fragment assembly, reliable delivery, and every game-message parser and builder. | Core |
| `AcDream.Runtime` | `GameRuntime`, the presentation-independent kernel. Owns the session, entities and objects, inventory, character state, selection and interaction, combat and casting, local movement, physics simulation, projectiles, environment, and portal transit. Exposes borrowed views, typed commands, and ordered deltas. | Core, Core.Net, Content, Platform, Plugin.Abstractions |
| `AcDream.App` | The graphical client: Vulkan 1.3 renderer over Silk.NET, world streaming, the game UI built from the game's own layout data, audio, and input. Projects runtime state; owns no gameplay truth. | Runtime, UI.Abstractions, Core, Core.Net, Content, Platform, and the bundled `AcDream.Plugins.MossTank` |
| `AcDream.Headless` | Runs one or many `GameRuntime` sessions without a window: deterministic bot commands and events, a scheduler, shared immutable content, resource telemetry. Windows and Linux. | Runtime |
| `AcDream.UI.Abstractions` | Input actions, key chords and bindings, the input dispatcher, view models, and panel contracts shared by the game UI and plugins. | Core, Runtime |
| `AcDream.Plugin.Abstractions` | The BCL-only contract plugins compile against: game state, events, commands, and markup panels. Plugins never reference `AcDream.App`. | BCL |
| `AcDream.Plugins.MossTank` | A complete bundled plugin: VTank-style automation with the familiar tabbed UI. Doubles as the reference for plugin authors. | Plugin.Abstractions |
| `AcDream.Bake` | Offline tool that builds `acdream.pak` from the DAT files. | Content, Platform |
| `AcDream.Cli` | Offline DAT inspector. | Core |
| `AcDream.Launcher.Core` | Installer, verified downloader, atomic updater, self-updater, profiles, and the client process supervisor. Testable without a GUI. | Platform |
| `AcDream.Launcher` | The Avalonia desktop launcher over `Launcher.Core`. | Launcher.Core |

`samples/` holds three render-pack samples that exercise the renderer's
extension points. `tools/` holds the shader compiler and the render-pack
validator (see `tools/README.md`).

## The two rules that shape the code

**Behavior comes from the original game.** Anything the original client did
in the world (how a slope stops you, when an animation swaps, what bytes go on
the wire) is reproduced as observed. Readable structure, original semantics.

**Gameplay truth has one owner.** `GameRuntime` and its owners hold the only
copy of session, entity, inventory, movement, and physics state. The graphical
client and the headless host borrow views of the same objects; neither keeps a
mirror. This is what lets a bot and a window run the same game.

## Content model

The client reads the game's DAT files directly for most data. World meshes and
collision, which are expensive to decode, come from `acdream.pak`, a validated
memory-mapped package that `AcDream.Bake` produces once per machine from those
same DAT files. The package is machine-local and never committed.

## Rendering

The renderer is Vulkan 1.3 only, through Silk.NET, with bindless textures and
multi-draw indirect. Shaders are GLSL compiled to committed SPIR-V by
`tools/compile-shaders.ps1`; a test re-hashes the sources against the
manifest so a stale binary fails CI. The startup capability probe reports an
actionable error if the device lacks a required feature.

## UI

The gameplay UI is the game's own: layouts are imported from its layout data
and bound to runtime state by focused controllers. Plugin panels use a markup
vocabulary documented in `plugin-ui-markup.md` and render in the same look.

## Tests

Most `src` projects have a matching `tests/AcDream.<Name>.Tests` project. Tests
that need something the machine may not have carry a `Lane` trait
(`InstalledDat`, `PreparedPackage`, `Live`, `Vulkan`, `Windows`, `Linux`,
`Timing`, `Manual`, `SystemFont`); the portable filter in `release-gate.md`
excludes them, and CI runs the hardware lanes on machines that have the
hardware.
