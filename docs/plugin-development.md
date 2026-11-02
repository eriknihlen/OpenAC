# Writing a plugin

openac-rust plugins are WebAssembly modules. You write one as an ordinary
Rust crate against the `openac-plugin` SDK, build it for
`wasm32-unknown-unknown`, and drop it into the plugins folder. The client
compiles it to machine code, runs it on the session's own thread, and gives
it only what its `plugin.json` declares. The same plugin runs in a session
with a game window and in one without.

The SDK (`crates/openac-plugin`) and the calling convention under it
(`crates/ac-plugin-abi`) are MIT-licensed, so a plugin may use any license.

Plugins written for OpenAC (the C# client) are .NET assemblies and do not
load here; the client says so when it finds one.

## Project setup

A plugin is a `cdylib` crate (add `rlib` to test it natively). Depend on the
SDK from this repository, pinned to a release tag or a commit:

```toml
[package]
name = "my-plugin"
version = "0.1.0"
edition = "2024"

[lib]
crate-type = ["cdylib", "rlib"]

[dependencies]
openac-plugin = { git = "https://github.com/eriknihlen/openac-rust", tag = "v0.1.0" }

[profile.release]
# A panic stops the plugin; the client unloads it and says why.
panic = "abort"
opt-level = "s"
lto = true
codegen-units = 1
```

Install the target once with `rustup target add wasm32-unknown-unknown`, then
build:

```bash
cargo build --release --target wasm32-unknown-unknown
# -> target/wasm32-unknown-unknown/release/my_plugin.wasm
```

## The entry point

A plugin implements `Plugin` and names its type once with `export_plugin!`:

```rust
use openac_plugin::{Event, Host, Plugin, StartInfo};

#[derive(Default)]
struct Hello;

impl Plugin for Hello {
    fn enable(&mut self, host: &mut Host<'_>, _info: &StartInfo) -> Result<(), String> {
        host.register_command("hello", "Say hello")?;
        Ok(())
    }

    fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
        if let Event::Command(call) = event {
            host.chat_post(&format!("Hello, {}!", call.arguments));
        }
    }
}

openac_plugin::export_plugin!(Hello);
```

The client constructs the plugin with `Default`, calls `enable` (register
verbs and read settings there; an error refuses the load), then `tick` every
15 ms of client time and `on_event` for everything that happens, until
`disable`. It never calls a plugin re-entrantly. Match events with a
catch-all arm: a new API minor version adds event kinds, and the client sends
a plugin only the kinds of the API version it was built for (API 1.1 added
`Event::StatusTextReceived`, the text of each notice on the status line, such
as the server's "already in use" refusal; a chat filter that hides a notice
does not stop it being heard). A record field a new minor version adds comes
at the end of its record, and a plugin built for an older version is sent the
record without it (API 1.2 added `hook_item_types` to the inventory and
world-object records: the item-type bits saying what a house hook takes, 0
for anything that is not a hook). API 1.4 added five plugin themes: a plugin
built for an older version is told `ThemeKind::Moss` under any of them, with
the theme's real name and colours. API 1.5 added, at the end of a path
preview's `NavigationPlan`, whether it reused a grid and how long the client's
worker took. Everything the client offers
goes through `Host`: game state, items, chat, movement and navigation, trade
and vendors, storage, network, peers, and UI. `cargo doc -p openac-plugin
--open` shows the whole surface with its documentation.

**Controls show what the plugin stores.** A checkbox, colour swatch or other bound
control does not change itself. A click sends the value it asks for to the
`onchange` handler, and the control shows whatever its binding says afterwards.
Store the value and set the binding, or the control looks as if nothing happened.

**Drawing like a themed window.** A canvas paints its own pixels, so it reads
the theme with `host.theme()`, hears each change as `Event::ThemeChanged`, and
repaints. For the same look, use the theme's colours, the bundled font's
SemiBold weight for titles (`host.font_bundled(14.0, FontWeight::SemiBold)`), and
the sizes in `openac_plugin::ui::theme_metrics`: `WINDOW_RADIUS`, `CONTROL_RADIUS`,
`HEADER_HEIGHT`, `SWITCH_WIDTH`, `SWITCH_HEIGHT` and `SWITCH_KNOB`.

**Light and dark.** Parchment is a light theme. A literal colour can name a
second colour for light themes, `color="#FFB6F0AE|light:#FF2E7D32"`, and a file
icon can have `icons/sword.light.png` and `icons/sword.dark.png` beside
`icons/sword.png`. The light one is used under a light theme in a
`theme="plugin"` window. The first colour and the dark file are used everywhere
else, Classic included, and a version that is missing or cannot be used falls
back to the named file. For a bound colour, read `host.theme().is_light()`.

**Dock icons.** A window's slot on the plugin dock shows the first of these it
can use: the window's own file (`PanelSpec::icon_file("icons/combat.png")`, API
1.4), the plugin's `icon.png`, the render surface from `PanelSpec::icon`, and
two letters from `icon_text` or the title. The window's file follows the rules
of `icon.png`: a PNG by name and by signature, exactly 64 x 64 pixels, at most
64 KiB and not animated, with a path relative to the plugin's folder that stays
inside it. It keeps its own colours and dims while the window is closed. It may
have `icons/combat.light.png` and `icons/combat.dark.png` beside it, each under
the same rules: the dock draws the one for its theme when it is there. A file
that cannot be used is logged once, and the next icon is drawn.

