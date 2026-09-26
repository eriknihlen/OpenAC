# Writing a plugin

This is the practical guide: how to set up a plugin project, what the host
expects from it, where it installs, and how the API is allowed to change.
The API itself is described in [plugin-api.md](plugin-api.md) (game state,
events, items, chat, trade, hotkeys, window, headless) and
[plugin-ui-markup.md](plugin-ui-markup.md) (in-game panels).

Getting a plugin into other players' hands has three stages, and this page
follows them in order:

1. **Build it** — project setup, the entry point, and what the API promises.
2. **Test it** — run it from your own plugins folder, then check it against
   the launcher's rules before anyone else sees it.
3. **Publish and list it** — a GitHub release the launcher can install, and
   an entry in the plugin list so players can find it.

The exact field-by-field rules live in
[plugin-manifest.md](plugin-manifest.md). This page links to them rather than
repeating them, so there is one place a rule can change.

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
with `-p:OpenAcRoot=<path>`.

### Or against the contract package

`AcDream.Plugin.Abstractions` is packable, so a plugin can build against a
version of the contract instead of against a checkout. That is the reference a
plugin in its own repository wants: it pins the contract version the plugin
targets, and it needs no client sources to build.

```xml
<ItemGroup>
  <PackageReference Include="AcDream.Plugin.Abstractions" Version="0.1.14"
                    ExcludeAssets="runtime" />
</ItemGroup>
```

`ExcludeAssets="runtime"` does for a package reference what `Private=false`
does for a project reference: compile against the contract, ship no copy of
it. The package carries the XML documentation, so your IDE shows the same text
the [API reference](plugin-api.md) does.

The package is not on a public feed. Every release from 0.1.13 on attaches it as an asset,
`AcDream.Plugin.Abstractions.<version>.nupkg`, with its SHA-256 beside it; put
the file in a folder and restore against that folder:

```
dotnet restore --source <folder>
```

Or produce one from a checkout, which is how you build against an unreleased
contract:

```
dotnet pack <OpenAcRoot>/src/AcDream.Plugin.Abstractions -c Release -o <feed>
dotnet restore --source <feed>
```

A plugin developed inside a checkout of this repository should be built the
package way at least once before it ships, so the reference it will actually
use is exercised rather than assumed;
[extracting a plugin](plugins/extracting-a-plugin.md) is the rest of that
checklist.

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
`Ui`, `Window`, `Clipboard`, `Hotkeys`, `WorldLines` and `Automation`.
`WorldLines` draws lines in the world (a route, say) in layers the plugin
owns; a host without a window hands out no layer. Only the parts of lines
within 250 m of the camera are drawn, and a frame draws a fixed amount of
them: past that, the lines nearest the camera are kept. `Automation` is the
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
| Windows | `%LOCALAPPDATA%\OpenAC\plugins` |
| macOS | `~/Library/Application Support/OpenAC/plugins` |
| Linux | `$XDG_DATA_HOME/openac/plugins` (default `~/.local/share/openac/plugins`) |

The plugins folder lives in the OpenAC install folder, which the launcher
settings show and can move; `ACDREAM_ROOT_DIR` (or `--root-dir`) names
another install folder for one run. What your plugin writes through
`Storage` lands in `plugins/<id>/files/`, inside its own folder: installs,
updates and removing the plugin's code never touch it, and a package that
ships its own top-level `files` folder is refused. Copy your assembly, its `.deps.json`, `plugin.json` and any markup into
one subdirectory, then start the client. Plugin log lines go to the
client's log with your plugin id as the prefix.

For a bot or a test that needs no window, the headless host
(`acdream-headless`) loads the same plugin folder and exposes the same
`Automation` surface; see the "Headless" section of `plugin-api.md` for the
few things that differ (no UI, no window, remote positions from the latest
server update).

### Reloading while the client runs

The client reads your assembly (and its `.pdb`, when it sits beside it, so
stack traces keep file and line numbers) into memory instead of running it
from the file, so nothing in your plugin's folder is held open. Rebuild
straight into the folder while the game runs: when your entry assembly or
`plugin.json` changes, the client waits until the folder has had no writes for
one second and then reloads your plugin. `/plugin reload <id>` reloads one
plugin on demand, `/plugin reload all` every plugin, in the chat box and on a
headless session's console alike. The same happens when the launcher updates
your plugin while a game is running.

A reload switches the running copy off (`Disable`), releases everything the
host handed it (panels, commands, hotkeys, event handlers, chat filters,
images, world lines, status lines), unloads its assemblies, then creates the
new copy and calls `Initialize` and `Enable` as at startup.
`IPluginHost.IsHotReload` is true for that new copy. If the character is
already in the world, the new copy's `LoginComplete` handlers are called right
after `Enable`, so a plugin that sets up on login needs nothing extra.

Before the running copy is touched, the client reads the new `plugin.json`
and loads the new assembly. A new copy with a different `id`, a newer
`apiVersion` or a `minHostVersion` this client does not meet is refused, the
running copy keeps running, and chat says the update needs a restart or a
newer client. A render pack is never reloaded; it takes effect when the client
restarts.

What this means for your plugin:

- **In-memory state does not survive a reload.** Anything you want to keep,
  save through `Storage` and read back in `Initialize` or on login.
- **Stop your own threads and timers in `Disable`,** and cancel any work you
  started with `Task.Run`. The host cannot stop them for you.
