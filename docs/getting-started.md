# Getting started

You need three things: a client, your own Asheron's Call data files, and a
server to connect to.

## 1. Download a client

Pick one. Both play the same game; OpenAC (C#) has had the most time in front
of players and has downloads for every platform.

### OpenAC (C#)

From the [latest OpenAC release](https://github.com/eriknihlen/OpenAC/releases/latest):

| Platform | Download | Then |
|---|---|---|
| Windows | `launcher-win-x64.zip` | Unzip it anywhere and run `acdream-launcher.exe`. |
| Linux | `launcher-linux-x64.zip` | Unzip it anywhere and run `./acdream-launcher`. |
| macOS (Apple silicon) | `launcher-osx-arm64.zip` | Unzip it, drag `OpenAC.app` to `~/Applications`, then open it in Finder. |
| macOS (Intel) | `launcher-osx-x64.zip` | The same as Apple silicon. |

The launcher installs the client, prepares your data files, and keeps itself
and the client up to date. The `client-*.zip` files are the bare client and
headless host for people who do not want the launcher.

### openac-rust (Rust)

From the [latest openac-rust release](https://github.com/eriknihlen/openac-rust/releases/latest):

| Platform | Download | Then |
|---|---|---|
| Windows | `openac-win-x64.zip` | Unzip it anywhere and run `openac-launcher.exe`. |
| Linux | `openac-linux-x64.zip` | Unzip it anywhere and run `./openac-launcher`. |
| macOS (Apple silicon) | none yet | Build it from source; see [Building openac-rust](https://github.com/eriknihlen/openac-rust/blob/main/docs/building-and-running.md). |

The zip holds both programs; the launcher starts the `openac` client beside it.

## 2. Your game data

Neither client ships any game data. You need the original data files:
`client_portal.dat`, `client_cell_1.dat`, `client_highres.dat` and
`client_local_English.dat`. Both launchers ask for their folder the first time
they run.

## 3. A server

Both clients speak [ACEmulator](https://github.com/ACEmulator/ACE)'s protocol.
Add your server and account in the launcher. A local ACE server works well for
trying things out.

## What your computer needs

- **OpenAC:** a GPU and driver with Vulkan. On macOS the launcher brings its
  own Vulkan layer; nothing to install.
- **openac-rust:** a GPU and driver with Direct3D 12 (Windows) or Vulkan
  (Windows or Linux). On macOS it draws through Metal.
- On Linux, an X11 or Wayland desktop.

## Problems?

Ask in [Discord](https://discord.gg/mBWtvgmuF), or open an issue for
[OpenAC](https://github.com/eriknihlen/OpenAC/issues) or
[openac-rust](https://github.com/eriknihlen/openac-rust/issues) with your
version, what you did, what happened, and a screenshot if you can.
