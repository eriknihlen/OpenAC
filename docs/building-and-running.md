# Building and running

## Prerequisites

- **Rust**, installed with [rustup](https://rustup.rs). `rust-toolchain.toml`
  pins Rust 1.95.0 with `rustfmt`, `clippy` and the `wasm32-unknown-unknown`
  target; rustup installs them the first time you build in the repository
  (`rustup toolchain install` does it explicitly).
- **A C compiler.** Everything is Rust except two small C files the plugin
  engine (wasmtime) compiles, and on Linux the OpenSSL bindings run the C
  preprocessor over the system headers. On Windows install the Visual Studio
  Build Tools with "Desktop development with C++"; on Linux your
  distribution's compiler.
- **Linux only:** `pkg-config` and the OpenSSL and ALSA development headers.
  On Debian or Ubuntu:

  ```bash
  sudo apt install build-essential pkg-config libssl-dev libasound2-dev
  ```

  The window and clipboard libraries (X11, Wayland, xkbcommon) are loaded
  when the program runs, so building needs nothing for them.
- **A GPU** with Direct3D 12 (Windows) or Vulkan (Windows or Linux) to open a
  game window. Headless sessions need none.
- **Your own Asheron's Call data files**: `client_portal.dat`,
  `client_cell_1.dat`, `client_highres.dat` and `client_local_English.dat`,
  in one folder. Nothing in this repository contains game data.
- **A server.** openac-rust speaks the protocol of
  [ACEmulator](https://github.com/ACEmulator/ACE) servers; a local ACE server
  is the easiest way to try it.

## Build and test

```bash
git clone https://github.com/eriknihlen/openac-rust.git
cd openac-rust
cargo build --release
```

This builds every program into `target/release/`:

| Program | What |
|---|---|
| `openac` | the client: one or more game sessions, with or without windows |
| `openac-launcher` | the launcher window |
| `actool` | inspects the game's data files (`actool dat info`, ...) |
| `plugincheck` | checks and packs WebAssembly plugins |

The project's gate is the check script, which CI runs on Windows and Linux:

```bash
scripts/check.sh                          # Linux, macOS
pwsh -NoProfile -File scripts/check.ps1   # Windows
```

It runs `cargo fmt --check`, `cargo clippy` with warnings as errors, a
WebAssembly build of `openac-plugin-compat`, and `cargo test --workspace`.
Tests that need the real data files are `#[ignore]`d; the script runs them
too (`--include-ignored`) when it finds `client_portal.dat` in the data
folder below.

## Where the data files are read

`openac` reads the data files from, in order:

1. `--dat-dir <path>`,
2. the `OPENAC_DAT_DIR` environment variable,
3. `Documents\Asheron's Call` under your user profile
   (`$HOME/Documents/Asheron's Call` on Linux).

The launcher asks for the folder on its first run and passes it to every
client it starts.

## Run the launcher

```bash
./target/release/openac-launcher
```

The launcher keeps your accounts, servers and characters, starts each
session graphical or headless, shows its console, and installs plugins.
Until a client is installed through its update feed, it starts the `openac`
in its own folder, so build both (`cargo build --release` does).

Its options (another install folder, a test update feed, a plugin list) are
listed in the `openac-launcher` section of [launch-options.md](launch-options.md).

## Run one session

```bash
export OPENAC_PASSWORD='...'              # PowerShell: $env:OPENAC_PASSWORD = '...'
./target/release/openac --server 127.0.0.1:9000 --account myaccount --character "My Character"
```

The password is read only from `OPENAC_PASSWORD`, never from the command
line. Without `--character` the session waits at character selection; type
`/enter <name>` to enter the world.

The session runs in the terminal. Lines you type that are not console
commands are chat (`/say`, `/tell <name>, <text>`, `/r`, channels, emotes).
`/window` opens a game window on the session and `/close` closes it again;
the session plays on either way, and nothing reconnects. Ctrl+C logs the
character off and exits.

Useful options:

| Option | Meaning |
|---|---|
| `--headless` | no window support at all (no display needed) |
| `--server <host:port>` | the login server (default `127.0.0.1:9000`) |
| `--duration <seconds>` | log off after this long in the world |
| `--log <level>` | `error`, `warn`, `info` (default), `debug`, `trace` |
| `--dat-dir <path>` | the data-file folder |

`openac --help` prints the rest, and [launch-options.md](launch-options.md)
lists every option and environment variable.

## Run several sessions

`openac run` runs every session in a configuration document, each on its own
thread, in one process:

```json
{
  "version": 1,
  "sessions": [
    {
      "id": "main",
      "endpoint": { "host": "127.0.0.1", "port": 9000 },
      "account": "myaccount",
      "character": { "name": "My Character" },
      "policy": { "id": "idle" },
      "credential": { "provider": "environment", "reference": "OPENAC_MAIN_PASSWORD" }
    }
  ]
}
```

```bash
./target/release/openac validate --config sessions.json   # check it without connecting
./target/release/openac run --config sessions.json --console
```

With `--console` you type into the sessions: `/sessions` lists them,
`/session <id>` picks the one unaddressed lines go to, `@<id> <line>` sends
one line to one session, `/window [id]` opens or moves the game window to a
session, `/close` closes it, and `/quit [id]` logs off. The document's full
grammar (paths, content, credentials, status files) is in
the session-config section of [launch-options.md](launch-options.md).

## Where files go

Everything the programs write lives under one install folder:
`%LOCALAPPDATA%\openac-rust` on Windows, `$XDG_DATA_HOME/openac-rust`
(`~/.local/share/openac-rust`) on Linux. `OPENAC_ROOT_DIR` or `--root-dir`
names another. Inside it: `settings/` (launcher profiles, display settings),
`plugins/` (installed plugins and their saved files), `logs/` (session logs,
chat logs, crash reports) and `cache/`.

## Linux notes

The window runs on X11 or Wayland (`OPENAC_DISPLAY_PROTOCOL` picks one when
both are available). Sound goes through ALSA; set `OPENAC_NO_AUDIO=1` to
run without it. The launcher's HTTPS uses the system OpenSSL.
