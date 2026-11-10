# Contributing to OpenAC

Thanks for your interest. OpenAC exists to be worked on together: pull
requests, bug reports, questions, plugins, and platform ports are all wanted,
and nobody needs to ask before starting. OpenAC is MIT-licensed: you may
fork it, ship it, or build something else on it without asking either.

## Ground rules

- **Be kind.** The [code of conduct](CODE_OF_CONDUCT.md) applies everywhere
  the project talks.
- **One topic per pull request.** A fix and an unrelated refactor are two PRs.
- **Green check.** `scripts/check.sh` (or `scripts/check.ps1` on Windows)
  passes before you open the pull request; CI runs the same script.
- **The behavior is the original game's.** OpenAC is the Rust port of
  the retired C# client (on the
  [`deprecated` branch](https://github.com/eriknihlen/OpenAC/tree/deprecated))
  and reproduces what the original client observably did. If you change how
  the client behaves in the world, say in the PR what the original client
  does and how you know.

## Getting set up

Install Rust with [rustup](https://rustup.rs); `rust-toolchain.toml` pins the
toolchain (Rust 1.95.0 with `rustfmt`, `clippy` and the
`wasm32-unknown-unknown` target). See
[content/docs/building-and-running.md](content/docs/building-and-running.md) for the
platform prerequisites. Short version:

```bash
git clone https://github.com/eriknihlen/OpenAC.git
cd OpenAC
scripts/check.sh                          # or: pwsh -NoProfile -File scripts/check.ps1
```

The check script is the gate. It runs, in order:

1. `cargo fmt --all --check`
2. `cargo clippy --workspace --all-targets -- -D warnings`
3. `cargo build -p openac-plugin-compat --target wasm32-unknown-unknown`
   (the crates plugins link must build for WebAssembly)
4. `cargo test --workspace`

Tests that read the real game data files are marked `#[ignore]`. When the
script finds `client_portal.dat` in `OPENAC_DAT_DIR` (default:
`Documents/Asheron's Call` under your user profile), it runs them too
(`cargo test --workspace -- --include-ignored`). CI has no game data, so run
them locally if your change touches data files, rendering or the interface.

## Where things live

The workspace is split into crates under `crates/`:

| Crates | What |
|---|---|
| `ac-bits`, `ac-dat`, `ac-proto`, `ac-net` | binary reading, the game's data files, typed messages, the UDP transport (sans-I/O) |
| `ac-core`, `ac-motion`, `ac-physics`, `ac-nav`, `ac-vfx` | pure game logic: rules, animation, collision and movement, navigation, effects |
| `ac-runtime` | the one owner of game and session state; headless and graphical alike |
| `ac-render`, `ac-ui`, `ac-audio` | presentation: the window and GPU renderer, the original interface, sound |
| `ac-host-core`, `ac-host` | configuration, paths, the session-config document (`ac-host-core`: the part the launcher shares) |
| `ac-plugin-abi`, `ac-plugin`, `ac-plugin-ui`, `ac-plugin-net` | the WebAssembly plugin host and its calling convention |
| `ac-plugin-version`, `ac-plugin-manifest`, `ac-plugin-icon` | the API version type, the client's `plugin-api.json` and the permissions, the `plugin.json` parser, the icon rules: shared by the client and the launcher (which API versions the client loads is in `ac-plugin-abi`, which the launcher does not link) |
| `openac-plugin`, `openac-plugin-compat` | the plugin SDK and its compatibility kit (MIT) |
| `ac-launcher-core`, `ac-launcher-net`, `ac-proxy` | the launcher's logic, its HTTPS, the system proxy |
| `openac`, `openac-launcher`, `actool`, `plugincheck` | the programs |

A few rules keep the design honest:

- The runtime owns all game and session state and never depends on a window,
  GPU or audio device. Presentation holds only what it can rebuild from the
  runtime, so a window can attach to a session and detach again.
- Input reaches the runtime as commands, never as direct mutation.
- One thread per session; networking is a state machine fed bytes and time.
- No `unsafe` outside the crates that need it (GPU and platform glue), and
  each `unsafe` block carries a `// SAFETY:` comment.
- Parsers return typed errors and never panic on malformed input.
- Startup options come in through one options struct and are documented in
  [content/docs/launch-options.md](content/docs/launch-options.md) in the same commit.
- Tests live beside the crate they test, and golden values name their source.

## Faithfulness and provenance

Game-specific behavior (physics, movement, animation, the wire protocol, UI
layout) reproduces what the original client observably did. The retired C#
client (the `deprecated` branch) is the reference for this port: when behavior
is in question, compare with it and with the original client. A contributor
who cannot verify a change against the original client should describe the
observed behavior instead and mark the PR as needing verification. Do not add comments that describe
the original client's internals; describe what the code does and why.

Do not paste code from other Asheron's Call projects. ACEmulator and
holtburger are AGPL-3.0 and ACViewer is GPL-3.0; their code cannot be
included in an MIT project. Reading them to understand a message layout is
fine; copying is not. New dependencies need a reason in the PR, and their
license must be compatible with MIT distribution (see [NOTICE.md](NOTICE.md)).

## The plugin ABI

`ac-plugin-abi` defines the calling convention between the client and its
WebAssembly plugins. A built plugin carries a fingerprint of the record
layouts it was compiled against, and the client refuses a plugin whose
layouts differ. So:

- **A record never changes shape silently.** Changing a record, reply, event
  or import in `ac-plugin-abi` changes the layout fingerprint
  (`ac_plugin_abi::layout`). Update the pinned value in its tests, and say in
  the pull request that plugins must be rebuilt.
- **Versioned additions keep older plugins loading.** A new event kind, or a
  new field at the end of a record, added in a new API minor version is
  marked with that version (`since (MAJOR, MINOR)` in `wire_union!` and
  `wire_record!`) and `API_CURRENT` (in `ac-plugin-abi`) moves to it. A
  plugin built for an older version is compared with that version's shapes,
  is never sent the new event, and is sent the record without the new field,
  so it keeps loading unrebuilt; pin the older version's shapes in the layout
  tests.
- **Additive where possible.** A new capability is a new import or event
  with an inert answer when the client has nothing to say, so plugins that do
  not use it are unaffected.
- **Tested through the host.** A new import gets a test that reaches it
  through a real plugin module, not only the encoding.

## AI-assisted contributions

This project is built with heavy AI assistance and welcomes contributions made
the same way. The bar is the same for every change regardless of how it was
written: the check passes, the behavior matches the original game where that
applies, and you can explain what it does and why.

## Reporting bugs

Use the bug template. The most useful report has: the version, what you did,
what you expected, what happened, and the log if there is one (the launcher's
**Log** view, or the `logs/` folder of the install). A screenshot of the
original client doing it right is gold.

## Writing a plugin instead

Most people who want to extend OpenAC do not need to change the client
at all. A plugin is a Rust crate in its own repository, built against the
`openac-plugin` SDK, and it installs through the launcher.
**[content/docs/plugin-development.md](content/docs/plugin-development.md)** is the full
path.
