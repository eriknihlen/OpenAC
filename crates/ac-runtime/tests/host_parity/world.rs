//! The little world a scenario stages, written once and said by the
//! server to both arms alike (OpenAC `ParityWorld`, `ParityPlayerBody`):
//! the character, a monster a few metres out, a second beyond it, a dead
//! one and a harmless bystander, a metre or more apart along the x axis
//! east of the character, and a hidden one off to the south-east, out of
//! every route a scenario walks or runs.

use super::arm::Arm;
use super::wire::{self, Object};

/// The character (OpenAC `ParityWorld.Player`).
pub const PLAYER: u32 = 0x5000_0001;
pub const PLAYER_NAME: &str = "Parity";
/// The account as the server spells it, the world and how many are on it
/// (OpenAC `ParitySessionOperations`).
pub const ACCOUNT_NAME: &str = "Parity";
pub const WORLD_NAME: &str = "Parity Realm";
pub const SERVER_POPULATION: i32 = 317;
pub const MONSTER: u32 = 0x5000_0012;
pub const SECOND_MONSTER: u32 = 0x5000_0013;
pub const DEAD_MONSTER: u32 = 0x5000_0011;
pub const HIDDEN_MONSTER: u32 = 0x5000_0010;
pub const BYSTANDER: u32 = 0x5000_0020;
/// Another player, standing next to the character.
pub const OTHER_PLAYER: u32 = 0x5000_0021;
/// The character's patron, who is nowhere near it.
pub const PATRON: u32 = 0x5000_0002;
/// The monarch of the allegiance the character belongs to.
pub const MONARCH: u32 = 0x5000_0003;

/// The square of ground the scenarios stand on (OpenAC
/// `ParityPlayerBody.Landblock`), the cell the character is in and the
/// ground's height, metres.
pub const LANDBLOCK: u32 = 0xA9B4_0000;
pub const CELL: u32 = LANDBLOCK | 0x0001;
pub const GROUND_HEIGHT: f32 = 50.0;

/// Where the character stands inside its cell, metres.
pub const PLAYER_X: f32 = 96.0;
pub const PLAYER_Y: f32 = 97.0;

/// The setup every staged object is created with (OpenAC
/// `ParityWorld.Spawn`).
pub const SETUP: u32 = 0x0200_0001;

/// What the server says between a scenario's steps so the arm knows it has
/// all been handled; never recorded.
pub const SYNC_MARKER: &str = "parity-sync";

/// Item types (`ac-core` `ItemType`).
pub const CREATURE: u32 = 0x10;
pub const MISC: u32 = 0x80;
pub const CONTAINER: u32 = 0x200;
pub const GEM: u32 = 0x800;
pub const MONEY: u32 = 0x40;

/// Public description flags (`ac-core` `PublicWeenieFlags`).
pub const STUCK: u32 = 0x4;
pub const BF_PLAYER: u32 = 0x8;
pub const ATTACKABLE: u32 = 0x10;

/// Physics state bits (`ac-core` `PhysicsState`).
pub const ETHEREAL: u32 = 0x4;
pub const HIDDEN: u32 = 0x4000;

/// The outdoor cell `(x, y)` lies in, inside `landblock` (8 x 8 cells of
/// 24 metres).
pub fn outdoor_cell(landblock: u32, x: f32, y: f32) -> u32 {
    let column = (x / 24.0).clamp(0.0, 7.0) as u32;
    let row = (y / 24.0).clamp(0.0, 7.0) as u32;
    landblock | (1 + column * 8 + row)
}

/// The character as the server creates it on arrival: a player creature
/// with a main pack, standing on the ground.
pub fn player_object() -> Object {
    Object {
        position: Some((CELL, PLAYER_X, PLAYER_Y, GROUND_HEIGHT)),
        setup: Some(SETUP),
        object_description_flags: BF_PLAYER,
        items_capacity: Some(102),
        containers_capacity: Some(7),
        ..Object::new(PLAYER, PLAYER_NAME, CREATURE)
    }
}

/// Something on the ground `x` metres along from the character's spot.
pub fn on_the_ground(guid: u32, name: &str, item_type: u32, x: f32) -> Object {
    Object {
        position: Some((CELL, x, PLAYER_Y, GROUND_HEIGHT)),
        setup: Some(SETUP),
        ..Object::new(guid, name, item_type)
    }
}

