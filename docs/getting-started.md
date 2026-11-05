# Getting started

You need three things: the client, your own Asheron's Call data files, and a
server to connect to.

## 1. Download the client

From the [latest release](https://github.com/eriknihlen/OpenAC/releases/latest):

| Platform | Download | Then |
|---|---|---|
| Windows | `openac-win-x64.zip` | Unzip it anywhere and run `openac-launcher.exe`. |
| Linux | `openac-linux-x64.zip` | Unzip it anywhere and run `./openac-launcher`. |
| macOS (Apple silicon) | `OpenAC-<version>-osx-arm64.zip` | Unzip it, drag `OpenAC.app` to Applications, then open it. |
| macOS (Intel) | `OpenAC-<version>-osx-x64.zip` | The same as Apple silicon. |

The zip holds both programs, the launcher and the `openac` client; the
launcher starts the client beside it. On macOS both are inside `OpenAC.app`.
The app is signed ad hoc, not with an Apple Developer ID, so the first time
you open it macOS blocks it: open System Settings → Privacy & Security and
click Open Anyway.

The launcher keeps your accounts and servers, starts sessions with a window
or headless (without one), installs plugins, and updates itself and the
client. The `client-*.zip` and `launcher-*.zip` files in a release are the
launcher's own update payloads; players take the files above.

**Coming from the C# client?** It is retired, and its launcher does not
install this one: download it from the table above. **Coming from an earlier
release of this client** (published as openac-rust)? A launcher from 0.1.12 on
moves over by itself; an older one updates to 0.1.12 first, then moves.

## 2. Your game data

The client ships no game data. You need the original data files:
`client_portal.dat`, `client_cell_1.dat`, `client_highres.dat` and
`client_local_English.dat`. The launcher asks for their folder the first time
it runs.

## 3. A server

The client speaks [ACEmulator](https://github.com/ACEmulator/ACE)'s protocol.
Add your server and account in the launcher. A local ACE server works well for
trying things out.

## What your computer needs

- A GPU and driver with Direct3D 12 (Windows) or Vulkan (Windows or Linux).
  On macOS (14 or later, Apple silicon or Intel) it draws through Metal.
- On Linux, an X11 or Wayland desktop.

## Problems?

Ask in [Discord](https://discord.gg/mBWtvgmuF), or open an
[issue](https://github.com/eriknihlen/OpenAC/issues) with your version, what
you did, what happened, and a screenshot if you can.
