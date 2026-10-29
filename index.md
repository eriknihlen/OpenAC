# OpenAC

Open-source clients for Asheron's Call: the original game's behavior, in code
built to be read, tested and changed. There are two clients, maintained side by
side and kept in step, both MIT licensed:

| | **OpenAC** (C#) | **openac-rust** (Rust) |
|---|---|---|
| Latest release | [![OpenAC release](https://img.shields.io/github/v/release/eriknihlen/OpenAC?display_name=tag&label=)](https://github.com/eriknihlen/OpenAC/releases/latest) | [![openac-rust release](https://img.shields.io/github/v/release/eriknihlen/openac-rust?display_name=tag&label=)](https://github.com/eriknihlen/openac-rust/releases/latest) |
| Built with | .NET 10, Vulkan | Rust 1.95, wgpu (Direct3D 12, Vulkan, Metal) |
| Downloads for | Windows, Linux, macOS (Apple silicon and Intel) | Windows, Linux; macOS from source |
| Plugins | .NET plugins | WebAssembly plugins |
| Headless host | yes | yes, the same program with or without a window |
| Source | [github.com/eriknihlen/OpenAC](https://github.com/eriknihlen/OpenAC) | [github.com/eriknihlen/openac-rust](https://github.com/eriknihlen/openac-rust) |

Both talk to [ACEmulator](https://github.com/ACEmulator/ACE) servers. Neither
ships any game data: you supply your own data files.

## Playing

- **[Getting started](docs/getting-started.md)**: download a launcher, point it
  at your game data, and connect to a server.
- **[What's new](docs/whats-new.md)**: the latest releases of both clients and
  who made them.
- **[The Rust client](docs/rust-client.md)**: what openac-rust is and how it
  differs from OpenAC.
- **[Launcher](docs/launcher.md)**: accounts, sessions, updates and plugins in
  the OpenAC launcher.

## Writing plugins

- **[Writing a plugin](docs/plugin-development.md)**: project setup, the
  manifest, where plugins install, what the API promises.
- **[Plugin API](docs/plugin-api.md)**: every surface a plugin can use, by
  topic, with examples.
- **[Plugin API reference](api/index.md)**: every public type and member,
  generated from the source.
- **[Navigation](docs/navigation.md)**: walks the client plans and drives for
  a plugin, and who is driving when two plugins want the character.
- **[Rust plugins](https://github.com/eriknihlen/openac-rust/blob/main/docs/plugin-development.md)**:
  WebAssembly plugins for openac-rust.

## Working on the clients

- **[Building and running](docs/building-and-running.md)** and
  **[Architecture](docs/architecture.md)** for the C# client.
- **[Building openac-rust](https://github.com/eriknihlen/openac-rust/blob/main/docs/building-and-running.md)**
  for the Rust client.

Found a bug? Tell us in [Discord](https://discord.gg/mBWtvgmuF) or open an
issue for [OpenAC](https://github.com/eriknihlen/OpenAC/issues) or
[openac-rust](https://github.com/eriknihlen/openac-rust/issues).