/// A creature to swing at, fixed in place as the server's creatures are.
pub fn monster(guid: u32, x: f32) -> Object {
    Object {
        object_description_flags: ATTACKABLE | STUCK,
        ..on_the_ground(guid, &format!("Monster {guid:08X}"), CREATURE, x)
    }
}

/// A creature nothing may swing at.
pub fn bystander(guid: u32, x: f32) -> Object {
    Object {
        object_description_flags: STUCK,
        ..on_the_ground(guid, &format!("Bystander {guid:08X}"), CREATURE, x)
    }
}

/// The player, a monster three metres out, a second monster beyond it, a
/// dead one, a hidden one and a harmless bystander (OpenAC
/// `ParityWorld.Stage`).
pub fn stage(arm: &mut Arm) {
    // OpenAC stands the hidden one a metre out on the line east of the
    // character. Here it stands four metres east and four south, clear of
    // every scenario's route (the runs north, the turns, the walks east to
    // the creatures, west to the corpse and four metres south): a hidden
    // creature is still solid, only an ethereal one is passed through, and
    // on this slope a walk that brushes it can be pinned against it (the
    // step down lands the body inside its sphere, and the check that the
    // body fits refuses the move). Whether a walk brushes it depends on the
    // frame timing, so a walk past it re-planned on one arm and not the
    // other.
    let mut hidden = Object {
        physics_state: HIDDEN,
        ..monster(HIDDEN_MONSTER, PLAYER_X + 4.0)
    };
    if let Some((_, _, y, _)) = hidden.position.as_mut() {
        *y -= 4.0;
    }
    arm.say_grounded(&hidden);
    arm.say_grounded(&monster(DEAD_MONSTER, PLAYER_X + 2.0));
    arm.server
        .game_event(PLAYER, 0x01C0, &wire::update_health(DEAD_MONSTER, 0.0));
    arm.say_grounded(&monster(MONSTER, PLAYER_X + 3.0));
    arm.say_grounded(&monster(SECOND_MONSTER, PLAYER_X + 7.0));
    arm.say_grounded(&bystander(BYSTANDER, PLAYER_X + 4.0));
    arm.sync();
}

// ── things to carry, and things to open ─────────────────────────────

/// A healing kit: usable on its own, carried in the main pack.
pub const KIT: u32 = 0x5000_0040;
pub const KIT_ICON_UNDERLAY: u32 = 0x0600_5B0C;
pub const KIT_ICON_OVERLAY: u32 = 0x0600_6C0B;
pub const KIT_COVERAGE: u32 = 0x0000_0400;
/// Test data only: a real kit is not a hook and carries no hook bits.
/// The bits are copied whatever the item is, so the kit carries them to
/// check that they reach both records on both clients.
pub const KIT_HOOK_ITEM_TYPES: u32 = 0x0000_0080;
/// A side pack, carried, with room for four things.
pub const SIDE_PACK: u32 = 0x5000_0041;
/// A tinkering tool, which is what a salvage request needs.
pub const SALVAGE_TOOL: u32 = 0x5000_0042;
/// Something to salvage with it.
pub const SCRAP_ITEM: u32 = 0x5000_0043;
/// An item that cannot be used without naming a target.
pub const TARGETED_ITEM: u32 = 0x5000_0044;
/// What the scrap is made of (steel).
pub const STEEL_MATERIAL: u32 = 61;
/// A part-filled stack the character carries, of the coins in the corpse.
pub const CARRIED_COIN: u32 = 0x5000_0045;
/// What takes the last free slot in the side pack.
pub const SIDE_PACK_FILLER: u32 = 0x5000_0046;
/// A part-filled stack of the same coins, in the side pack.
pub const SIDE_PACK_COIN: u32 = 0x5000_0047;
/// A corpse lying on the ground, openable.
pub const CORPSE: u32 = 0x5000_0050;
/// Close enough to open without going anywhere first, metres.
pub const WITHIN_ARMS_REACH: f32 = 0.4;
/// Inside the corpse: something plain, and something worth a look.
pub const CORPSE_COIN: u32 = 0x5000_0051;
pub const CORPSE_GEM: u32 = 0x5000_0052;
/// A corpse three metres out and four metres up, out of every reach.
pub const CORPSE_OUT_OF_EVERY_REACH: u32 = 0x5000_0053;

pub const TINKERING_TOOL: u32 = 0x2000_0000;

/// Use rules (`ac-core` `ItemUseable`).
pub const USEABLE_CONTAINED: u32 = 0x8;
pub const USEABLE_REMOTE: u32 = 0x20;

