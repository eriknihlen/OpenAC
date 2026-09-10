<p align="center">
  <img src="assets/icons/acdream-client-128.png" alt="OpenAC" width="96" height="96">
</p>

<h1 align="center">OpenAC</h1>

<p align="center">
  An open-source Asheron's Call client for .NET 10.<br>
  <strong>The original game's behavior, in a codebase built to be worked on.</strong>
</p>

<p align="center">
  <a href="https://github.com/eriknihlen/OpenAC/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/eriknihlen/OpenAC/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/eriknihlen/OpenAC/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/eriknihlen/OpenAC?display_name=tag"></a>
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/github/license/eriknihlen/OpenAC"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4">
  <a href="https://github.com/eriknihlen/OpenAC/discussions"><img alt="Discussions" src="https://img.shields.io/github/discussions/eriknihlen/OpenAC"></a>
  <a href="https://discord.gg/mBWtvgmuF"><img alt="Join Discord" src="https://img.shields.io/badge/Discord-Join%20the%20community-5865F2?logo=discord&logoColor=white"></a>
</p>

## What is OpenAC

OpenAC is a from-scratch client for Asheron's Call that reproduces the
observable behavior of the original client in a C# codebase built to be read,
tested, and changed. It
renders with Vulkan through Silk.NET, talks to
[ACEmulator](https://github.com/ACEmulator/ACE) servers over the game's wire
protocol, and exposes a plugin API the original never had.

OpenAC does not ship any game data. You supply your own data files.

## Status

OpenAC is in **beta**: it is fully playable against an ACEmulator server, and
it is under active development. Expect bugs, plenty of them; the perfect is
the enemy of the good, and this is out so people can play it and report what
breaks. Today it does the following:

- Log in, create and select characters, enter the world, and log out cleanly.
- Stream the outdoor world, towns, buildings, cellars, and dungeons with the
  original terrain, scenery, lighting, sky, weather, and day/night cycle.
- Move, jump, and collide the way the original client did, including slopes,
  stairs, doorways, and portal travel.
- Melee, bows, crossbows, and the complete late-era spell catalog with
  components, enchantments, projectiles, and effects.
- Inventory, equipment, containers, corpse looting, vendors, secure trade, item use,
  assessment, and the familiar quick bars.
- Chat with the original channels, colors, and the slash commands.
- The original UI: vitals, options, character, spellbook, fellowship and
  allegiance panels, radar, dialogs, and floating windows.
- A headless host that runs the same game runtime without a window, for bots
  and automated testing, on Windows and Linux.
- Plugins, with a bundled example that reproduces a familiar automation tool.

**Found a bug or missing feature?** Please report it in
[Discord](https://discord.gg/mBWtvgmuF) or open a
[GitHub issue](https://github.com/eriknihlen/OpenAC/issues). Include your OpenAC
version, what you did, what happened, and a screenshot if you can. Reports from
players help us decide what to fix next.

**Platforms.** Windows and Linux, 64-bit, for the launcher, the graphical
client, and the headless host. Windows is where most of the play-testing
happens; the Linux client is newer and has had less time in front of players,
so reports from Linux desktops are especially welcome. macOS on Apple
silicon runs the graphical client, built from source; the launcher does not
support it yet.

## Roadmap

- **Linux in CI.** The Linux graphical client builds, installs through the
  launcher, and plays, but CI runs its rendering tests on Windows hardware
  only. A Linux rendering lane is next.
- **macOS in the launcher and CI.** The graphical client builds, renders
  through MoltenVK, and plays on Apple silicon. The launcher does not run
  there yet, and no CI lane covers it.
- **Performance tuning.** Improve frame times, world streaming, memory use,
  and responsiveness.
- **Housing.**
- **A documented plugin API.**

## Download

Grab the launcher for your platform from the
[latest release](https://github.com/eriknihlen/OpenAC/releases/latest), unzip
it anywhere, and run it:

| Platform | Download | Run |
|---|---|---|
| Windows | `launcher-win-x64.zip` | `acdream-launcher.exe` |
| Linux | `launcher-linux-x64.zip` | `./acdream-launcher` |

The launcher installs the client, prepares your data files, keeps itself and
the client up to date, and stores your server and character profiles.

You will need:

- **Windows 10 or 11**, or a **64-bit Linux desktop** (X11 or Wayland), with a
  **Vulkan 1.3** capable GPU and driver.
- **Your own Asheron's Call data files** (`client_portal.dat`,
  `client_cell_1.dat`, `client_highres.dat`, `client_local_English.dat`).
- **A server to connect to.** OpenAC speaks ACEmulator's protocol; a local
  ACE server works well for trying it out.

## Build from source

Requires the .NET 10 SDK (the exact band is pinned in `global.json`).

```bash
git clone https://github.com/eriknihlen/OpenAC.git
cd OpenAC
dotnet build AcDream.slnx -c Release
dotnet test AcDream.slnx -c Release --no-build --filter "Lane!=InstalledDat&Lane!=PreparedPackage&Lane!=Live&Lane!=Manual&Lane!=Timing&Lane!=Windows&Lane!=Linux&Lane!=Vulkan&Lane!=SystemFont&Purpose!=Diagnostic&Status!=KnownFailure"
```

Then see [docs/building-and-running.md](docs/building-and-running.md) for
preparing your data files and launching the client or the headless host
against a server.

## Plugins

Plugins are .NET assemblies that target `AcDream.Plugin.Abstractions`, a
small BCL-only contract: game state, events, commands, and a markup-based UI
panel system that renders in the game's own look.

**The plugin API is not documented yet.** That is an open to-do; until it
lands, [docs/plugin-ui-markup.md](docs/plugin-ui-markup.md) covers the panel
markup and the interfaces in `src/AcDream.Plugin.Abstractions` are the
reference, with `src/AcDream.Plugins.MossTank` as the worked example.

**MossTank** is the bundled plugin: a re-implementation of VirindiTank, the
automation plugin most Asheron's Call players ran for years. It reads
VirindiTank's own profile and navigation files so existing setups carry over,
and it aims at the same tabs, the same behavior, and the same vocabulary.
Full credit to Virindi for the original; MossTank exists because that design
was right. **What ships here is a proof of concept. It is not working yet
and is not expected to;** most of the real work lives on another branch and
lands when it is ready.

**Custom shader packs** are an experiment. The render packs under `samples/`
are plugins that swap in their own shader stages (an atmospheric tier, a
shadows-only tier, a no-op pack), and they exist so people can play with the
rendering pipeline without touching the client. Try them, break them, and
say what you found. They can be enabled in game under the graphics settings.

## AI-assisted development

This is a heavily AI-assisted project, and it says so openly. Most of the code
was written by AI tools working under the maintainer's direction and supervision.
Not every line has been read by a human. What every change has to pass instead is
the same gate: it builds with warnings as errors, it passes the test suite (about
18,000 tests in CI), it survives an independent AI review pass, and it behaves
like the original game when the maintainer plays it. The AI is a tool here, not
the author of record; the person who merges a change answers for it.

The project welcomes AI-assisted contributions on exactly those terms. Use whatever
tools you like; submit changes you understand, that build, that pass the tests,
and that you can explain in the pull request.

## Contributing

OpenAC is meant to be built by the community, not by one person. The code is
shared so that anyone who cares about Asheron's Call can read it, run it,
change it, and ship it. Contributions of every size are wanted: a typo, a bug
report with a screenshot, a plugin, a port to a new platform, or a whole
subsystem. If you have played the game and something feels wrong, that
observation alone is useful.

- Read [CONTRIBUTING.md](CONTRIBUTING.md) for the ground rules and how to get
  a build running.
- Join [Discord](https://discord.gg/mBWtvgmuF) to report bugs, share screenshots,
  and discuss what you find while playing. You do not need a GitHub account to
  help us improve the client.
- Ask questions and float ideas in
  [Discussions](https://github.com/eriknihlen/OpenAC/discussions).
- File bugs and feature requests in
  [Issues](https://github.com/eriknihlen/OpenAC/issues).
- Fork it. The MIT license means you never need permission.

## Acknowledgements

OpenAC stands on the work of the Asheron's Call preservation community:
[ACEmulator](https://github.com/ACEmulator/ACE) for the server and protocol,
[WorldBuilder](https://github.com/Chorizite/WorldBuilder) and
[Chorizite](https://github.com/Chorizite) for the data-file and rendering
foundation this project extracted and adapted,
[ACViewer](https://github.com/ACEmulator/ACViewer) for the viewer that
answered many rendering questions, and
[holtburger](https://github.com/merklejerk/holtburger) for a working
reference of client behavior, and Virindi, whose VirindiTank defined what an
Asheron's Call automation plugin should be. Rendering, windowing, and audio go
through [Silk.NET](https://github.com/dotnet/Silk.NET).

## License

OpenAC is released under the [MIT License](LICENSE). Third-party notices are
in [NOTICE.md](NOTICE.md).

Asheron's Call and all associated names, art, and data files are the property
of their respective owners. This project distributes no game assets and is not
affiliated with Microsoft, Turbine, or Warner Bros. Entertainment.

<p align="center">From Sweden with love 🇸🇪</p>
