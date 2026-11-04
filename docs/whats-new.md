# What's new

The latest releases of both clients. Every release, with its downloads and
full changelog, is on GitHub:
[OpenAC releases](https://github.com/eriknihlen/OpenAC/releases) and
[openac-rust releases](https://github.com/eriknihlen/openac-rust/releases).

## openac-rust 0.1.11 (2026-11-04)

**New**

- **Plugins that work together** (plugin API 1.7 and 1.8): a plugin can offer
  a named service that other plugins call and get an answer from, and can
  follow another plugin's status values as they change. Limits on size and
  time keep one plugin from holding up the game. *(David Smith)*
- **Several plugins from one GitHub repository:** each plugin tags its own
  releases, and an optional index lists them; Discover, Add from URL and the
  update check find plugins by their id, and Add from URL asks which one to
  add. *(David Smith)*
- **Position log** for debugging (`OPENAC_POSITION_LOG=1`): shows when a
  creature and the server's place for it drift apart; plugins can read the
  server's last position too. *(David Smith)*
- **More for plugins:** canvases hear the mouse hover and leave (1.6), and
  plugins can put items into an open chest and read the landscape's terrain
  and its map colours.
- **New plugins in Discover:** MudSort (inventory sorting), GoArrow (routes
  over walks, portals and recalls, with a Dereth map) and MossTank 0.6.5 with
  a Comps HUD, LootOwnDeathCorpse, material names in `/ub` commands and a
  Monster Weapon cell that cycles the Items page *(Johan Lundberg)*, and a
  route of one point that holds still instead of turning in circles.

**Fixed**

- A plugin's `/ub use` on a portal or object no longer fails at some
  distances and then never works for that object again: the server walks the
  character there.
- A plugin's walk that was stopped at the wrong moment no longer left the
  character running on. *(David Smith)*
- Walks through large areas plan much faster (one took 19.6 s, now 2.0 s).
- MudSort 0.1.0 did not load on 0.1.10; MudSort 0.1.1 needs 0.1.11.

Plugins built for API 1.0 to 1.7 keep loading unchanged.

## openac-rust 0.1.10 (2026-11-03)

Close every client before updating: the first start moves your files into a
new folder layout (logs by server, character and start time; each plugin's
data inside its own folder) and deletes logs older than 7 days.

**New**

- **Plugin dock** in place of the plugin shelf: on any screen edge, across or
  down, in three sizes; it closes to an icon, can peek open on hover, and its
  end is dragged to set how many plugins it shows. *(David Smith)*
- **Five more plugin themes:** Lavender Dusk, Ember, Bloodmoon, Glacier and
  Parchment (the first light one), with rounded, shadowed windows and new
  controls. *(David Smith)*
- **Landscape Detail Textures** (Options, off by default): the fine ground
  grit older clients repeated over the terrain.
- **Launcher:** says why a client ended, with the server's own message; shows
  a frozen client as "Not responding"; after a crash counts down 30 s until
  the character can log in again; keeps relaunching while the server
  answers; closes over running clients with progress.
- Every session keeps its own folder with the client's full output and each
  plugin's log.
- Using something says "Using the …" or "Approaching …", and a locked chest
  says it is locked.
- **Plugin API 1.5:** plugin fonts, light and dark colours and image files in
  markup, dock icons (1.4); every reachable cell of a dungeon with its walking
  distance, faster path previews, and cells' doorways, bounds and floor
  height (1.5). *(David Smith)*

**Fixed**

- Long farming sessions no longer grow by hundreds of MB of recoloured
  textures. *(reported by LiamWestray)*
- An examined spell shows foci and infusions among its components and marks
  the ones you don't carry; special chests show their contents once
  unlocked; the chase camera stops at rising ground instead of bouncing over
  it. *(reported by Rumpadump)*
- Dropping your main pack on a vendor sells what's in it and never your spell
  foci.
- Changing an option no longer puts back other settings, such as the plugin
  dock's place, after a restart.

## OpenAC 0.1.32 (2026-10-31)

Brings OpenAC level with openac-rust 0.1.9, except texture detail applying at
once and plugins drawing in the world.

**New**

- **UI Scale:** the interface at 50% to 300%, under Display beside Potato
  Mode; it applies when you let go of the slider. *(mightbemyname)*
- **Modern mouse turning** and **both mouse buttons run forward**: opt-in
  camera options; off by default. *(mightbemyname)*
- The window title names your character and shows readable statistics, and a
  window opened onto a running session comes to the front.
  *(reported by Rumpadump)*

**Fixed**

- Decorations that should not be selectable can't be clicked, cycled to with
  the selection keys or seen on the radar. *(David Smith, reported by
  Rumpadump)*
- Allegiance members show as squares on the radar. *(reported by Rumpadump)*
- Dragging out of the salvage window, a vendor's sell list or the house
  window no longer drops the item on the ground. *(David Smith, reported by
  Rumpadump)*
- Items on the ground drag into the inventory or onto the hotbar.
  *(reported by David Smith)*
- A newly identified Aetheria moves to the top-left slot of its pack.
  *(reported by Rumpadump)*
- A vendor keeps its tab and its row's scroll when the shop refreshes after a
  purchase. *(reported by Rumpadump)*
- Character creation: the face and clothes choices zoom the preview, and
  Gearknight and Olthoi colours stay picked. *(David Smith, reported by
  Rumpadump)*
- Long text in the game's own windows wraps and starts at its first line
  (the profession help in character creation, for one).
  *(reported by Rumpadump)*
- The default camera sits as far back as in the original game; clicking the
  chat window no longer stops a run. *(reported by Rumpadump)*
- Buffs and debuffs add up in the attribute, vital and skill panels as in the
  original game, including spells the client doesn't know.
  *(reported by evantaylor)*
- Maximum health counts Enlightenment.
- A chest you unlock stops reading as locked.

## openac-rust 0.1.9 (2026-10-31)

**New**

- **UI Scale:** Options → Display, 50–300 %. *(mightbemyname)*
- **Modern mouse turning:** Options → Camera. Your character turns with the
  camera, and both mouse buttons run forward. *(mightbemyname)*
- **Plugins can draw in the world:** markers, lines, triangles, icons and
  ground shapes (plugin API 1.3). *(David Smith)*
- Ctrl+drag works on plugin canvases. *(David Smith)*
- The window title shows your character's name and labelled stats, and
  `/window` brings it to the front. *(reported by Rumpadump)*

**Fixed**

- Buying from a vendor keeps the shop list's scroll position and tab;
  allegiance members show as boxes on the radar; texture detail and Potato
  Mode apply at once; long text in character creation wraps and starts at the
  top; the camera resets to its default after relogging.
  *(reported by Rumpadump)*
- Health no longer shows above its maximum on enlightened characters.

## openac-rust 0.1.8 (2026-10-30)

**New**

- **macOS downloads (Apple silicon, macOS 14 or later):** `OpenAC.app`,
  sized right and sharp on Retina screens. The first time, allow it under
  System Settings → Privacy & Security → Open Anyway. *(David Smith)*

**Fixed**

- Decorations that should not be selectable can't be clicked, cycled to or
  seen on the radar. *(David Smith, reported by Rumpadump)*
- Dragging out of the salvage window or a vendor's sell list no longer drops
  the item on the ground. *(David Smith, reported by Rumpadump)*
- Items on the ground drag into the inventory or onto the hotbar.
  *(reported by David Smith)*
- Character creation: the face and clothes choices zoom the preview, and
  Gearknight and Olthoi colours stay picked. *(David Smith, reported by
  Rumpadump)*
- Picking up an item into a stack shows the stack's count; the examination
  window fits large creatures; the default camera sits as far back as in the
  original game; clicking the chat window no longer stops a run; a newly
  identified Aetheria moves to the top-left slot of its pack.
  *(reported by Rumpadump)*
- Buffs and debuffs add up in the attribute and skill panels as in the
  original game. *(reported by evantaylor)*

## openac-rust 0.1.7 (2026-10-29)

- **macOS (Apple silicon):** the client builds and plays on Macs with Apple
  silicon, drawing natively through Metal. There are no Mac downloads yet;
  build it from source. *(David Smith)*

## OpenAC 0.1.31 (2026-10-29)

**New**

- **Chess:** use a chess board in the world to play in the Game Center,
  against players or the server's AI; also from headless sessions and
  plugins. *(David Smith)*
- **Plugin icons from files:** plugin windows can show their own PNG and JPEG
  images. *(David Smith)*
- **Plugins** can read what a house hook accepts. *(David Smith)*

**Fixed**

- No more getting stuck in portal space after a portal into another area.
  *(David Smith, reported by Rumpadump)*
- House hooks open with a single item slot; container windows size to their
  capacity. *(David Smith)*
- Players you teleport to are shown holding their weapon or tool.
  *(David Smith)*
- Screenshots work on Retina and other high-DPI screens. *(David Smith)*
- Inventory: the main pack on the toolbar shows how full it is, packs drag
  with one press, items stack when dropped on a pack, the window refreshes
  when you switch characters, and drops find room in the pack you have open.
  *(reported by Rumpadump)*
- Character info shows correct grades; choosing a face takes off headgear;
  locked sliders fill correctly. *(reported by Rumpadump)*
- The standing long jump is sent correctly, and the stop key stops a run.
  *(reported by Rumpadump)*
- The examination window's stat rows stretch with the window.
  *(reported by Rumpadump)*
- Fullscreen works on Wayland. *(reported by Rumpadump)*
- Items lying on the ground stay put instead of hovering.
  *(reported by jhendy717)*
- Ethereal and static objects collide correctly.

## openac-rust 0.1.6 (2026-10-29)

**New**

- **Chess** in the Game Center, also from headless sessions and plugins.
  *(from OpenAC, David Smith)*
- **Plugin icons from files** (PNG and JPEG). *(from OpenAC, David Smith)*
- **Plugin API 1.2:** plugins can read what a house hook accepts. Existing
  plugins keep working without a rebuild.

**Fixed** *(reported by Rumpadump unless noted)*

- Fullscreen works on Wayland, and the screen stays awake in fullscreen on
  Linux.
- No more blur when moving: the player and camera move smoothly.
- Jumping: the charge stays ready, and the stop key keeps it.
- Doors and stairs no longer disappear.
- The map view isn't hidden by fog.
- Character creation sliders and face choice work; character info grades are
  correct.
- Inventory: pack fill on the toolbar, one-press pack drags, stacks join on
  drop, the inventory refreshes on relog, and drops find room in the open
  pack.
- Non-humanoid corpses and large treasure piles can be looted.
- Corpse and salvage items show their material name; salvage clicks work.
- The examination window updates, follows your selection, rewraps its text
  and stretches its stat rows.
- Spell and spell-component lists fill their window. *(spell components:
  reported by jhendy717)*
- Tells go to the selected player, not local chat.
- Respawned tokens fall instead of hovering. *(reported by jhendy717)*
- House hooks show a single slot; container windows size to their capacity.
- Ctrl+V no longer types a "v"; Escape keeps held keys; the combat power
  slider follows the mouse.
- Indoor objects without collision data collide correctly; objects and
  buildings draw out to the full near distance.

## Thanks

To David Smith for the code, and to Rumpadump and jhendy717 for the bug
reports.