/// Public flags of a corpse that can be opened.
pub const OPENABLE: u32 = 0x1;
pub const CORPSE_FLAG: u32 = 0x2000;

/// The pyreal's weenie class.
const PYREAL_CLASS: u32 = 0x0111;

/// Something carried in `container`.
pub fn carried(guid: u32, name: &str, useability: u32, container: u32) -> Object {
    Object {
        container: Some(container),
        stack_size: Some(1),
        useability: Some(useability),
        ..Object::new(guid, name, MISC)
    }
}

fn coins(guid: u32, container: u32, count: u16) -> Object {
    Object {
        container: Some(container),
        weenie_class_id: PYREAL_CLASS,
        stack_size: Some(count),
        max_stack_size: Some(25_000),
        useability: Some(USEABLE_CONTAINED),
        ..Object::new(guid, "Pyreal", MONEY)
    }
}

/// The character's pack room changes: the server sends its object again.
pub fn player_with_room(items: i8, packs: i8) -> Object {
    Object {
        items_capacity: Some(items),
        containers_capacity: Some(packs),
        ..player_object()
    }
}

/// Fills the character's packs: a kit to use, a side pack to move things
/// into, a tinkering tool and something to salvage with it, and an item
/// that refuses to be used without a target; the character has room for
/// ten things and two packs (OpenAC `ParityWorld.StageCarriedItems`).
pub fn carried_items() -> Vec<Vec<u8>> {
    let kit = Object {
        icon_underlay_id: Some(KIT_ICON_UNDERLAY),
        icon_overlay_id: Some(KIT_ICON_OVERLAY),
        priority: Some(KIT_COVERAGE),
        // Test data only (see `KIT_HOOK_ITEM_TYPES`).
        hook_item_types: Some(KIT_HOOK_ITEM_TYPES),
        plural_name: Some("Healing Kits".into()),
        ..carried(KIT, "Healing Kit", USEABLE_CONTAINED, PLAYER)
    };
    let side_pack = Object {
        weenie_class_id: 0x0834,
        items_capacity: Some(4),
        ..Object {
            item_type: CONTAINER,
            ..carried(SIDE_PACK, "Side Pack", USEABLE_CONTAINED, PLAYER)
        }
    };
    let tool = Object {
        item_type: TINKERING_TOOL,
        ..carried(SALVAGE_TOOL, "Tinkering Tool", USEABLE_CONTAINED, PLAYER)
    };
    let scrap = Object {
        material_type: Some(STEEL_MATERIAL),
        ..carried(SCRAP_ITEM, "Scrap", USEABLE_CONTAINED, PLAYER)
    };
    let stone = Object {
        target_type: Some(MISC),
        ..carried(
            TARGETED_ITEM,
            "Mana Stone",
            USEABLE_CONTAINED | (USEABLE_CONTAINED << 16),
            PLAYER,
        )
    };
    vec![
        // The character described again at the same incarnation: its item
        // record takes the room. (A re-sent description would create the
        // character anew, as the original client does.)
        player_with_room(10, 2).build(),
        kit.build(),
        side_pack.build(),
        tool.build(),
        scrap.build(),
        stone.build(),
    ]
}

/// Says the carried items and waits until they are handled.
pub fn stage_carried_items(arm: &mut Arm) {
    for message in carried_items() {
        arm.server.send(&message);
    }
    arm.sync();
}

/// The carried items, plus two part-filled stacks of one kind of coin: one
/// loose in the main pack and one in the side pack (OpenAC
/// `ParityWorld.StageTwoStacksOfOneKind`).
pub fn stage_two_stacks_of_one_kind(arm: &mut Arm) {
    for message in carried_items() {
        arm.server.send(&message);
    }
    arm.server.send(&coins(CARRIED_COIN, PLAYER, 40).build());
    arm.server
        .send(&coins(SIDE_PACK_COIN, SIDE_PACK, 250).build());
    // The listing the server sends for the pack.
    arm.server.game_event(
        PLAYER,
        0x0196,
        &wire::view_contents(SIDE_PACK, &[(SIDE_PACK_COIN, 0)]),
    );
    arm.sync();
}

