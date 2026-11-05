//! The world side of a drag: what the object under a press point lifts.
//! Nothing lifts in combat, nothing wielded lifts, and nothing with a
//! request already in flight lifts. A loose object lifts unless it is stuck
//! in place or a creature: it carries a ground payload, so a drop on the
//! inventory picks it up. Your own character lifts too: it carries your
//! main pack, so it can go on the hotbar as a shortcut that targets
//! yourself (healing kits, mana stones).

use ac_core::combat::CombatMode;
use ac_core::items::{ClientObjectTable, ItemType, PublicWeenieFlags};

use crate::event::{DragPayload, ItemDragPayload, ItemDragSource};
use crate::icons::IconSource;
use crate::panels::inventory::PLAYER_PACK_BASE_ICON;
use crate::tree::DragGhost;

/// Where the main pack sits in the pack row: lifting the player off the
/// world is the same lift as one started on that slot, so every drop
/// target that knows that payload treats it the same.
pub const MAIN_PACK_SLOT_INDEX: i32 = 0;

/// The slot a lift off the world names: an object in the world sits in no
/// list.
pub const WORLD_SLOT_INDEX: i32 = 0;

/// The drag ghost's size in pixels, the paperdoll figure's.
pub const GHOST_SIZE: i32 = 32;

/// The lift for the object `hit` under the press point, or `None` when it
/// lifts nothing. `objects` describes it; `pending_item` is the item with
/// a request in flight, which waits and so does not lift.
pub fn lift(
    hit: u32,
    player: u32,
    combat_mode: CombatMode,
    objects: &ClientObjectTable,
    pending_item: Option<u32>,
    icons: &mut dyn IconSource,
) -> Option<(DragPayload, Option<DragGhost>)> {
    if hit == 0 || combat_mode != CombatMode::NON_COMBAT || pending_item == Some(hit) {
        return None;
    }
    let object = objects.get(hit)?;
    let stuck = object
        .public_weenie_bitfield
        .is_some_and(|f| f.intersects(PublicWeenieFlags::STUCK));
    let creature = object.item_type.intersects(ItemType::CREATURE);
    if ((stuck || creature) && hit != player) || object.wielder_id != 0 {
        return None;
    }
    if hit != player {
        // A loose object carries itself, as a ground lift, so a drop on a
        // pack picks it up.
        let payload = ItemDragPayload::new(hit, ItemDragSource::Ground, WORLD_SLOT_INDEX);
        let ghost = ghost(
            icons,
            object.item_type,
            object.icon_id,
            object.icon_underlay_id,
            object.icon_overlay_id,
            object.effects,
        );
        return Some((DragPayload::new(payload), ghost));
    }
    let payload = ItemDragPayload::new(hit, ItemDragSource::Inventory, MAIN_PACK_SLOT_INDEX);
    // The player has no icon of their own; the pack row draws the main pack
    // with a fixed one, and this lift shows the same picture.
    let ghost = ghost(icons, ItemType::CONTAINER, PLAYER_PACK_BASE_ICON, 0, 0, 0);
    Some((DragPayload::new(payload), ghost))
}

/// The picture a lift shows: the dedicated drag icon, else the item icon.
fn ghost(
    icons: &mut dyn IconSource,
    item_type: ItemType,
    icon: u32,
    underlay: u32,
    overlay: u32,
    effects: u32,
) -> Option<DragGhost> {
    icons
        .drag_icon(item_type, icon, underlay, overlay, effects)
        .or_else(|| icons.item_icon(item_type, icon, underlay, overlay, effects))
        .map(|texture| DragGhost {
            texture,
            width: GHOST_SIZE,
            height: GHOST_SIZE,
        })
}