- **Do not keep references the host cannot see.** A static cache in another
  assembly, a handler on a .NET or operating-system event
  (`AppDomain.ProcessExit`, `SystemEvents`, a `FileSystemWatcher` you did not
  dispose), or a thread still running keeps the old copy in memory. The client
  checks for about a second after every reload and, if the old copy is still
  there, names your plugin in chat and in the log: it is switched off, but
  its memory is not returned until the client restarts.
- **`Assembly.Location` is empty.** Your assembly was loaded from memory, so
  it has no file. Read the files your package ships relative to
  `IPluginHost.PluginDirectory`. A relative markup path handed to `Ui` is
  already read from your plugin's folder, so `Path.Combine(".", "panel.xml")`
  and plain `"panel.xml"` both work.

## Checking it before you publish

`acdream-plugincheck` tells you whether the launcher would install your
plugin, using the launcher's own code: the same manifest parser, install
rules, zip safety checks, content policy and icon rules an install runs. It
cannot disagree with the launcher, because it is the launcher's code.

Run it from your OpenAC checkout (the one `OpenAcRoot` points at). It works
the same on Windows, macOS and Linux:

```
dotnet run --project <OpenAcRoot>/src/AcDream.PluginCheck -- <path>
```

`<path>` is either your **release zip**, which is what players actually
download and the check to run before publishing, or an **unzipped plugin
folder**, which is what a hand install looks like. Checking the zip also
verifies a `.sha256` sidecar if one sits beside it.

```
OpenAC PluginCheck — solrlabs.buffbot-0.1.0-beta.2.zip
Mode: plugin .zip (a release asset)

[PASS] zip within size cap
[PASS] zip extracts safely
[PASS] plugin.json present
[PASS] plugin.json parses
[PASS] manifest satisfies install rules
[PASS] plugin content policy
[PASS] plugin icon
[PASS] sha256 sidecar

Verdict: this plugin would install.
```

A failure names the problem in the launcher's own words, the same message a
player would see:

```
[FAIL] plugin.json parses
       capability 'chat' note contains a link. Fix: Correct the problem named
       above in plugin.json; ...
```

Checks stop at the first problem in the manifest, so fix it and run again
until the verdict passes. The exit code is `0` when the plugin would install,
`1` when it would be refused, and `2` when the path is missing or unreadable.
Add `--json` for a single machine-readable line in CI, with nothing else
written to standard output.

Two things it cannot check from a local file, so check them yourself against
[plugin-manifest.md](plugin-manifest.md): the **GitHub release layout** (tag
name, asset names, the prerelease flag, whether the release is marked
*latest*), and whether a **particular client version** falls inside your
`minHostVersion`/`maxHostVersion`/`skipHostVersions` range.

## Publishing a release

The launcher installs a plugin from a GitHub release in your own repository.
It never runs a downloaded file: it unzips, verifies the checksum, and stages
the plugin **disabled**, so enabling it stays an explicit choice the player
makes on the Plugins tab.

A release the launcher can install is stricter than a plugin that merely
loads locally. The headlines:

- A namespaced `id` (`yourname.yourplugin`), and a SemVer `version` equal to
  the tag minus a leading `v`.
- `minHostVersion` and `hosts` become **required**, not optional.
- Tag `v<version>`, on a public repository, not a draft.
- Assets with exact names: `plugin.json`, `<id>-<version>.zip`,
  `<id>-<version>.zip.sha256`, and `icon.png` if the zip carries one.
- Managed files only, by extension allowlist; no `runtimes/` folder.
- An optional `icon.png` at the zip root: PNG, exactly 64x64, at most 64 KiB,
  not animated. No icon is fine; a broken one refuses the whole install.
- Declare [capabilities](plugin-manifest.md#capabilities) for anything the
  player would want to know about — network access, chat, input automation.
  The launcher shows them before installing and asks again when an update
  changes them.

[plugin-manifest.md](plugin-manifest.md) has the full contract, the size and
extraction caps, and how to publish a **beta release** for players who opt
that plugin into the Beta channel.

## Getting listed

A listed plugin appears in the launcher's **Discover** panel, so players can
find and install it without being sent a link. The list lives at
[eriknihlen/openac-plugins](https://github.com/eriknihlen/openac-plugins).

**To ask for a listing, open an issue on that repository** with your plugin's
id, display name, author name, a one-line description, and the `owner/name`
of its GitHub repository. The list is published as a release asset by its
maintainer; there is no pull request to merge, which is deliberate — nothing
lands in the list that its maintainer did not put there.

What is checked before a plugin is listed:

- `acdream-plugincheck` passes against the release zip itself.
- The repository is public, and the release layout is exactly as described.
- Declared capabilities match what the plugin actually does.

Beyond that mechanical bar, listing is at the maintainer's discretion: what
a plugin does is looked at, a plugin can be declined, and one already listed
can be **blocked** later — by id and version, with a reason players see. A
blocked plugin is hidden from Discover, refused for install or update, and
filtered out of every character's plugin list at launch. Being unlisted is
not a judgement; a plugin distributed by link installs perfectly well through
**Add from URL**.

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
- [openac-mosstank](https://github.com/eriknihlen/openac-mosstank): a large
  external plugin, which uses the panel markup heavily.
- `samples/`: render packs, the `RenderPack` kind.