**Fonts.** Canvas text is drawn in the interface font unless a plugin names
one of its own (API 1.4). `host.font_bundled(16.0, FontWeight::SemiBold)` gives
the client's Noto Sans in the weight themed window titles use, and
`host.font_load("Inter-Bold.ttf", include_bytes!("../fonts/Inter-Bold.ttf"), 22.0, &[])`
prepares a TrueType or OpenType font the plugin ships. Each size of each font is
its own `Font`, carrying its `pixel_size`, `line_height` and `ascent`, and sizes
are prepared to the nearest quarter pixel. Draw with `painter.text_in(font, ...)`
and measure with `host.font_measure(font, text)`, which includes kerning. Asking
twice for the same font holds it twice, and `host.font_release(font)` lets go of
one hold.

Name characters beyond the default set in the last argument, so an icon font
can name the private-use area. A plugin may hold 16 fonts, sized from 6 to 64
pixels, each of at most 2048 characters. Its own fonts may take 16 MB, counting
the files and their atlases; a size of the bundled font counts its atlas only
while no other plugin holds it too. A file's name may be 256 bytes and a request
may list 256 ranges. A font whose outlines take too much work to draw is refused.
A request past a limit, or a file that is not a usable font, answers
`Font::NONE`. A refusal is logged once, and each kind of refusal for at most
eight requests, then once to say the rest are left out; text in such a font, or a
released one, draws nothing.
Preparing a font takes some milliseconds, so ask up front rather than on every
paint.

**UI.** Windows are written in OpenAC's panel markup with `{Binding}`s; the
plugin registers a window with `ui::PanelSpec`, pushes values to its
bindings, and hears its buttons as `Event::Ui`. The window's state lives in
the session, so a game window that attaches later shows it as it stands, and
a session without a window answers the same.

**World drawing.** A plugin can put labels over objects
(`Host::show_labels`), lines (`Host::create_line_layer`) and filled triangles
(`Host::create_triangle_layer`, API 1.3) in the world, at `MapPoint`
positions, and icons and ground shapes (`Host::create_marker_layer`, API 1.3)
over objects or at positions. Lines, triangles and markers live in layers the
plugin owns: setting a layer replaces its contents, and removing it, or
unloading the plugin, takes them down. Labels, and markers over objects, also
go when the character leaves the world, since the objects they sat on go with
it and their ids mean nothing on the next stay; a marker layer stays usable,
so set them again on the next stay. Lines, triangles and markers at a fixed
position stay, since a position is the same place in the world after a relog.

*Triangles* are flat between their three corners and see-through by their
colour's alpha (`0xRRGGBBAA`, one per corner, blended across the face). They
are drawn from both sides and unlit. Walls and hills in front hide them, but
overlapping triangles blend rather than hide each other. A triangle does not
follow the ground, so lift one meant to lie on it by about 0.1 m. The client
draws what is within 250 m of the camera and in view, nearest first when a
frame holds more than about twelve thousand. A plugin holds at most 65,536
triangles across its layers; a set that goes over is refused, and the layer
keeps what it had. Send a few thousand at a time, a few times a second, not
every frame.

*Markers* are pinned by a `MarkerAnchor`: an object by its id, which they
follow, or a `MapPoint` and whether it is on the landscape. A marker over an
object the client does not hold is not drawn, and comes back when it does.
A plugin holds at most 256 icons and 256 shapes across its layers; a set that
goes over is refused, and the layer keeps what it had. Markers pinned to
nothing, or with values that are not usable, are dropped from the set.

*Icons* (`MarkerLayer::set_icons`, `ui::world_icon`) hang one of the plugin's
own images in the world at a constant size on the screen, 8 to 128 interface
points: they grow and shrink with the player's UI Scale like the rest of the
interface, and at 100% a point is a pixel (two on a Retina Mac).
They are drawn after the world, under the labels' text, so walls and hills do
not hide them, unless `hidden_by_world` is set. Then the whole icon goes while
anything in the world stands between the camera and its anchor, a frame or two
after the view changes. An object's icons sit in rows of eight above its head
and its labels, in the order given; an icon at a position is centred on it.
Each fades over the last fifth of its range. The portal view hides icons and
labels alike.

