//! What a drag started on the world lifts (OpenAC
//! `tests/AcDream.App.Tests/UI/WorldDragTests.cs`, and GitHub #45: a loose
//! item on the ground lifts itself).

use ac_core::combat::CombatMode;
use ac_core::items::{ClientObject, ClientObjectTable, ItemType, PublicWeenieFlags};
use ac_ui::draw::TextureRef;
use ac_ui::event::{ItemDragPayload, ItemDragSource};
use ac_ui::icons::IconSource;
use ac_ui::panels::inventory::PLAYER_PACK_BASE_ICON;
use ac_ui::world_drag::lift;

const PLAYER: u32 = 0x5000_0A01;
const LOOT: u32 = 0x7000_0001;
const LOOT_ICON: u32 = 0x0600_1234;

struct Icons {
    drag: bool,
}

impl IconSource for Icons {
    fn item_icon(&mut self, t: ItemType, icon: u32, _: u32, _: u32, _: u32) -> Option<TextureRef> {
        if t == ItemType::CONTAINER && icon == PLAYER_PACK_BASE_ICON {
            return Some(TextureRef::Runtime(2));
        }
        (icon == LOOT_ICON).then_some(TextureRef::Runtime(4))
    }
    fn drag_icon(&mut self, t: ItemType, icon: u32, _: u32, _: u32, _: u32) -> Option<TextureRef> {
        if !self.drag {
            return None;
        }
        if t == ItemType::CONTAINER && icon == PLAYER_PACK_BASE_ICON {
            return Some(TextureRef::Runtime(1));
        }
        (icon == LOOT_ICON).then_some(TextureRef::Runtime(3))
    }
    fn spell_icon(&mut self, _: u32) -> Option<TextureRef> {
        None
    }
    fn component_icon(&mut self, _: u32) -> Option<TextureRef> {
        None
    }
}

/// The player and a loose sword lying on the ground.
fn world() -> ClientObjectTable {
    let mut t = ClientObjectTable::new();
    let mut ev = Vec::new();
    let mut player = ClientObject::new(PLAYER);
    player.item_type = ItemType::CREATURE;
    player.public_weenie_bitfield = Some(PublicWeenieFlags::PLAYER);
    t.add_or_update(player, &mut ev);
    let mut loot = ClientObject::new(LOOT);
    loot.item_type = ItemType::MELEE_WEAPON;
    loot.icon_id = LOOT_ICON;
    t.add_or_update(loot, &mut ev);
    t
}

fn with(t: &mut ClientObjectTable, id: u32, f: impl FnOnce(&mut ClientObject)) {
    let mut o = t.get(id).unwrap().clone();
    f(&mut o);
    t.add_or_update(o, &mut Vec::new());
}

#[test]
fn your_own_character_lifts_your_main_pack_out_of_combat() {
    let (payload, ghost) = lift(
        PLAYER,
        PLAYER,
        CombatMode::NON_COMBAT,
        &world(),
        None,
        &mut Icons { drag: true },
    )
    .unwrap();
    let p = payload.get::<ItemDragPayload>().unwrap();
    assert_eq!(p.obj_id, PLAYER);
    assert_eq!(p.source_kind, ItemDragSource::Inventory);
    assert_eq!(p.source_slot, 0);
    let g = ghost.unwrap();
    assert_eq!(
        (g.texture, g.width, g.height),
        (TextureRef::Runtime(1), 32, 32)
    );
}

#[test]
fn without_a_drag_picture_the_lift_shows_the_pack_icon() {
    let (_, ghost) = lift(
        PLAYER,
        PLAYER,
        CombatMode::NON_COMBAT,
        &world(),
        None,
        &mut Icons { drag: false },
    )
    .unwrap();
    assert_eq!(ghost.unwrap().texture, TextureRef::Runtime(2));
}

#[test]
fn a_loose_item_on_the_ground_lifts_itself_as_a_ground_drag() {
    // GitHub #45: pressing an item on the ground and dragging it lifted
    // nothing, so it could not be dragged into a pack.
    let (payload, ghost) = lift(
        LOOT,
        PLAYER,
        CombatMode::NON_COMBAT,
        &world(),
        None,
        &mut Icons { drag: true },
    )
    .expect("a loose item lifts");
    let p = payload.get::<ItemDragPayload>().unwrap();
    assert_eq!(p.obj_id, LOOT);
    assert_eq!(p.source_kind, ItemDragSource::Ground);
    let g = ghost.unwrap();
    assert_eq!(
        (g.texture, g.width, g.height),
        (TextureRef::Runtime(3), 32, 32)
    );
    // Without a dedicated drag picture it shows its icon.
    let (_, ghost) = lift(
        LOOT,
        PLAYER,
        CombatMode::NON_COMBAT,
        &world(),
        None,
        &mut Icons { drag: false },
    )
    .unwrap();
    assert_eq!(ghost.unwrap().texture, TextureRef::Runtime(4));
}

#[test]
fn nothing_lifts_in_combat() {
    let icons = &mut Icons { drag: true };
    let t = world();
    for mode in [CombatMode::MELEE, CombatMode::MISSILE, CombatMode::MAGIC] {
        assert!(lift(PLAYER, PLAYER, mode, &t, None, icons).is_none());
        assert!(lift(LOOT, PLAYER, mode, &t, None, icons).is_none());
    }
}

#[test]
fn nothing_and_unknown_objects_lift_nothing() {
    let icons = &mut Icons { drag: true };
    let t = world();
    assert!(lift(0, PLAYER, CombatMode::NON_COMBAT, &t, None, icons).is_none());
    assert!(lift(0x7000_0999, PLAYER, CombatMode::NON_COMBAT, &t, None, icons).is_none());
}

#[test]
fn a_stuck_object_or_another_creature_lifts_nothing() {
    let icons = &mut Icons { drag: true };
    let mut t = world();
    with(&mut t, LOOT, |o| {
        o.public_weenie_bitfield = Some(PublicWeenieFlags::STUCK);
    });
    assert!(lift(LOOT, PLAYER, CombatMode::NON_COMBAT, &t, None, icons).is_none());
    let mut t = world();
    with(&mut t, LOOT, |o| o.item_type = ItemType::CREATURE);
    assert!(lift(LOOT, PLAYER, CombatMode::NON_COMBAT, &t, None, icons).is_none());
}

#[test]
fn a_wielded_item_lifts_nothing() {
    let icons = &mut Icons { drag: true };
    let mut t = world();
    with(&mut t, LOOT, |o| o.wielder_id = 0x5000_0B01);
    assert!(lift(LOOT, PLAYER, CombatMode::NON_COMBAT, &t, None, icons).is_none());
}

#[test]
fn an_item_with_a_request_in_flight_lifts_nothing() {
    let icons = &mut Icons { drag: true };
    let t = world();
    assert!(lift(LOOT, PLAYER, CombatMode::NON_COMBAT, &t, Some(LOOT), icons).is_none());
    // Another item's request does not hold this one.
    assert!(
        lift(
            LOOT,
            PLAYER,
            CombatMode::NON_COMBAT,
            &t,
            Some(0x7000_0002),
            icons
        )
        .is_some()
    );
}
