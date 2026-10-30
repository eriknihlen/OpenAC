# What's new

The latest releases of both clients. Every release, with its downloads and
full changelog, is on GitHub:
[OpenAC releases](https://github.com/eriknihlen/OpenAC/releases) and
[openac-rust releases](https://github.com/eriknihlen/openac-rust/releases).

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