*Ground shapes* (`MarkerLayer::set_shapes`, `ui::ground_ring`,
`ui::ground_disc`, `ui::ground_arc`) lie on the ground around their anchor,
with a radius of 0.1 to 100 m: a ring is a band inward from the radius, a disc
is filled, and an arc goes clockwise from a compass bearing (0 north, 90 east),
as a wedge or a band. Set `faces_object` for an arc that turns with its object,
with its start measured from the way the object faces. They are see-through by
their colour's alpha, and walls and hills hide them. On the landscape, within a
metre of the land, a shape follows the slope; anywhere else (a floor, a roof, a
bridge, a dungeon) it lies flat at the anchor's height. The client draws shapes
centred within 250 m of the camera and in view, nearest first when a frame holds
more than it has room for.

**Navigation.** `Host::go_to` and `Host::go_to_place` walk the character,
and `Host::go_to_report` says how the latest walk stands. The client plans
over a grid of 0.25 m columns built from the collision loaded now; inside a
sealed dungeon one grid covers every cell. `Host::preview_path` and
`Host::preview_path_to_place` plan a route without moving the character: each
returns a handle (0 never), and the plan arrives later as
`Event::NavigationPlan` with that handle, to this plugin only. Only asking
happens on the session thread. The grid build and the search run on a worker,
so a plan can take tens of milliseconds and several can be out at once. A
preview searches the grid a walk or an earlier preview built when it still
stands for the world there. Inside a sealed dungeon a grid built for a
preview is kept for the walks and previews after it, until it goes unused for
30 seconds or the dungeon unloads. The plan's `grid_reused` says whether its
grid was there already (API 1.5), and `took_ms` how long the worker spent:
the first preview in a dungeon builds the grid, and the ones after it are
quicker.

## Testing natively

On a native target the SDK has `testing::FakeHost`, which answers every
client call the way the client does, records what the plugin did, and drives
its lifecycle, so plugin logic runs under `cargo test`:

```rust
use openac_plugin::testing::FakeHost;

let mut host = FakeHost::new("example.hello");
let mut plugin = Hello::default();
host.enable(&mut plugin).unwrap();
assert!(host.type_line(&mut plugin, "/hello world"));
assert_eq!(host.posted[0].text, "Hello, world!");
```

`FakeHost::set_reply` sets what a client function answers; anything not set
gets the inert answer (nothing found, unavailable).

## plugin.json

Every plugin folder holds a `plugin.json` beside its module:

```json
{
  "id": "yourname.hello",
  "displayName": "Hello",
  "version": "0.1.0",
  "entry": "hello.wasm",
  "apiVersion": "1.5",
  "minHostVersion": "0.1.0",
  "permissionsVersion": 1,
  "permissions": [
    { "name": "chat", "note": "Answers /hello in chat." }
  ],
  "limits": { "memoryMiB": 64 }
}
```

| Member | Meaning |
|---|---|
| `id` | Namespaced, lowercase: `yourname.yourplugin` (segments of `a-z`, `0-9`, `-`). |
| `displayName` | The name players see. |
| `version` | A SemVer 2.0 version (`1.2.0`, `1.3.0-beta.1`). |
| `entry` | The `.wasm` file, inside the plugin folder. |
| `apiVersion` | The plugin API version it was built for, `"MAJOR.MINOR"`: the SDK's `API_CURRENT` (currently `"1.5"`), which must match the version the SDK writes into the module. |
| `minHostVersion`, `maxHostVersion`, `skipHostVersions` | The client versions it supports or is known broken on. `minHostVersion` is required for a launcher install. |
| `permissionsVersion` | The permission vocabulary version (`1`); required when `permissions` is present. |
| `permissions` | What it may do beyond the basics, each with a one-sentence `note` (at most 120 characters) the player reads before installing. |
| `limits.memoryMiB` | Its memory cap in MiB: default 256, at most 1024. |
| `dependencies` | Other plugin ids it names (kept, not acted on). |

Member names match ignoring case; a repeated member is refused.

### Permissions

A plugin can call only the client functions its permissions allow. The
basics (game state, commands, UI, its own storage) need none.

| Permission | Allows |
|---|---|
| `chat` | reading chat, chat filters and input interceptors, sending and posting lines |
| `network` | HTTP requests and outgoing sockets. Hosts listed in the entry's `hosts` are reached without asking; any other host needs the player's `/plugin allow <id> <host>` for the session. |
| `listen` | a listening socket |
| `profiles` | the shared VTank profiles folder |
| `clipboard` | the system clipboard |

### Limits

Each call into a plugin runs under a deadline: a tick, event, command or
callback slower than 100 ms is reported (a warning in the log and one chat
line), and one still running after 10 s is taken for stuck, stopped, and the
plugin unloaded. Starting and stopping get 30 s. Memory is capped by
`limits.memoryMiB`. What a plugin saves through storage lands in
`plugins/<id>/files/` inside its own folder, which installs, updates and
removal of its code never touch.

