# Getting started

You need three things: the launcher, your own Asheron's Call data files, and
a server to connect to.

## 1. Download the launcher

Download the launcher; it fetches the game client for you. From the
[latest release](https://github.com/eriknihlen/OpenAC/releases/latest):

| Platform | Download | Then |
|---|---|---|
| Windows | `launcher-win-x64.zip` | Unzip it anywhere and run `openac-launcher.exe`. |
| Linux | `launcher-linux-x64.zip` | Unzip it anywhere and run `./openac-launcher`. |
| macOS (Apple silicon) | `OpenAC-<version>-osx-arm64.zip` | Unzip it, drag `OpenAC.app` to Applications, then open it. |
| macOS (Intel) | `OpenAC-<version>-osx-x64.zip` | The same as Apple silicon. |

On first start the launcher asks for your game data folder (step 2), then
offers the game client: the banner says "A new version of the game is
available"; click **Review update** and it installs the client. From then on
it keeps itself and the client up to date. On macOS the launcher and the
client come together in `OpenAC.app`. The app is signed ad hoc, not with an
Apple Developer ID, so the first time you open it macOS blocks it: open
System Settings → Privacy & Security and click Open Anyway.

The launcher keeps your accounts, characters and servers, starts sessions
with a window or headless (without one), keeps checked clients running, runs
each character's logon commands, and installs plugins. (The
`openac-<platform>.zip` files hold the launcher and the client together, for
setting up without a connection; the `client-*.zip` files are the launcher's
own downloads.)

**Coming from an earlier release** (published as openac-rust)? A launcher
from 0.1.12 on moves over by itself; an older one updates to 0.1.12 first,
then moves.

## Bring your setup with you

- **From the Thwarg launcher:** **Import from Thwarg launcher…** (beside
  Logon commands) reads Thwarg's folder and adds your servers, accounts and
  passwords, characters, profiles and each character's logon commands. It
  shows what it will add before it changes anything, and never overwrites an
  account you already have.
- **From VirindiTank and UtilityBelt:** once the MossTank plugin is
  installed, **Import VTank & UB…** copies your VirindiTank and UtilityBelt
  settings and profiles (metas, routes, loot profiles) into the folder
  MossTank reads; your originals are only read.

## 2. Your game data

The client ships no game data. You need the original data files:
`client_portal.dat`, `client_cell_1.dat` and `client_local_English.dat`, and
`client_highres.dat` if you have it (optional: its sharper textures are used
when it is there). The launcher asks for their folder the first time it runs.

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
