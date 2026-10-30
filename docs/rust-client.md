# The Rust client

[openac-rust](https://github.com/eriknihlen/openac-rust) is the Rust port of
OpenAC. It aims at full feature parity with the C# client and is maintained
alongside it: the same behavior as the original game, in modern Rust. Changes
go to both clients, so a fix or feature in one reaches the other too.

## How it differs from OpenAC

- **One program, many sessions.** `openac` runs any number of game sessions in
  one process, with zero or more windows. A window can open onto any running
  session and close again without the session noticing; a session without a
  window keeps playing headless. The launcher can switch a running session
  between headless and graphical.
- **Plugins are WebAssembly.** A plugin is an ordinary Rust crate built for
  `wasm32-unknown-unknown`, written against the `openac-plugin` SDK (MIT). The
  same plugin runs with or without a window, sandboxed, with only the
  permissions its `plugin.json` declares. .NET plugins do not run here; the
  owner's MossTank and MosswartMassacre plugins have Rust versions.
- **Rendering** goes through [wgpu](https://wgpu.rs): Direct3D 12 or Vulkan on
  Windows, Vulkan on Linux, Metal on macOS.
- **The launcher** (`openac-launcher`) keeps your accounts and servers, starts
  sessions graphical or headless, installs plugins, and updates itself.

## Plugin API versions

The plugin interface is versioned, and a plugin built for an older version
keeps loading and receives exactly what it did before:

| Version | Since | What it added |
|---|---|---|
| 1.0 | 0.1.0 | the core API |
| 1.1 | 0.1.3 | status-line notices (`StatusTextReceived`) |
| 1.2 | 0.1.6 | a house hook's accepted item types on items and world objects |

To use a newer version, move the plugin's SDK to that release and set
`"apiVersion"` in its `plugin.json`.

## Platforms

Windows 10 and 11 (64-bit) is where it is played most. Linux (64-bit, X11 or
Wayland) builds and runs, with less time in front of players. macOS 14 or
later on Apple silicon has downloads since 0.1.8; there is no Intel Mac build.

## More

- [Download and run](getting-started.md#openac-rust-rust)
- [Building openac-rust](https://github.com/eriknihlen/openac-rust/blob/main/docs/building-and-running.md)
- [Launch options](https://github.com/eriknihlen/openac-rust/blob/main/docs/launch-options.md)
- [Writing a Rust plugin](https://github.com/eriknihlen/openac-rust/blob/main/docs/plugin-development.md)