## Installing and running

The client loads every folder directly under its plugins folder that holds a
`plugin.json`:

| Platform | Plugins folder |
|---|---|
| Windows | `%LOCALAPPDATA%\openac-rust\plugins` |
| Linux | `$XDG_DATA_HOME/openac-rust/plugins` (default `~/.local/share/openac-rust/plugins`) |

`OPENAC_ROOT_DIR` or `--root-dir` names another install folder. Copy your
`.wasm`, `plugin.json` and any other files into one subfolder and start a
session. What the plugin logs goes to the session's log.

**Reloading.** When the module, its markup or `plugin.json` changes and then
stays unchanged for one second, the client reloads the plugin: it disables
the running copy, releases everything it registered, and loads the new one.
`/plugin reload <id>` and `/plugin reload all` do it on demand, in the chat
box and on a headless console alike; `/plugin list` lists what is loaded.
In-memory state does not survive a reload; save what you want to keep
through storage.

## Checking it before you publish

`plugincheck` tells you whether the launcher would install your plugin, using
the launcher's own code: the same manifest parser, install rules, zip safety
checks, content policy and icon rules. It also loads the module (never runs
it) and checks it as a session would: the API version it was built for, the
record layouts it was compiled against (a plugin built against a different
SDK layout must be rebuilt), and that every client function it imports is
allowed by its declared permissions. It lists permissions declared but never
used.

Install it once from this repository (or run it from a checkout with
`cargo run --release -p plugincheck --`):

```bash
cargo install --locked --git https://github.com/eriknihlen/openac-rust plugincheck
plugincheck <path> [--json]
```

`<path>` is a plugin folder (a hand install) or a release zip. Each check
prints `[PASS]`, `[FAIL]` or `[SKIP]`, a failure with its fix. Exit code 0
means it would install, 1 that it would be refused, 2 a usage error or a
missing path. `--json` prints one JSON object and nothing else, for CI.

## Publishing a release

The launcher installs plugins from GitHub releases. It never runs a
downloaded file: it unzips it within strict limits, verifies the checksum,
and stages the plugin **disabled**, so enabling it stays the player's choice.

`plugincheck pack` writes the release assets from your plugin folder:

```bash
plugincheck pack <plugin folder> <out dir>
```

It writes `plugin.json`, `<id>-<version>.zip` (every file of the folder,
manifest at the root; the same folder always packs to the same bytes),
`<id>-<version>.zip.sha256`, and `icon.png` when the folder has one, then
checks the zip it wrote and prints the tag to publish under. A manifest the
launcher would refuse writes nothing.

Publish those files as the assets of a release in a public repository:

- **Tag `wasm-v<version>`**, where `<version>` is the manifest's `version`.
  The `wasm-` prefix keeps WebAssembly builds apart from a C# plugin's
  `v<version>` releases in the same repository. Do not mark it as the latest
  release if the repository also publishes C# releases.
- **Assets with exact names**: `plugin.json`, `<id>-<version>.zip`,
  `<id>-<version>.zip.sha256`, and `icon.png` if the zip carries one.
- **Content**: only `.wasm .json .xml .txt .md .png .ttf .otf` files, the
  zip at most 64 MiB, no top-level `files` folder.
- **Icon** (optional): `icon.png` at the zip root, a 64 x 64 PNG of at most
  64 KiB. No icon is fine; a broken one refuses the install.
- **Beta releases**: a pre-release version (`1.3.0-beta.1`) is offered only
  to players who put that plugin on the Beta channel.

Players install it with the launcher's **Add from URL**
(`https://github.com/<owner>/<repo>`), and the launcher checks the
repository's releases for updates.

## Getting listed

A listed plugin appears in the launcher's **Discover** panel. The list is
published from [eriknihlen/openac-plugins](https://github.com/eriknihlen/openac-plugins)
as a release asset, `plugins-rust.json`; its entries for this client carry
`"client": "openac-rust"`. To ask for a listing, open an issue there with your
plugin's id, display name, author, a one-line description, and its
repository. Before listing, `plugincheck` must pass against the release zip,
the repository must be public with the release layout above, and the
declared permissions must match what the plugin does. Listing is at the
maintainer's discretion, and a listed plugin version can later be blocked.
An unlisted plugin installs just as well through **Add from URL**.

## Contributing to the API

If your plugin needs something the API does not offer, open an issue or a
pull request here. Changes to `ac-plugin-abi` follow the rules in
[CONTRIBUTING.md](../CONTRIBUTING.md#the-plugin-abi): a record never changes
shape silently, and a change to the layout fingerprint means plugins must be
rebuilt.
