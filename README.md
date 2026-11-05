<p align="center">
  <img src="assets/icons/launcher-256.png" alt="openac-rust" width="96" height="96">
</p>

<h1 align="center">openac-rust</h1>

<p align="center">
  An open-source Asheron's Call client in Rust.<br>
  <strong>The original game's behavior, in a codebase built to be worked on.</strong>
</p>

<p align="center">
  <a href="https://github.com/eriknihlen/openac-rust/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/eriknihlen/openac-rust/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/eriknihlen/openac-rust/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/eriknihlen/openac-rust?display_name=tag"></a>
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/github/license/eriknihlen/openac-rust"></a>
  <img alt="Rust 1.95" src="https://img.shields.io/badge/Rust-1.95-DEA584?logo=rust">
  <img alt="Platforms" src="https://img.shields.io/badge/platforms-Windows%20%7C%20Linux-blue">
</p>

## What is openac-rust

openac-rust is the Rust port of [OpenAC](https://github.com/eriknihlen/OpenAC),
the open-source C# client for Asheron's Call. It aims at full feature parity
with OpenAC and is maintained alongside it: the same behavior as the original
game, in modern Rust.

- **One program, many sessions.** `openac` runs any number of game sessions
  in one process, with zero or more windows. A window can open onto any
  running session and close again without the session noticing; a session
  without a window keeps playing headless.
- **Renders** with [wgpu](https://wgpu.rs) (Direct3D 12, Vulkan or Metal) in a
  [winit](https://github.com/rust-windowing/winit) window, with the original
  game's own interface rebuilt from its data files.
- **Talks** to [ACEmulator](https://github.com/ACEmulator/ACE) servers over
  the game's wire protocol.
- **Plugins are WebAssembly.** A plugin is an ordinary Rust crate built for
  `wasm32-unknown-unknown`; the same plugin runs with or without a window,
  sandboxed, with the permissions it declares.
- **A launcher** (`openac-launcher`) keeps your accounts and servers, starts
  sessions graphical or headless, and installs plugins.

openac-rust does not ship any game data. You supply your own data files.

## Status

openac-rust is **under active development**. It ports OpenAC subsystem by
subsystem and covers nearly all of it: login and character selection, the
streamed world with its terrain, buildings and dungeons, movement and
collision, combat and magic, inventory, vendors and trade, chat, the original
interface, audio, the WebAssembly plugin host, and the launcher. Expect bugs;
reports are welcome.

**Platforms.** Windows 10 and 11 (64-bit) is where it is played most. Linux
(64-bit, X11 or Wayland) builds and runs, with less time in front of players.
macOS (macOS 14 or later, Apple silicon or Intel) builds and runs on Metal;
it is the newest platform.

**Found a bug?** Open a
[GitHub issue](https://github.com/eriknihlen/openac-rust/issues) with the
version, what you did, what happened, and a screenshot if you can.

## Download

Grab the build for your platform from the
[latest release](https://github.com/eriknihlen/openac-rust/releases/latest),
unzip it anywhere, and run the launcher:

| Platform | Download | Run |
|---|---|---|
| Windows | `openac-win-x64.zip` | `openac-launcher.exe` |
| Linux | `openac-linux-x64.zip` | `./openac-launcher` |
| macOS (Apple silicon) | `OpenAC-<version>-osx-arm64.zip` | `OpenAC.app` |
| macOS (Intel) | `OpenAC-<version>-osx-x64.zip` | `OpenAC.app` |

The zip holds both programs; the launcher starts the `openac` beside it. On
macOS they are inside `OpenAC.app`; move it to Applications and open it. The
app is signed ad hoc, not with an Apple Developer ID, so macOS blocks the
first start of a downloaded copy: allow it under System Settings, Privacy &
Security ("Open Anyway").

You will need:

- A GPU and driver with **Direct3D 12** (Windows) or **Vulkan** (Windows or
  Linux); on macOS, **Metal** (every Mac that runs macOS 14).
- **Your own Asheron's Call data files** (`client_portal.dat`,
  `client_cell_1.dat`, `client_highres.dat`, `client_local_English.dat`).
  The launcher asks for their folder on first run; `openac` looks in
  `Documents\Asheron's Call` under your user profile unless `--dat-dir` or
  `OPENAC_DAT_DIR` names another.
- **A server to connect to.** openac-rust speaks ACEmulator's protocol; a
  local ACE server works well for trying it out.

## Build from source

Install Rust with [rustup](https://rustup.rs). The repository's
`rust-toolchain.toml` pins Rust 1.95.0 with `rustfmt`, `clippy` and the
`wasm32-unknown-unknown` target, and rustup installs them on the first build.

A C compiler is needed too (the plugin engine compiles two small C files):
on Windows the Visual Studio Build Tools ("Desktop development with C++"),
on Linux your distribution's compiler, on macOS the Xcode command-line tools
(`xcode-select --install`). Linux also needs the OpenSSL and ALSA
development headers and `pkg-config`; on Debian or Ubuntu:

```bash
sudo apt install build-essential pkg-config libssl-dev libasound2-dev
```

Then:

```bash
git clone https://github.com/eriknihlen/openac-rust.git
cd openac-rust
cargo build --release
```

The programs land in `target/release/`: `openac` (the client),
`openac-launcher` (the launcher), `actool` (a data-file inspector) and
`plugincheck` (the plugin validator); on macOS, `scripts/bundle-macos.sh`
builds the launcher and the client into `OpenAC.app`. Run the launcher, or the
client on its own:

```bash
# The password comes only from the environment.
export OPENAC_PASSWORD='...'
./target/release/openac --server 127.0.0.1:9000 --account myaccount --character "My Character"
```

Type `/window` in its console to open a game window on the session and
`/close` to close it again; `--headless` runs without any window support.
`openac run --config <file>` runs several sessions from one configuration
document. [docs/building-and-running.md](docs/building-and-running.md) walks
through it, and [docs/launch-options.md](docs/launch-options.md) lists every
option and environment variable.

Before sending a change, run the same check CI runs:

```bash
scripts/check.sh                               # Linux, macOS
pwsh -NoProfile -File scripts/check.ps1        # Windows
```

It runs `cargo fmt --check`, `cargo clippy -D warnings`, a WebAssembly build
of the plugin crates, and the tests. Tests that read the real game data
files run too when the files are found.

## Plugins

Plugins are WebAssembly modules. You write one as a Rust crate against the
`openac-plugin` SDK (`crates/openac-plugin`, MIT), build it for
`wasm32-unknown-unknown`, and test its logic natively with the SDK's fake
host. A plugin declares what it needs beyond the basics (chat, network,
listening sockets, the shared profiles folder, the clipboard) in its
`plugin.json`, and the client enforces it: a plugin can call only what its
permissions allow, within a memory cap and a time limit per call.

`plugincheck` tells you whether the launcher would install your plugin, using
the launcher's own rules, and `plugincheck pack` writes its release assets.
Plugins are published as GitHub releases tagged `wasm-v<version>` and can be
listed in the launcher's Discover panel.
[docs/plugin-development.md](docs/plugin-development.md) walks the whole path.

## AI-assisted development

Like OpenAC, this is a heavily AI-assisted project, and it says so openly.
Most of the code was written by AI tools working under the maintainer's
direction and supervision. What every change has to pass is the same gate: it
is formatted, it passes clippy with warnings as errors, it passes the test
suite, and it behaves like the original game when the maintainer plays it.
The person who merges a change answers for it.

AI-assisted contributions are welcome on exactly those terms: submit changes
you understand, that pass the check, and that you can explain in the pull
request.

## Contributing

- Read [CONTRIBUTING.md](CONTRIBUTING.md) for the ground rules.
- File bugs and feature requests in
  [Issues](https://github.com/eriknihlen/openac-rust/issues).
- Fork it. The MIT license means you never need permission.

## Acknowledgements

openac-rust is a port of [OpenAC](https://github.com/eriknihlen/OpenAC) and
stands on the same work of the Asheron's Call preservation community:
[ACEmulator](https://github.com/ACEmulator/ACE) for the server and protocol,
[DatReaderWriter](https://github.com/Chorizite/DatReaderWriter),
[WorldBuilder](https://github.com/Chorizite/WorldBuilder) and
[Chorizite](https://github.com/Chorizite) for the data-file and rendering
foundation, [ACViewer](https://github.com/ACEmulator/ACViewer) for the viewer
that answered many rendering questions,
[holtburger](https://github.com/merklejerk/holtburger) for a working reference
of client behavior, and Virindi, whose VirindiTank defined what an Asheron's
Call automation plugin should be. Rendering goes through
[wgpu](https://wgpu.rs), windowing through
[winit](https://github.com/rust-windowing/winit), and plugins run on
[Wasmtime](https://wasmtime.dev).

## License

openac-rust is released under the [MIT License](LICENSE). Third-party notices
are in [NOTICE.md](NOTICE.md).

Asheron's Call and all associated names, art, and data files are the property
of their respective owners. This project distributes no game assets and is not
affiliated with Microsoft, Turbine, or Warner Bros. Entertainment.

<p align="center">From Sweden with love 🇸🇪</p>