/// The character with nowhere to put anything new, every item slot taken
/// and no room for another pack, while it carries a part-filled stack of
/// the coins in the corpse (OpenAC `ParityWorld.StageAFullInventoryOverAStack`).
pub fn stage_a_full_inventory_over_a_stack(arm: &mut Arm) {
    let mut messages = carried_items();
    // Room for exactly what is staged: five things and one pack in the
    // main pack, one thing in the side pack.
    messages[0] = player_with_room(5, 1).build();
    for message in messages {
        arm.server.send(&message);
    }
    arm.server.send(&coins(CARRIED_COIN, PLAYER, 250).build());
    let filler = carried(SIDE_PACK_FILLER, "Trinket", USEABLE_CONTAINED, SIDE_PACK);
    arm.server.send(&filler.build());
    let side_pack = Object {
        weenie_class_id: 0x0834,
        items_capacity: Some(1),
        ..Object {
            item_type: CONTAINER,
            ..carried(SIDE_PACK, "Side Pack", USEABLE_CONTAINED, PLAYER)
        }
    };
    arm.server.send(&side_pack.build_update());
    arm.server.game_event(
        PLAYER,
        0x0196,
        &wire::view_contents(SIDE_PACK, &[(SIDE_PACK_FILLER, 0)]),
    );
    arm.server.game_event(
        PLAYER,
        0x0196,
        &wire::view_contents(
            PLAYER,
            &[
                (KIT, 0),
                (SALVAGE_TOOL, 0),
                (SCRAP_ITEM, 0),
                (TARGETED_ITEM, 0),
                (CARRIED_COIN, 0),
                (SIDE_PACK, 1),
            ],
        ),
    );
    arm.sync();
}

/// A corpse on the ground `metres_out` along, with nothing visible in it
/// yet (OpenAC `ParityWorld.StageCorpse`).
pub fn corpse(metres_out: f32) -> Object {
    Object {
        items_capacity: Some(8),
        stack_size: Some(1),
        useability: Some(USEABLE_REMOTE),
        object_description_flags: CORPSE_FLAG | OPENABLE,
        ..on_the_ground(
            CORPSE,
            "Corpse of Monster",
            CONTAINER,
            PLAYER_X + metres_out,
        )
    }
}

pub fn stage_corpse(arm: &mut Arm, metres_out: f32) {
    arm.say_grounded(&corpse(metres_out));
    arm.sync();
}

/// A second corpse `metres_out` along and four metres up (OpenAC
/// `ParityWorld.StageCorpseOutOfEveryReach`, three metres out).
pub fn stage_corpse_out_of_every_reach(arm: &mut Arm, metres_out: f32) {
    let mut corpse = arm.grounded(Object {
        name: "Corpse on a Ledge".into(),
        ..Object {
            guid: CORPSE_OUT_OF_EVERY_REACH,
            weenie_class_id: CORPSE_OUT_OF_EVERY_REACH & 0xFFFF,
            ..corpse(metres_out)
        }
    });
    if let Some((_, _, _, z)) = corpse.position.as_mut() {
        *z += 4.0;
    }
    arm.server.send(&corpse.build());
    arm.sync();
}

/// The two things inside the corpse, as objects the server created
/// (OpenAC `ParityWorld.StageCorpseContents`).
pub fn corpse_contents() -> Vec<Vec<u8>> {
    let coin = Object {
        stack_size: Some(250),
        ..coins(CORPSE_COIN, CORPSE, 250)
    };
    let gem = Object {
        container: Some(CORPSE),
        weenie_class_id: 0x0222,
        stack_size: Some(1),
        useability: Some(USEABLE_CONTAINED),
        ..Object::new(CORPSE_GEM, "Gem", GEM)
    };
    vec![coin.build(), gem.build()]
}

pub fn stage_corpse_contents(arm: &mut Arm) {
    for message in corpse_contents() {
        arm.server.send(&message);
    }
    arm.sync();
}

/// What the server says once the corpse has been used: the two things
/// inside it and the listing (OpenAC `ParityWorld.DeliverCorpseContents`).
pub fn deliver_corpse_contents(arm: &mut Arm) {
    for message in corpse_contents() {
        arm.server.send(&message);
    }
    arm.server.game_event(
        PLAYER,
        0x0196,
        &wire::view_contents(CORPSE, &[(CORPSE_COIN, 0), (CORPSE_GEM, 0)]),
    );
}

/// Another player two metres out (OpenAC `ParityWorld.StageAnotherPlayer`).
pub fn stage_another_player(arm: &mut Arm) {
    let other = Object {
        object_description_flags: BF_PLAYER,
        ..on_the_ground(OTHER_PLAYER, "Another", CREATURE, PLAYER_X + 2.0)
    };
    arm.say_grounded(&other);
    arm.sync();
}
