# OpenAC

An open-source client for Asheron's Call, written in Rust: the original game's
behavior, in code built to be read, tested and changed. MIT licensed.

[![Latest release](https://img.shields.io/github/v/release/eriknihlen/OpenAC?display_name=tag)](https://github.com/eriknihlen/OpenAC/releases/latest)

- **Plays on** Windows, Linux and macOS (Apple silicon and Intel).
- **One program, many sessions:** `openac` runs any number of game sessions,
  each with or without a window.
- **Draws** through [wgpu](https://wgpu.rs): Direct3D 12, Vulkan or Metal.
- **Plugins are WebAssembly:** sandboxed, the same with or without a window.
- **A launcher** keeps your accounts and servers, starts sessions, installs
  plugins and updates itself.

It talks to [ACEmulator](https://github.com/ACEmulator/ACE) servers. It ships
no game data: you supply your own data files.

## Playing

- **[Getting started](docs/getting-started.md)**: download the launcher, point
  it at your game data, and connect to a server.
- **[What's new](docs/whats-new.md)**: the latest releases and who made them.
- **[The client](docs/rust-client.md)**: what it is, its platforms, and its
  plugin API versions.

## Writing plugins

- **[Writing a plugin](docs/plugin-development.md)**: project setup, the SDK,
  `plugin.json`, testing, publishing and getting listed.

## Working on the client

- **[Building and running](docs/building-and-running.md)**: building from
  source, running the launcher and the client.
- **[Launch options](docs/launch-options.md)**: every option and environment
  variable.

Found a bug? Tell us in [Discord](https://discord.gg/mBWtvgmuF) or open an
[issue](https://github.com/eriknihlen/OpenAC/issues).
