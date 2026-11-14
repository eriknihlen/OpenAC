+++
title = "Writing a plugin"
weight = 40
aliases = ["docs/plugin-development.html"]
+++
OpenAC plugins are WebAssembly modules. You write one as an ordinary
Rust crate against the `openac-plugin` SDK, build it for
`wasm32-unknown-unknown`, and drop it into the plugins folder. The client
compiles it to machine code, runs it on the session's own thread, and gives
it only what its `plugin.json` declares. The same plugin runs in a session
with a game window and in one without.

The SDK (`crates/openac-plugin`) and the calling convention under it
(`crates/ac-plugin-abi`, with its version type in `crates/ac-plugin-version`)
are MIT-licensed, so a plugin may use any license.

Plugins written for the retired C# client are .NET assemblies and do not
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
openac-plugin = { git = "https://github.com/eriknihlen/OpenAC", tag = "v0.2.0" }

[profile.release]
# A panic stops the plugin; the client unloads it and says why.
panic = "abort"
opt-level = "s"
lto = true
codegen-units = 1
```

Until 0.2.0 the SDK lived at `https://github.com/eriknihlen/openac-rust`, and
that address keeps working for revisions pinned there: a plugin that depends
on a tag before `v0.2.0`, or on a commit from before the move, can keep its
dependency as it is. Keep the old address for those tags; in this repository
the tags before `v0.2.0` are the retired C# client's releases.

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
`disable`. It never calls a plugin re-entrantly. A released API version
never changes: a new function, event kind or record field comes with the
next minor version, so a plugin built for one keeps loading as it was. Match events with a
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
worker took, at the end of `IndoorCell` its doorways and the cells seen
from it, and at the end of the floorplan's `DungeonCell` its bounds and floor
height. It also added `Host::reachable_cells` and its answer,
`Event::NavigationReach`, which a plugin built for an older version never
hears, and `Host::put_in_open_container`, which puts an owned item into the
open chest at a slot, or moves one already in it to another. It also added
`Host::landscape_terrain` and `Host::terrain_palette`, which read the
landscape from the player's game files for a map. API 1.6 added two kinds of
canvas pointer event, `PointerEvent::HOVER` (the pointer over a listening
canvas with no button held, at most one per delivery at the latest place) and
`PointerEvent::LEAVE` (one when the hovering ends: the pointer moved off, a
press began elsewhere, or the canvas hid or went), for tooltips; a plugin built
for an older version hears neither. API 1.7 added plugin services, through
which plugins built to work together call each other
([Plugin services](#plugin-services)), with `Event::ServiceChanged` and
`Callback::ServiceRequest`, which a plugin built for an older version never
gets. It also added `server_position` at the end of `WorldObject` and
`CombatTarget`: the last position the server sent for the object, beside
`position` (and, for a target, `distance`), which give where the client has
it now, a creature's simulated body. The two can stand apart while the body
catches up to the server's place; for the character it can lag where the
player has moved it. A change to it alone raises no object change (an
object is told moved when its cell changes), so a plugin that follows it
asks again. API 1.8 added subscriptions to the status board
([The status board](#the-status-board)), with `Event::StatusChanged`, which
a plugin built for an older version never gets. API 1.9 added `creature` at
the end of `ItemProperties`: a creature's profile from its last appraisal
([Appraising a creature](#appraising-a-creature)). A plugin built for an
older version is sent the properties without it. API 1.10 added
`Host::all_enchantments`: every enchantment on the character, also one that
a stronger enchantment of its family surpasses (an item's spell under a
stronger buff), where `Host::enchantments` lists only those in effect. It
covers every list (the multiplicative and additive ones, vitae and
cooldowns), in order of layer, then spell id; one with no spell-table entry,
such as a cooldown, reads family 0 and tier 0. An
enchantment that does not run out reads `seconds_remaining` infinite; a timed
one that has run out reads 0. A plugin built for an older version cannot
import it. It also added `align` on a list's `<column>` in markup. API 1.11
added `source` at the end of `SelectionChange` (`Event::SelectionChanged`):
who changed the selection, one of the constants in `world::selection_source`:
`SYSTEM` (0: the client itself: the selected object died, left the world or
hid, or the session ended; the nearest monster picked as the next target with
the auto-target option; a window picking a listing on its own, such as a
shop's first listing as it opens), `KEYBOARD` (1: a selection key or a
quick-slot select key; a deselect asked for on purpose, by Escape, a window or
a plugin, reads this too), `MOUSE` (2: any selection made with the mouse: a
left click, a right click that examines, or lifting an item to drag it, in the
world or in a window) and `PLUGIN` (3: a plugin's select, or a selection the
client makes for a command: an attack or a spell at a target, the give key, a
merge or a pickup selecting the stack it joins; so `PLUGIN` does not mean the
plugin's own selection). The event compares the selection
once a pass, so when it changed more than once in a pass it reports the last
change's source. A plugin built for an older version is sent the change
without it and reads `SYSTEM`. API 1.12
added `launch_speed` at the end of `ProjectilePathQuery`: for a missile or arc,
the launcher's maximum velocity in metres per second, so the probe flies at
that total speed on the lower arc to the aim point, as the server launches
the shot, instead of the fixed speed lofted to land there. A speed above
50 m/s is capped at 50. A speed that is not a finite number above 0 answers
`InvalidTarget`, and so does a target no arc reaches at the capped speed
("The target is out of reach at 20.0 m/s.", with no angle, as there is no
launch). Any other answer's notice ends with the speed and launch angle used
("Launched at 20.0 m/s, 28.0° up."). A straight shot ignores the speed. The
probe flies toward where the target is now; the server aims ahead of a moving
target, so the real shot can take a different arc. A
plugin built for an older version sends no launch speed, and its probe flies
as before. API 1.12 also added canvases inside windows: `in_markup` at the end
of a canvas registration, the markup `<canvas>` element that hosts such a canvas
([Canvases in windows](#canvases-in-windows)), and `Event::CanvasResized`,
which a plugin built for an older version never gets; its canvases sit on the
screen as before. It also added `Canvas::resize` for a canvas on the screen.
API 1.13 added WebP to the formats `Host::image_decode` reads (PNG, JPEG,
BMP, TGA and GIF before): lossy, lossless and with alpha, and an animation's
first frame, as for a GIF. No call changed; a client older than 1.13 refuses
the bytes (handle 0), so a plugin that ships only WebP art builds for 1.13.

Everything the client offers goes through
`Host`: game state, items, chat, movement and navigation, trade and vendors,
storage, network, peers, and UI. `cargo doc -p openac-plugin --open` shows
the whole surface with its documentation.

Three changes came with API 1.7 that a plugin built for an older version
may notice. A chat filter and an input interceptor now run with no other
plugin to ask, as every other callback does: a loot classifier or a
command's completions asked from one gets no answer (`None`), and a service
call is refused (`Nested`). `Callback` is non-exhaustive, as `Event` is, so
a plugin that matched every callback kind without a catch-all arm needs one
to build against this SDK. And a module that imports a client function
newer than the `apiVersion` it declares is refused at load: it would never
hear the events that go with it.

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

**List columns.** A list's `<column>` takes `align="left"` (the default) or
`align="right"` (API 1.10), which keeps its text cells and its header caption
to that edge of the column, a few pixels in, so numbers line up; text too
wide for the column starts at the left and is cut at the right, as a
left-aligned cell is. Any other value refuses the markup, and so does
`align="right"` on a tree list's tree column (the first text column), whose
tree indents from the left; its other columns may align right. The attribute
moves only text: check, icon and button cells draw as they do. A client
older than 1.10 ignores the attribute, as it ignores any it does not know,
and draws the column left-aligned.

### Canvases in windows

A canvas can sit inside one of the plugin's windows instead of on the screen
by itself (API 1.12), say a map with routes to edit. Put a `<canvas id="map">`
element in the markup and register the canvas with the same id and
`in_markup` on (`CanvasSpec::in_markup("map", 320, 240)`):

```xml
<panel title="Atlas" w="420" h="360" layout="column">
  <label text="{Route}"/>
  <canvas id="map" grow="1" tooltip="Drag a waypoint to move it"/>
</panel>
```

The element is an empty rectangle the window's layout places and sizes like
any other element: `x`, `y`, `w` and `h` in a window without a layout, or
`w`, `h`, `grow`, `minw` and the other flex item attributes in a flex
container. Its `id` is required and is the canvas's id as text, not a
binding. It holds no children and cannot be inside a `<repeat>`. It draws nothing itself and lets clicks
through; the canvas fills it, in whole pixels, at its top-left, and answers
for the rectangle, its tooltip included. It clips, so a canvas whose element
has no room shows nothing.

The registered `width` and `height` are the canvas's size only until it is
laid out. After that `Canvas::state` reports the laid-out size, and each new
size arrives as `Event::CanvasResized` with the canvas and its width and
height, the first layout included when it differs from the registered size. What the canvas shows was painted for the
old size, so paint it again then: `Canvas::paint` gives the painter the size
the canvas has now. The anchor and offset are kept but ignored, and
`Canvas::resize` answers false: the element decides the size.

Either side may come first. A canvas registered before its window waits for
its element, and an element draws nothing until its canvas is registered.
When two of the plugin's windows have an element with the same id, the first
one mounted hosts the canvas, and the other takes over if that window goes;
when the last one goes, the canvas waits for the next.
`Canvas::is_available` is true only while an element hosts it. The canvas is
drawn and takes input only while it is shown and its element is: its window
open, its tab or group not hidden. Pointer input (presses, held moves, the
wheel, hover and leave) works as for a canvas on the screen, in the canvas's
own pixels. A press on the canvas does not move the window. The client logs
once, not every frame, a canvas that waits for its element, an element whose
canvas another window already shows, and an element naming a canvas
registered without `in_markup`, which sits on the screen by itself.

**Resizing a canvas on the screen.** `Canvas::resize(host, width, height)`
(API 1.12) changes the size of a canvas on the screen. Its anchor, offset,
visibility, pointer listening and what it shows are kept, so a canvas
anchored at a right or bottom edge keeps that edge where it was. Paint it
again for the new size; `Canvas::width` and `Canvas::height` of the handle
you resized report the new size at once. A `Canvas` is `Copy`, so another copy
of the handle keeps the old size; `Canvas::state` asks the client for the size
it has now. Resizing to the size it has answers true and changes nothing.
It answers false, and nothing changes, for a width or height below 1, a
removed canvas, and an in-markup canvas.

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

Inside a sealed dungeon, `Host::reachable_cells(max_meters)` (API 1.5) asks
for every cell the character can walk to from where it stands, each with the
length of its route, from one search. Use `f32::INFINITY` as the distance for
the whole dungeon. It returns a handle, or 0 when nothing can be searched
from: out of the world, in portal space or the air, or not in a sealed
dungeon. The answer arrives as `Event::NavigationReach` with that handle, to
this plugin only. It runs on a worker over the same grid as walks and
previews, and in a dungeon of a few hundred cells it takes about 10 to 15 ms
once the grid is built. Each `CellReach` gives:

- `route_meters`: the search's cost by the shortest way over the grid's
  steps, before the route is straightened. That is metres walked, plus the
  height climbed or dropped, plus a few metres for each jump. In the
  installed dungeons it comes within about 15% of a preview's
  `length_meters` to the same place, usually a little under, since a walk
  keeps off walls.
- `place`: the point on the cell's floor the length is measured to, ready
  for `go_to_place` or a preview.
- `by_drop`: the way there steps or leaps down farther than the character
  steps up, so it could not walk back up the way it came. A jump, or
  another way round, might still take it back.

The cells come nearest first, the character's own among them. A cell left
out cannot be walked to from where the character stands now, within the
distance. It may lie behind a portal or an object in the way, in a part of
the dungeon joined only by a portal, or up a drop it cannot climb. It is
still part of the dungeon. A cell with no floor of its own (`floor_z` is
`None`) is never listed; the cell under it is. Doors are not in the grid, so
a shut or locked door stops neither a reach search nor a preview. A walk
opens a shut door when it reaches it and goes round a locked one.

**Dungeon data.** `Host::dungeon_floorplan` and `Host::indoor_cells` read a
landblock's cells from the game files once and keep them, so only the first
call for a landblock costs a few milliseconds. Each `IndoorCell` lists its
`doorways` (API 1.5): the cell each opens onto, or `leads_outside` for a
doorway to the landscape, and the doorway's middle in landblock metres. It
also lists `visible_cells`, the cells the game files say can be seen from
this one. That is the list the game draws from when the camera is in the
cell, worked out ahead of time from the doorways, so it is coarser than a
line of sight: it can take in a cell round a corner or behind a closed door.
A doorway is where the dungeon is open, not whether a door stands in it; a
door is a world object, and walks open it or go round it when it is locked.
Each floorplan `DungeonCell` has `bounds_min` and `bounds_max`, the box
around its polygons, and `floor_z`, the height of its lowest floor (API 1.5).
Use `floor_z`, not `layer_z`, to tell floors apart: `layer_z` is a 6 m band,
and a tall room's cell can sit a band above a low corridor on the same floor.
`floor_z` is `None` for a cell with no floor of its own. Tall rooms are built
as one cell on top of another, so the upper cell has none; nor does a shaft.
A plugin that needs a floor for such a cell can follow its doorways down.

**Landscape data.** `Host::landscape_terrain(&[u16])` (API 1.5) reads the
outdoor terrain of landblocks from the game files, so a plugin can draw a
map of Dereth. A landblock is named `x << 8 | y`, `x` counting east and `y`
north. It is a square 192 m on a side, and its terrain is a grid of 9 by 9
vertices 24 m apart, covering it edge to edge: its east and north edges are
its neighbours' west and south edges. Each `LandblockTerrain` lists its 81
vertices with vertex `(x, y)` at index `x * 9 + y` (`LandblockTerrain::vertex`
finds one). Vertex `(0, 0)` is the landblock's south-west corner and vertex
`(x, y)` stands at landblock-local `(24 x, 24 y)` metres. Each vertex has a
`terrain_type` (0 to 31), its `road` bits (0 to 3, non-zero where a road
runs) and `height_meters`, the ground's height there as the character
stands on it. Landblocks come back in the order asked; one with no terrain
in the files is left out. A call answers at most 256 landblocks
(`world::LANDSCAPE_TERRAIN_MAX`); the rest of a longer request is left out,
so a map of the whole world asks for it a page at a time over several ticks.
Each landblock is read once and kept; a full request takes a few
milliseconds at most.
`Host::terrain_palette()` lists each terrain type the region defines, so a
map looks like the game. Each `TerrainColor` has the type's name and two
colours, both `0xRRGGBB`. `rgb` is the mean colour of the type's landscape
texture, what the ground looks like close up. `map_rgb` is the colour the
game's data assigns the type for maps. After the 32 terrain types comes one
entry for roads, `terrain_type` 32 (`world::ROAD_TERRAIN_TYPE`), named
"Road": its `rgb` is the mean colour of the road texture, and since the
game's data gives roads no map colour, its `map_rgb` repeats `rgb`. Use it
where a vertex's `road` is non-zero; there the game draws the road over the
vertex's terrain type. The palette is worked out once a session, about 20 ms
the first time, so ask for it once (at start, or when the map is first drawn)
and keep it. A texture that does not decode gives its type an `rgb` of 0.
Neither call needs a permission; both are empty without the
game files.

## Appraising a creature

`host.identify(id)` asks the server to appraise an object without opening
the assess window. For a creature, the answer's profile is then in
`host.properties(id)` (the world object's properties) as `creature`:
`health` and `max_health`, and in `attributes` its six attributes and its
stamina, mana and their maximums (`max_stamina`, `max_mana`).
`attribute_highlights` marks which values enchantments change, one bit each
(strength `0x1`, endurance `0x2`, quickness `0x4`, coordination `0x8`,
focus `0x10`, self `0x20`, health `0x40`, stamina `0x80`, mana `0x100`), and
`attribute_colors` sets the same bit when that value is raised and clears it
when it is lowered: so a debuff that landed shows as a highlighted bit with
its colour bit clear. Both are 0 when nothing is enchanted. These are what the assess window shows
for the same appraisal. When the character could not assess the creature,
the server still sends its health, so `creature` holds `health` and
`max_health` with no `attributes`. Each appraisal answer replaces
`creature` with the profile it carried, and one with no profile (any
item's) leaves `None`. A new instance of the creature starts without one.

One appraisal is asked at a time, the player's or a plugin's: `identify`
answers `ItemStatus::Busy` while another is awaited (a lost answer is given
up after about 5 seconds), so ask again on a later tick. The answer usually
comes back within the server's round trip; `host.loot_state().appraisal`
counts it in `revision` and names the object in `current_id`. An appraisal the player
starts meanwhile takes the slot from a plugin's; the plugin's answer is
still kept on the object when it arrives but is not named in `current_id`,
so a plugin that follows a creature reads `creature` when the object
changes, or again on a later tick.

This changes nothing else: the server's health reports and
`CombatTarget::maximum_health` are as they were.

## Plugin services

Plugins built to work together, a suite, call each other through services
(API 1.7). One plugin offers a service by name and answers it; another calls
it and waits for the reply. The request and the reply are bytes the two
plugins agree on; the client never reads them.

The plugin offering it registers the service, `host.register_service_raw("distance",
"Metres to an object")`, which is then known to every plugin as
`<plugin id>/distance`. The returned handle comes back in each
`Callback::ServiceRequest`, which carries the caller's id and the request.
The plugin answers in `Plugin::callback` with
`CallbackAnswer::Service(Ok(reply))`, or with `Err(reason)` to refuse. A name
is 1 to 64 bytes of `a-z`, `0-9` and `-`, a description at most 1 KiB
(`DescriptionTooLarge`), and a plugin offers at most 32. The service goes
when the plugin calls `unregister_service` or stops; a plugin that fails
while answering is stopped, and its services go at once.

The calling plugin uses `host.call_service_raw("example.navigation/distance",
&request)`, matching the id ignoring case, and gets the reply's bytes, or a
`ServiceError` saying why there is none: no such service (`NotFound`), its
own (`SelfCall`: a plugin calls its own code directly), a call made while
answering any callback, a service request, a chat filter or an input
interceptor (`Nested`), a request, reply or refusal over 64 KiB
(`TooLarge`), the service refusing (`Refused`), the service's plugin
stopping while it answered (`TimedOut`, `Faulted`), or the calling plugin's
tick, event or command having already waited on other plugins' answers as
long as one call may take (`OutOfTime`; the service is not asked). What the
service says about a request, such as "no such object", belongs in its reply.
`host.services()` lists what is on offer. Every plugin hears
`Event::ServiceChanged` when a service comes or goes, and a plugin that
starts first hears one for every service already offered.

With the SDK's `json` feature (`openac-plugin = { ..., features = ["json"] }`,
off by default so a plugin without services builds no serde), the request
and reply are types the two plugins share, deriving serde's `Serialize` and
`Deserialize`, sent as JSON. `host.register_service::<DistanceTo,
Distance>("distance", "Metres to an object")` gives a `Service`, and in
`Plugin::callback`, `service.answer(&request, |asked| Ok(Distance { .. }))`
answers a request for it (`None` for another service), refusing one that
does not decode. The caller uses `host.call_service::<DistanceTo,
Distance>("example.navigation/distance", &DistanceTo { object })`, which
gives the reply or a `CallError`: the `ServiceError`, or a reply that does
not decode.

An answer runs as the offering plugin's own callback, under its own limits
([Limits](#limits)): a slow answer warns, and a stuck one stops, the plugin
that offers the service. The caller's clock stops while it waits, but one
tick, event or command of the caller may wait on other plugins (services,
loot classifiers, command completions) for at most one call deadline in
all; past it, a service call answers `OutOfTime`, and a classifier or a
completion is not asked (no answer), until the caller's next call.

The caller's id in a `ServiceRequest` is set by the client, never by the
caller, but it names a plugin id, not who made the plugin: any plugin
installed under that id carries it. The service decides whom to trust with
what. Permissions do not carry across a call: the answering plugin's own
calls into the client are checked against its own `plugin.json`, and the
caller's are not lent to it, so a service with the network permission uses
it on its own say, whoever asked, and what it does for a caller is its own
choice. Services are per session: a
plugin running for another character in the same client sees none of them.

For state that changes over time and that any plugin may read, such as
what a plugin is doing now, the status board fits better than a service.

## The status board

A plugin publishes short text values under keys with
`host.status_publish(key, Some(value))` (`None` withdraws one), and any
plugin of the session reads them by the publisher's id:
`host.status_read(id, key)` for one, `host.status_capture(id)` for all. A
plugin's values go when it stops.

Rather than read another plugin's values every tick, a plugin can subscribe
to them (API 1.8): after `host.status_subscribe(id)` it hears
`Event::StatusChanged` with the publisher, the key and the value now (`None`
once withdrawn). Changes are gathered over a pass of the plugins and handed
out on the next, one per key with its last value, so a publisher writing a
key every tick, or many times in one, costs its subscribers one event a
pass, and a key set and set back within a pass sends nothing. A plugin that
publishes in answer to a change is heard on the following pass, so two
plugins answering each other cannot loop.

A subscription may name a plugin that has not started yet, and outlasts it
stopping and starting again; when a publisher stops, its subscribers hear
each of its values withdrawn. A publisher reloaded within one pass is heard
as the difference: a value it published again unchanged is not told.
Subscribing tells nothing of the values already there, nor of changes made
earlier in the same pass, so read them with `status_capture` after
subscribing. A
plugin subscribes to at most 64 others (`status::MAX_SUBSCRIPTIONS`), never
to itself, and `host.status_unsubscribe(id)` ends one; all end when it stops.

```rust
fn enable(&mut self, host: &mut Host<'_>, _info: &StartInfo) -> Result<(), String> {
    host.status_subscribe("openac.vision");
    self.target = host.status_read("openac.vision", "target");
    Ok(())
}

fn on_event(&mut self, _host: &mut Host<'_>, event: Event) {
    if let Event::StatusChanged(change) = event
        && change.key == "target"
    {
        self.target = change.value;
    }
}
```

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
gets the inert answer (nothing found, unavailable). For services,
`FakeHost::set_service` stands in for another plugin's service, and
`FakeHost::request_service` calls one of the plugin's own as another plugin
would.

## plugin.json

Every plugin folder holds a `plugin.json` beside its module:

```json
{
  "id": "yourname.hello",
  "displayName": "Hello",
  "version": "0.1.0",
  "entry": "hello.wasm",
  "apiVersion": "1.13",
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
| `apiVersion` | The plugin API version it was built for, `"MAJOR.MINOR"`: the SDK's `API_CURRENT` (currently `"1.13"`), which must match the version the SDK writes into the module. The launcher installs a plugin only when the installed client loads this version (each client release says which versions it loads; see [Plugin API versions](rust-client.md#plugin-api-versions)), and refuses an install while no client is installed. |
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
plugin unloaded. Starting and stopping get 30 s. When a call asks another
plugin something (a loot classifier, a command's completions), the answer
runs as that plugin's own callback under these limits, and the asking
call's clock stops meanwhile: a slow or stuck answer counts against the
plugin that gave it, never the one that asked. Memory is capped by
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
it) and checks it as a session would: the API version it was built for
(against the client it was built with, so build `plugincheck` from the
release your players run), the
record layouts it was compiled against (a plugin built against a different
SDK layout must be rebuilt), and that every client function it imports is
allowed by its declared permissions. It lists permissions declared but never
used.

Install it once from this repository (or run it from a checkout with
`cargo run --release -p plugincheck --`):

```bash
cargo install --locked --git https://github.com/eriknihlen/OpenAC plugincheck
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

### Several plugins in one repository

A repository can publish several plugins (a suite built to work together,
say). Tag each plugin's releases with its id, and keep a plugin index:

```bash
plugincheck pack <plugin folder> <out dir> --index plugins-index.json
```

- **Tag `wasm-<plugin id>-v<version>`**, for example
  `wasm-acme.vision-v1.2.0`. `pack --index` prints it. The launcher matches
  the exact `wasm-<id>-v` prefix for the plugin it wants and checks that the
  release's `plugin.json` names that id, so another plugin's release is
  never offered in its place.
- **The index**: `pack --index <file>` sets this plugin's entry in the local
  file (starting it if it does not exist) and keeps every other plugin's.
  It never moves an entry back: packing a hotfix of an older line (1.1.1
  after 1.2.0) leaves the index naming 1.2.0, and says so. Upload it as `plugins-index.json` to a release tagged `wasm-index`, not
  marked latest, replacing the copy there, every time you publish a plugin:

  ```json
  {
    "schemaVersion": 1,
    "plugins": [
      { "id": "acme.vision", "stable": "wasm-acme.vision-v1.2.0",
        "beta": "wasm-acme.vision-v1.3.0-beta.1" },
      { "id": "acme.navigation", "stable": "wasm-acme.navigation-v0.4.0" }
    ]
  }
  ```

  GitHub's releases feed, which the launcher reads, lists only a
  repository's latest ten releases, so one plugin's releases can push
  another's off it; the index keeps every plugin findable. It only points
  at tags: the launcher still reads each tag's `plugin.json`, takes the
  highest version from the feed and the index together, and ignores an
  index it cannot read (noting why in its log), so a stale index at worst
  offers an older release. A repository publishing one plugin under
  `wasm-v` tags needs no index. The launcher reads the index when the feed
  shows a `wasm-<id>-v` tag or the `wasm-index` release, and otherwise
  only when none of the feed's releases is the plugin's: there is none for
  this client, each names another plugin, or (on the stable channel) all
  are pre-releases. A missing index is then simply not found.
- **Moving an existing plugin over**: its `wasm-v` releases keep counting
  for it (their `plugin.json` names it), so it can switch to its own tags
  at its next release. A new plugin in a repository whose older plugin used
  `wasm-v` tags needs its own tags from the start.
- **Older launchers**: launchers 0.1.10 and earlier read only `wasm-v`
  tags. A plugin published only under `wasm-<id>-v` tags shows there as
  having no release, and one that switches stops getting updates there.

**Add from URL** on such a repository lists the plugins not yet installed
and asks which one to add, then asks again for the rest until the player
cancels; each one added is its own entry on the plugin list, all naming
the same repository.

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
[CONTRIBUTING.md](https://github.com/eriknihlen/OpenAC/blob/main/CONTRIBUTING.md#the-plugin-abi): a record never changes
shape silently, and a change to the layout fingerprint means plugins must be
rebuilt.
