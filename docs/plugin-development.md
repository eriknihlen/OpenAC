# Writing a plugin

This is the practical guide: how to set up a plugin project, what the host
expects from it, where it installs, and how the API is allowed to change.
The API itself is described in [plugin-api.md](plugin-api.md) (game state,
events, items, chat, trade, hotkeys, window, headless) and
[plugin-ui-markup.md](plugin-ui-markup.md) (in-game panels).

## The one rule

A plugin references `AcDream.Plugin.Abstractions` and nothing else from
OpenAC. Not `AcDream.App`, not `AcDream.Runtime`, not `AcDream.Core`. The
abstractions project is a small contract with no dependencies beyond the
.NET base library; everything a plugin can see or do goes through it. That
is what lets the same plugin run in the graphical client and in the
headless host, and what lets the client change underneath without breaking
you.

## Project setup

A plugin is a .NET 10 class library in its own repository. Reference the
contract so that it is compiled against but not copied next to your
assembly; the host already ships it:

```xml
<ItemGroup>
  <ProjectReference Include="$(OpenAcRoot)src\AcDream.Plugin.Abstractions\AcDream.Plugin.Abstractions.csproj">
    <Private>false</Private>
    <ExcludeAssets>runtime</ExcludeAssets>
  </ProjectReference>
</ItemGroup>
```

`OpenAcRoot` points at a checkout of this repository. Give it a default in
your `Directory.Build.props` and let people override it on the command line
with `-p:OpenAcRoot=<path>`. A published contract package will replace the
checkout reference; until then, build against the tagged release you target.

Put a `plugin.json` next to your project and copy it to the output
directory:

```json
{
  "id": "yourname.yourplugin",
  "displayName": "Your Plugin",
  "version": "0.1.0",
  "entryDll": "YourPlugin.dll",
  "apiVersion": 1,
  "kinds": ["Gameplay"]
}
```

`id` is the stable identity the host uses for storage, settings and
commands; do not rename it after release. `kinds` is `Gameplay` for a normal
plugin and `RenderPack` for a shader pack. Markup files for panels ship the
same way, as content copied to the output directory.

## The entry point

Implement `IAcDreamPlugin` on one public class with a public parameterless
constructor. The host creates it with no arguments, then calls:

- `Initialize(IPluginHost host)` once, before the character is in the
  world. Keep the host; everything is reached through it.
- `Enable()` when the plugin is switched on and `Disable()` when it is
  switched off or the client shuts down. Undo in `Disable` what you did in
  `Enable`: unsubscribe events, release hotkeys, stop timers.

`IPluginHost` gives you `State`, `Events`, `Commands`, `Storage`, `Log`,
`Ui`, `Window`, `Clipboard`, `Hotkeys` and `Automation`. `Automation` is the
large surface: character, items, spells, combat, world objects, trade,
vendor, navigation, fellowship, login. Check `IsAvailable` on a surface
before relying on it; a host that cannot provide something returns an inert
value rather than throwing. Walks the client plans for you, and the rule that
one plugin drives the character at a time, are in [navigation.md](navigation.md).

## Installing and running

The host loads every immediate subdirectory of its plugins folder that
contains a `plugin.json`:

| Platform | Plugins folder |
|---|---|
| Windows | `%LOCALAPPDATA%\acdream\plugins` |
| macOS | `~/Library/Application Support/acdream/plugins` |
| Linux | `$XDG_DATA_HOME/acdream/plugins` (default `~/.local/share/acdream/plugins`) |

`ACDREAM_DATA_DIR` moves the data directory, and the plugins folder with
it. Copy your assembly, its `.deps.json`, `plugin.json` and any markup into
one subdirectory, then start the client. Plugin log lines go to the
client's log with your plugin id as the prefix.

For a bot or a test that needs no window, the headless host
(`acdream-headless`) loads the same plugin folder and exposes the same
`Automation` surface; see the "Headless" section of `plugin-api.md` for the
few things that differ (no UI, no window, remote positions from the latest
server update).

## What you can rely on

- **Additive changes only.** New capabilities arrive as new members with
  default implementations, so a plugin built against an older contract
  keeps compiling and keeps loading. A member that is unavailable on a host
  returns `false`, `Unavailable` or an empty value.
- **Every public member is documented** from its XML comment; an
  undocumented member fails the client's build. Your IDE shows the same
  text.
- **Commands are per plugin.** Register verbs through `host.Commands`; the
  host refuses a verb another loaded plugin already owns, so pick a prefix
  that is yours (`/mt`, `/vt`, `/drakbot` are taken).
- **One bot drives the character at a time.** Two plugins can be installed
  together; when both want to move, fight or use items, expect the host to
  refuse the second request rather than interleave them.

## Contributing to the API

If your plugin needs something the contract does not expose, open an issue
or a pull request against OpenAC rather than reaching past the contract.
The rules for an API change are in [CONTRIBUTING.md](https://github.com/eriknihlen/OpenAC/blob/main/CONTRIBUTING.md):
one implementation per operation, bound on both hosts, documented, tested,
and additive.

## Examples

- [OpenAC-MagTools](https://github.com/eriknihlen/OpenAC-MagTools): an
  external plugin in its own repository, built and installed exactly as
  described above.
- `src/AcDream.Plugins.MossTank` in this repository: the bundled example,
  which uses the panel markup heavily.
- `samples/`: render packs, the `RenderPack` kind.
