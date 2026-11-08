# The client

OpenAC is an open-source Asheron's Call client written in Rust: the same
behavior as the original game, in modern code. It began as openac-rust and is
released from [this repository](https://github.com/eriknihlen/OpenAC) since
0.2.0. Download the launcher from [Getting started](getting-started.md); it
fetches the client.

> **The C# client is retired.** Its last release is
> [v0.1.33](https://github.com/eriknihlen/OpenAC/releases/tag/v0.1.33) and its
> code is on the [`deprecated` branch](https://github.com/eriknihlen/OpenAC/tree/deprecated).
> This client replaces it, with Rust versions of the MossTank and
> MosswartMassacre plugins.

## What it is

- **One program, many sessions.** `openac` runs any number of game sessions in
  one process, with zero or more windows. A window can open onto any running
  session and close again without the session noticing; a session without a
  window keeps playing headless. The launcher can switch a running session
  between headless and graphical.
- **Plugins are WebAssembly.** A plugin is an ordinary Rust crate built for
  `wasm32-unknown-unknown`, written against the `openac-plugin` SDK (MIT). The
  same plugin runs with or without a window, sandboxed, with only the
  permissions its `plugin.json` declares. See
  [Writing a plugin](plugin-development.md).
- **Rendering** goes through [wgpu](https://wgpu.rs): Direct3D 12 or Vulkan on
  Windows, Vulkan on Linux, Metal on macOS.
- **The launcher** (`openac-launcher`) keeps your accounts and servers, starts
  sessions graphical or headless, installs plugins, and updates itself and the
  client.

## Plugin API versions

The plugin interface is versioned, and a plugin built for an older version
keeps loading and receives exactly what it did before:

| Version | Since | What it added |
|---|---|---|
| 1.0 | 0.1.0 | the core API |
| 1.1 | 0.1.3 | status-line notices (`StatusTextReceived`) |
| 1.2 | 0.1.6 | a house hook's accepted item types on items and world objects |
| 1.3 | 0.1.9 | drawing in the world: markers, lines, triangles, icons and ground shapes |
| 1.4 | 0.1.10 | five more themes, the bundled and a plugin's own fonts, light and dark colours and image files in markup, a dock icon per window |
| 1.5 | 0.1.10 | every reachable cell of a dungeon with its route's length, a preview's grid reuse and time, cells' doorways, seen cells, bounds and floor height; in 0.1.11 also putting items into the open chest and the landscape's terrain and map colours |
| 1.6 | 0.1.11 | a canvas hears the pointer hover and leave |
| 1.7 | 0.1.11 | plugin services (plugins call each other) and the server's last position of objects |
| 1.8 | 0.1.11 | status board subscriptions: a plugin hears another's status changes |
| 1.9 | 0.2.2 | a creature's appraisal profile (health, attributes, stamina and mana, and which are enchanted) in its item properties |

From 1.6 on, a released version never changes: a new function, event or
field comes with the next minor version. 0.2.0 adds none; 1.9 is current.

To use a newer version, move the plugin's SDK to that release and set
`"apiVersion"` in its `plugin.json`.

## Platforms

Windows 10 and 11 (64-bit) is where it is played most. Linux (64-bit, X11 or
Wayland) builds and runs, with less time in front of players. macOS 14 or
later has downloads for Apple silicon since 0.1.8 and for Intel since 0.2.0.

## More

- [Download and run](getting-started.md)
- [Building and running](building-and-running.md)
- [Launch options](launch-options.md)
- [Writing a plugin](plugin-development.md)
- [Issues](https://github.com/eriknihlen/OpenAC/issues): bugs and requests for
  the Rust client
