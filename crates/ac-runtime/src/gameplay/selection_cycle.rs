//! Stepping the selection from one nearby thing of a kind to the next: the
//! keyboard's closest/next/previous item, compass item, monster, person and
//! unopened corpse.
//!
//! The order is the radar's: candidates sorted by how far away they are,
//! measured in the character's own frame with height counting for a little
//! more than distance across the ground, the object id breaking a tie so the
//! order never depends on which order the client heard about things.
//! "Previous" and "next" step in and out along that order and wrap round at
//! the ends. Who is in reach at all is decided on ground distance alone,
//! against the radar's range: height orders, it never excludes. Everything
//! it reads is session state, so every host steps through the same things
//! in the same order.

use ac_core::combat::CombatMode;
use ac_core::items::{ClientObject, PublicWeenieFlags};
use ac_core::physics::PhysicsState;
use ac_core::properties::PropertyInstanceId;
use glam::{Quat, Vec3};

use crate::world::World;

/// What a cycle steps through.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SelectionCycleKind {
    /// Things the radar does not show, and lifestones, portals and
    /// bindstones.
    Item,
    /// What the compass shows (in melee or missile mode, only what can be
    /// attacked).
    CompassItem,
    /// Creatures that can be attacked, not fellows or vendors.
    Monster,
    /// Other people.
    Player,
    /// Corpses not opened this session.
    UnopenedCorpse,
}

/// What a cycle reads besides the world.
pub struct CycleFacts<'a> {
    /// The combat mode (the compass narrows in melee and missile mode).
    pub combat_mode: CombatMode,
    /// A fellow of the player.
    pub is_fellow: &'a dyn Fn(u32) -> bool,
    /// A creature that may be attacked.
    pub is_attackable: &'a dyn Fn(u32) -> bool,
    /// A corpse the player opened this session.
    pub corpse_opened: &'a dyn Fn(u32) -> bool,
    /// Leave out what the player carries or wears.
    pub exclude_owned_by_player: bool,
}

impl std::fmt::Debug for CycleFacts<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CycleFacts")
            .field("combat_mode", &self.combat_mode)
            .field("exclude_owned_by_player", &self.exclude_owned_by_player)
            .finish_non_exhaustive()
    }
}

impl CycleFacts<'_> {
    /// No fellows, nothing attackable, no corpse opened, peace.
    #[must_use]
    pub fn none() -> CycleFacts<'static> {
        CycleFacts {
            combat_mode: CombatMode::NON_COMBAT,
            is_fellow: &|_| false,
            is_attackable: &|_| false,
            corpse_opened: &|_| false,
            exclude_owned_by_player: false,
        }
    }
}

/// Which way a cycle steps.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SelectionCycleDirection {
    /// The nearest candidate, wherever the selection is now.
    Closest,
    /// The candidate just inside the current one.
    Previous,
    /// The candidate just outside the current one.
    Next,
}

/// How much a metre of height counts for in the cycle's ordering, against a
/// metre across the ground.
pub const SELECTION_HEIGHT_WEIGHT: f32 = 1.2;
/// How far the radar reaches outside, metres.
const OUTDOOR_RADAR_METERS: f32 = 75.0;
/// How far the radar reaches inside a building, metres.
const INDOOR_RADAR_METERS: f32 = 25.0;
/// The metres one landblock spans.
const LANDBLOCK_METERS: f32 = 192.0;

/// The other character the step lands on, or `None` when none is within
/// radar range. `anchor` is where the step starts (the selection, else the
/// previous one).
#[must_use]
pub fn find_player(
    world: &World,
    direction: SelectionCycleDirection,
    anchor: Option<u32>,
) -> Option<u32> {
    find_target(
        world,
        SelectionCycleKind::Player,
        direction,
        anchor,
        &CycleFacts::none(),
    )
}

/// The thing of `kind` the step lands on, or `None` when none is within
/// radar range. `anchor` is where the step starts (the selection, else the
/// previous one).
#[must_use]
pub fn find_target(
    world: &World,
    kind: SelectionCycleKind,
    direction: SelectionCycleDirection,
    anchor: Option<u32>,
    facts: &CycleFacts<'_>,
) -> Option<u32> {
    let player = world.player_guid()?;
    let me = world.object(player)?.position()?;
    let here = global(me);
    let facing = quat(me);
    let radar = radar_meters(me.cell_id);
    let mut candidates: Vec<(f32, u32)> = Vec::new();
    for object in world.objects() {
        let guid = object.guid();
        let (Some(position), Some(item)) = (object.position(), world.item(guid)) else {
            continue;
        };
        if guid == 0
            || guid == player
            || (facts.exclude_owned_by_player && world.items().is_owned_by_object(guid, player))
        {
            continue;
        }
        // Reach is ground distance alone; height only orders the candidates,
        // so a monster below in a pit is still in the cycle.
        let offset = player_space_offset(here, facing, global(position));
        if !is_within_selection_cycle_range(offset, radar)
            || !matches_kind(kind, guid, item, object.physics().state, facts)
        {
            continue;
        }
        candidates.push((selection_order(offset), guid));
    }
    if candidates.is_empty() {
        return None;
    }
    candidates.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)));
    if direction == SelectionCycleDirection::Closest {
        return Some(candidates[0].1);
    }
    let anchor_key = anchor.and_then(|guid| {
        let position = world.object(guid)?.position()?;
        Some((
            selection_order(player_space_offset(here, facing, global(position))),
            guid,
        ))
    });
    let Some(anchor_key) = anchor_key else {
        return Some(if direction == SelectionCycleDirection::Previous {
            candidates[candidates.len() - 1].1
        } else {
            candidates[0].1
        });
    };
    let compare = |c: &(f32, u32)| c.0.total_cmp(&anchor_key.0).then(c.1.cmp(&anchor_key.1));
    if direction == SelectionCycleDirection::Next {
        return candidates
            .iter()
            .find(|c| compare(c).is_gt())
            .or(candidates.first())
            .map(|c| c.1);
    }
    candidates
        .iter()
        .rev()
        .find(|c| compare(c).is_lt())
        .or(candidates.last())
        .map(|c| c.1)
}

/// The creature attacking the player, when the server named one and it is
/// strictly inside the radar's range across the ground.
#[must_use]
pub fn find_last_attacker(world: &World) -> Option<u32> {
    let player = world.player_guid()?;
    let attacker = world
        .item(player)?
        .properties
        .instance_ids
        .get(&PropertyInstanceId::CURRENT_ATTACKER.0)
        .copied()
        .filter(|&id| id != 0)?;
    let me = world.object(player)?.position()?;
    let them = world.object(attacker)?.position()?;
    is_within_radar_range(
        player_space_offset(global(me), quat(me), global(them)),
        radar_meters(me.cell_id),
    )
    .then_some(attacker)
}

/// Out in the world rather than in a pack, not hidden, and of the kind.
fn matches_kind(
    kind: SelectionCycleKind,
    guid: u32,
    item: &ClientObject,
    physics_state: u32,
    facts: &CycleFacts<'_>,
) -> bool {
    let flags = item.public_weenie_bitfield.map_or(0, |f| f.bits());
    if item.container_id != 0
        || physics_state & PhysicsState::CLOAKED.bits() != 0
        || flags & PublicWeenieFlags::UI_HIDDEN.bits() != 0
    {
        return false;
    }
    let has = |flag: PublicWeenieFlags| flags & flag.bits() != 0;
    // Shown on the radar: when moving, when attacking, or always.
    let showable_on_radar = matches!(item.radar_behavior, Some(2..=4));
    let special_compass_object = has(PublicWeenieFlags::LIFESTONE)
        || has(PublicWeenieFlags::PORTAL)
        || has(PublicWeenieFlags::BINDSTONE);
    let combat_compass = matches!(facts.combat_mode, CombatMode::MELEE | CombatMode::MISSILE);
    match kind {
        SelectionCycleKind::Item => {
            matches!(item.radar_behavior, None | Some(0)) || special_compass_object
        }
        SelectionCycleKind::CompassItem => {
            (special_compass_object || showable_on_radar)
                && (!combat_compass
                    || ((facts.is_attackable)(guid)
                        && !(facts.is_fellow)(guid)
                        && !has(PublicWeenieFlags::VENDOR)
                        && physics_state & PhysicsState::REPORT_AS_ENVIRONMENT.bits() == 0))
        }
        SelectionCycleKind::Monster => {
            showable_on_radar
                && (facts.is_attackable)(guid)
                && !(facts.is_fellow)(guid)
                && !has(PublicWeenieFlags::VENDOR)
        }
        SelectionCycleKind::Player => showable_on_radar && has(PublicWeenieFlags::PLAYER),
        SelectionCycleKind::UnopenedCorpse => {
            has(PublicWeenieFlags::CORPSE) && !(facts.corpse_opened)(guid)
        }
    }
}

/// How far the radar reaches from `cell`.
fn radar_meters(cell: u32) -> f32 {
    if is_outdoor_cell(cell) {
        OUTDOOR_RADAR_METERS
    } else {
        INDOOR_RADAR_METERS
    }
}

/// Where something stands relative to the character, in the character's own
/// frame, in metres.
fn player_space_offset(here: Vec3, facing: Quat, there: Vec3) -> Vec3 {
    facing.inverse() * (there - here)
}

/// Distance across the ground, in metres, of an offset in the character's
/// own frame. Height plays no part.
#[must_use]
pub fn horizontal_distance(offset: Vec3) -> f32 {
    (offset.x * offset.x + offset.y * offset.y).sqrt()
}

/// Whether the next/previous/closest keys may land on something at this
/// offset: its distance across the ground, not counting height, is at most
/// the radar's range. A monster ten metres down a pit is in reach as long as
/// it is close enough across the ground.
#[must_use]
pub fn is_within_selection_cycle_range(offset: Vec3, radar_meters: f32) -> bool {
    horizontal_distance(offset) <= radar_meters
}

/// Whether "select last attacker" may land on something at this offset: its
/// distance across the ground is strictly inside the radar's range.
#[must_use]
pub fn is_within_radar_range(offset: Vec3, radar_meters: f32) -> bool {
    horizontal_distance(offset) < radar_meters
}

/// The cycle's ordering key: distance across the ground plus
/// [`SELECTION_HEIGHT_WEIGHT`] times the height difference, so something a
/// floor up sorts behind something on this floor. It orders candidates only;
/// reach is [`is_within_selection_cycle_range`], on ground distance alone.
#[must_use]
pub fn selection_order(offset: Vec3) -> f32 {
    horizontal_distance(offset) + offset.z.abs() * SELECTION_HEIGHT_WEIGHT
}

/// An outdoor cell: the radar reaches three times as far outside.
fn is_outdoor_cell(cell: u32) -> bool {
    cell == 0 || (cell & 0xFFFF) < 0x100
}

/// A position in absolute metres.
fn global(p: &ac_proto::Position) -> Vec3 {
    Vec3::new(
        f32::from((p.cell_id >> 24) as u8) * LANDBLOCK_METERS + p.origin.x,
        f32::from((p.cell_id >> 16) as u8) * LANDBLOCK_METERS + p.origin.y,
        p.origin.z,
    )
}

fn quat(p: &ac_proto::Position) -> Quat {
    let q = p.orientation;
    let q = Quat::from_xyzw(q.x, q.y, q.z, q.w);
    if q.length_squared() > 0.0 {
        q.normalize()
    } else {
        Quat::IDENTITY
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.HostParity.Tests/Scenarios/SelectionCycleParityTests.cs`
    //! (three people in a line out from the character, a creature that is
    //! no person).

    use super::*;
    use crate::world::tests::{create, position, stamps};
    use ac_core::items::ItemType;
    use ac_proto::{Inbound, PublicWeenieDesc};

    const PLAYER: u32 = 0x5000_000A;
    const NEAR: u32 = 0x5000_0061;
    const MIDDLE: u32 = 0x5000_0062;
    const FAR: u32 = 0x5000_0063;
    const CELL: u32 = 0xA9B4_0001;

    fn place(world: &mut World, guid: u32, x: f32, desc: Option<PublicWeenieDesc>) {
        place_with_state(world, guid, x, desc, 0);
    }

    fn place_with_state(
        world: &mut World,
        guid: u32,
        x: f32,
        desc: Option<PublicWeenieDesc>,
        physics_state: u32,
    ) {
        let Inbound::CreateObject(mut create) = create(guid, stamps(1, 1), Some(position(CELL, x)))
        else {
            unreachable!()
        };
        create.weenie = desc;
        create.physics.state = physics_state;
        world.apply(&Inbound::CreateObject(create));
    }

    fn staged_without_drudge() -> World {
        let mut world = World::new();
        world.set_player(PLAYER);
        place(&mut world, PLAYER, 10.0, person("Me"));
        world
    }

    fn person(name: &str) -> Option<PublicWeenieDesc> {
        Some(PublicWeenieDesc {
            name: name.into(),
            item_type: ac_core::items::ItemType::CREATURE.bits(),
            object_description_flags: PublicWeenieFlags::PLAYER.bits(),
            radar_behavior: Some(4),
            ..PublicWeenieDesc::default()
        })
    }

    fn staged() -> World {
        let mut world = World::new();
        world.set_player(PLAYER);
        place(&mut world, PLAYER, 10.0, person("Me"));
        place(
            &mut world,
            0x7000_0001,
            11.0,
            Some(PublicWeenieDesc {
                name: "Drudge".into(),
                item_type: ac_core::items::ItemType::CREATURE.bits(),
                radar_behavior: Some(4),
                ..PublicWeenieDesc::default()
            }),
        );
        world
    }

    #[test]
    fn stepping_out_and_back_lands_on_the_same_people_in_order() {
        let mut world = staged();
        place(&mut world, NEAR, 12.0, person("Near"));
        place(&mut world, MIDDLE, 15.0, person("Middle"));
        place(&mut world, FAR, 19.0, person("Far"));
        use SelectionCycleDirection::{Closest, Next, Previous};
        assert_eq!(find_player(&world, Next, None), Some(NEAR));
        assert_eq!(find_player(&world, Next, Some(NEAR)), Some(MIDDLE));
        assert_eq!(find_player(&world, Next, Some(MIDDLE)), Some(FAR));
        // Past the last one wraps round, and back in wraps the other way.
        assert_eq!(find_player(&world, Next, Some(FAR)), Some(NEAR));
        assert_eq!(find_player(&world, Previous, Some(NEAR)), Some(FAR));
        assert_eq!(find_player(&world, Previous, None), Some(FAR));
        assert_eq!(find_player(&world, Closest, Some(FAR)), Some(NEAR));
    }

    // OpenAC `tests/AcDream.App.Tests/Interaction/WorldSelectionQueryTests.cs`
    // (the Retail*Selection cases), over the runtime's world.

    const SHOW_ALWAYS: u8 = 4;

    fn thing(
        item_type: ItemType,
        flags: PublicWeenieFlags,
        radar: Option<u8>,
    ) -> Option<PublicWeenieDesc> {
        Some(PublicWeenieDesc {
            name: "Thing".into(),
            item_type: item_type.bits(),
            object_description_flags: flags.bits(),
            radar_behavior: radar,
            ..PublicWeenieDesc::default()
        })
    }

    fn closest(world: &World, kind: SelectionCycleKind, facts: &CycleFacts<'_>) -> Option<u32> {
        find_target(world, kind, SelectionCycleDirection::Closest, None, facts)
    }

    fn attackable_creature(world: &World) -> impl Fn(u32) -> bool + '_ {
        |guid| {
            world.item(guid).is_some_and(|i| {
                i.item_type.intersects(ItemType::CREATURE)
                    && i.public_weenie_bitfield
                        .is_some_and(|f| f.intersects(PublicWeenieFlags::ATTACKABLE))
            })
        }
    }

    #[test]
    fn the_item_keys_take_what_the_radar_does_not_show_and_special_objects() {
        // RetailItemSelectionUsesRadarAndSpecialObjectRules.
        const RADAR_ITEM: u32 = 0x7000_0020;
        const ORDINARY: u32 = 0x7000_0021;
        const PORTAL: u32 = 0x7000_0022;
        let mut world = staged();
        let none = PublicWeenieFlags::NONE;
        place(
            &mut world,
            RADAR_ITEM,
            11.0,
            thing(ItemType::MISC, none, Some(SHOW_ALWAYS)),
        );
        place(
            &mut world,
            ORDINARY,
            12.0,
            thing(ItemType::MISC, none, None),
        );
        place(
            &mut world,
            PORTAL,
            13.0,
            thing(ItemType::MISC, PublicWeenieFlags::PORTAL, Some(SHOW_ALWAYS)),
        );
        let facts = CycleFacts::none();
        assert_eq!(
            closest(&world, SelectionCycleKind::Item, &facts),
            Some(ORDINARY)
        );
        let mut carried = thing(ItemType::MISC, none, None);
        carried.as_mut().unwrap().container_id = Some(PLAYER);
        place(&mut world, ORDINARY, 12.0, carried);
        assert_eq!(
            closest(&world, SelectionCycleKind::Item, &facts),
            Some(PORTAL)
        );
    }

    #[test]
    fn the_compass_keys_narrow_to_what_can_be_fought_in_melee_and_missile() {
        // RetailCompassSelectionChangesPredicateInPhysicalCombat.
        const PEACEFUL: u32 = 0x7000_0030;
        const FELLOW: u32 = 0x7000_0031;
        const VENDOR: u32 = 0x7000_0032;
        const ENVIRONMENT: u32 = 0x7000_0033;
        const HOSTILE: u32 = 0x7000_0034;
        let mut world = staged_without_drudge();
        let attackable = PublicWeenieFlags::ATTACKABLE;
        place(
            &mut world,
            PEACEFUL,
            11.0,
            thing(ItemType::MISC, PublicWeenieFlags::NONE, Some(SHOW_ALWAYS)),
        );
        place(
            &mut world,
            FELLOW,
            12.0,
            thing(ItemType::CREATURE, attackable, Some(SHOW_ALWAYS)),
        );
        place(
            &mut world,
            VENDOR,
            13.0,
            thing(
                ItemType::CREATURE,
                attackable | PublicWeenieFlags::VENDOR,
                Some(SHOW_ALWAYS),
            ),
        );
        place_with_state(
            &mut world,
            ENVIRONMENT,
            14.0,
            thing(ItemType::CREATURE, attackable, Some(SHOW_ALWAYS)),
            PhysicsState::REPORT_COLLISIONS.bits() | PhysicsState::REPORT_AS_ENVIRONMENT.bits(),
        );
        place(
            &mut world,
            HOSTILE,
            15.0,
            thing(ItemType::CREATURE, attackable, Some(SHOW_ALWAYS)),
        );
        let is_attackable = attackable_creature(&world);
        let is_fellow = |guid| guid == FELLOW;
        let facts = |combat_mode| CycleFacts {
            combat_mode,
            is_fellow: &is_fellow,
            is_attackable: &is_attackable,
            corpse_opened: &|_| false,
            exclude_owned_by_player: false,
        };
        let compass = SelectionCycleKind::CompassItem;
        assert_eq!(
            closest(&world, compass, &facts(CombatMode::NON_COMBAT)),
            Some(PEACEFUL)
        );
        assert_eq!(
            closest(&world, compass, &facts(CombatMode::MELEE)),
            Some(HOSTILE)
        );
        assert_eq!(
            closest(&world, compass, &facts(CombatMode::MAGIC)),
            Some(PEACEFUL)
        );
    }

    #[test]
    fn the_monster_keys_skip_fellows_and_vendors() {
        // RetailMonsterSelectionUsesObjectIsAttackableAndRejectsFellowsAndVendors.
        const FELLOW: u32 = 0x7000_0040;
        const VENDOR: u32 = 0x7000_0041;
        const HOSTILE: u32 = 0x7000_0042;
        let mut world = staged_without_drudge();
        let attackable = PublicWeenieFlags::ATTACKABLE;
        place(
            &mut world,
            FELLOW,
            11.0,
            thing(ItemType::CREATURE, attackable, Some(SHOW_ALWAYS)),
        );
        place(
            &mut world,
            VENDOR,
            12.0,
            thing(
                ItemType::CREATURE,
                attackable | PublicWeenieFlags::VENDOR,
                Some(SHOW_ALWAYS),
            ),
        );
        place(
            &mut world,
            HOSTILE,
            13.0,
            thing(ItemType::CREATURE, attackable, Some(SHOW_ALWAYS)),
        );
        let is_attackable = attackable_creature(&world);
        let facts = CycleFacts {
            is_fellow: &|guid| guid == FELLOW,
            is_attackable: &is_attackable,
            ..CycleFacts::none()
        };
        assert_eq!(
            closest(&world, SelectionCycleKind::Monster, &facts),
            Some(HOSTILE)
        );
    }

    #[test]
    fn no_key_lands_on_an_object_hidden_from_the_ui() {
        // A trap hidden from the UI (bit 0x80 of the description) is never
        // a candidate, whatever the key; the top bit of the field is no
        // such mark.
        const TRAP: u32 = 0x7000_0070;
        const MARKED: u32 = 0x7000_0071;
        let mut world = staged_without_drudge();
        let hidden = PublicWeenieFlags::ATTACKABLE | PublicWeenieFlags::UI_HIDDEN;
        place(
            &mut world,
            TRAP,
            11.0,
            thing(ItemType::CREATURE, hidden, Some(SHOW_ALWAYS)),
        );
        {
            let is_attackable = attackable_creature(&world);
            let facts = CycleFacts {
                is_attackable: &is_attackable,
                ..CycleFacts::none()
            };
            for kind in [SelectionCycleKind::CompassItem, SelectionCycleKind::Monster] {
                assert_eq!(closest(&world, kind, &facts), None, "{kind:?}");
            }
        }
        place(
            &mut world,
            MARKED,
            12.0,
            thing(
                ItemType::CREATURE,
                PublicWeenieFlags::ATTACKABLE | PublicWeenieFlags(0x8000_0000),
                Some(SHOW_ALWAYS),
            ),
        );
        let is_attackable = attackable_creature(&world);
        let facts = CycleFacts {
            is_attackable: &is_attackable,
            ..CycleFacts::none()
        };
        assert_eq!(
            closest(&world, SelectionCycleKind::Monster, &facts),
            Some(MARKED)
        );
    }

    #[test]
    fn the_corpse_keys_skip_a_corpse_once_opened() {
        // RetailUnopenedCorpseSelectionRemembersOpenUntilDelete.
        const CORPSE: u32 = 0x7000_0050;
        let mut world = staged_without_drudge();
        place(
            &mut world,
            CORPSE,
            11.0,
            thing(ItemType::CONTAINER, PublicWeenieFlags::CORPSE, None),
        );
        let mut opened = ac_core::items::ExternalContainerState::new();
        let kind = SelectionCycleKind::UnopenedCorpse;
        let facts_for = |opened: &ac_core::items::ExternalContainerState| {
            closest(
                &world,
                kind,
                &CycleFacts {
                    corpse_opened: &|guid| opened.has_corpse_been_opened(guid),
                    ..CycleFacts::none()
                },
            )
        };
        assert_eq!(facts_for(&opened), Some(CORPSE));
        opened.request_open(CORPSE, true);
        assert_eq!(facts_for(&opened), None);
        assert!(opened.set_corpse_deleted(CORPSE));
        assert_eq!(facts_for(&opened), Some(CORPSE));
    }

    #[test]
    fn the_last_attacker_is_the_one_the_server_named_within_radar_range() {
        // OpenAC `WorldSelectionQuery.FindLastAttacker` (the player's
        // CurrentAttacker instance id, within radar range).
        const NEAR_ATTACKER: u32 = 0x7000_0060;
        const FAR_ATTACKER: u32 = 0x7000_0061;
        let mut world = staged_without_drudge();
        assert_eq!(find_last_attacker(&world), None);
        for (guid, x) in [(NEAR_ATTACKER, 30.0), (FAR_ATTACKER, 100.0)] {
            place(
                &mut world,
                guid,
                x,
                thing(
                    ItemType::CREATURE,
                    PublicWeenieFlags::ATTACKABLE,
                    Some(SHOW_ALWAYS),
                ),
            );
        }
        let attacked_by = |world: &mut World, sequence, attacker| {
            world.apply(&Inbound::PrivateUpdatePropertyInstanceId(
                ac_proto::PrivateUpdatePropertyInstanceId {
                    sequence,
                    property: PropertyInstanceId::CURRENT_ATTACKER.0,
                    value: attacker,
                },
            ));
        };
        attacked_by(&mut world, 1, NEAR_ATTACKER);
        assert_eq!(find_last_attacker(&world), Some(NEAR_ATTACKER));
        // 90 m away, past the outdoor radar's 75 m.
        attacked_by(&mut world, 2, FAR_ATTACKER);
        assert_eq!(find_last_attacker(&world), None);
    }

    #[test]
    fn a_creature_is_no_person_and_the_radar_range_bounds_the_cycle() {
        let mut world = staged();
        assert_eq!(
            find_player(&world, SelectionCycleDirection::Next, None),
            None
        );
        // Further than the outdoor radar reaches (75 m).
        place(&mut world, FAR, 90.0, person("Far"));
        assert_eq!(
            find_player(&world, SelectionCycleDirection::Next, None),
            None
        );
    }

    // OpenAC issue #220: reach is ground distance against the
    // radar's range; height only weighs into the order.

    /// Place `guid` at this offset from the player (who stands at x 10,
    /// y 10, height 0), metres.
    fn place_offset(world: &mut World, guid: u32, offset: Vec3, desc: Option<PublicWeenieDesc>) {
        let mut at = position(CELL, 10.0 + offset.x);
        at.origin.y += offset.y;
        at.origin.z += offset.z;
        let Inbound::CreateObject(mut create) = create(guid, stamps(1, 1), Some(at)) else {
            unreachable!()
        };
        create.weenie = desc;
        world.apply(&Inbound::CreateObject(create));
    }

    fn monster() -> Option<PublicWeenieDesc> {
        thing(
            ItemType::CREATURE,
            PublicWeenieFlags::ATTACKABLE,
            Some(SHOW_ALWAYS),
        )
    }

    // MonsterCycleReachesMonstersFarBelowAndAboveOnTheRadar.
    #[test]
    fn the_monster_cycle_reaches_monsters_far_below_and_above_on_the_radar() {
        const LEVEL: u32 = 0x7000_0060;
        const BELOW: u32 = 0x7000_0061;
        const ABOVE: u32 = 0x7000_0062;
        const BEYOND: u32 = 0x7000_0063;
        let mut world = staged_without_drudge();
        // Ground 10 m, no height: order 10.
        place_offset(&mut world, LEVEL, Vec3::new(10.0, 0.0, 0.0), monster());
        // Ground 40 m, 30 m down a pit: order 40 + 1.2 * 30 = 76.
        place_offset(&mut world, BELOW, Vec3::new(40.0, 0.0, -30.0), monster());
        // Ground 20 m, 50 m up: order 20 + 1.2 * 50 = 80.
        place_offset(&mut world, ABOVE, Vec3::new(0.0, 20.0, 50.0), monster());
        // Ground 76 m: off the radar however level it is.
        place_offset(&mut world, BEYOND, Vec3::new(76.0, 0.0, 0.0), monster());
        let is_attackable = attackable_creature(&world);
        let facts = CycleFacts {
            is_attackable: &is_attackable,
            ..CycleFacts::none()
        };
        let step = |direction, anchor| {
            find_target(
                &world,
                SelectionCycleKind::Monster,
                direction,
                anchor,
                &facts,
            )
        };
        use SelectionCycleDirection::{Next, Previous};
        assert_eq!(step(Next, None), Some(LEVEL));
        assert_eq!(step(Next, Some(LEVEL)), Some(BELOW));
        assert_eq!(step(Next, Some(BELOW)), Some(ABOVE));
        assert_eq!(step(Next, Some(ABOVE)), Some(LEVEL));
        assert_eq!(step(Previous, Some(LEVEL)), Some(ABOVE));
    }

    // MonsterCycleRangeIsGroundDistanceInclusiveOfTheRadarEdge.
    #[test]
    fn the_monster_cycle_range_is_ground_distance_inclusive_of_the_radar_edge() {
        const EDGE: u32 = 0x7000_0064;
        let mut world = staged_without_drudge();
        // Exactly on the 75 m edge across the ground, 10 m below.
        place_offset(&mut world, EDGE, Vec3::new(0.0, -75.0, -10.0), monster());
        let is_attackable = attackable_creature(&world);
        let facts = CycleFacts {
            is_attackable: &is_attackable,
            ..CycleFacts::none()
        };
        assert_eq!(
            closest(&world, SelectionCycleKind::Monster, &facts),
            Some(EDGE)
        );
    }

    // LastAttackerFarBelowIsSelectedWhileInsideTheRadarAcrossTheGround.
    #[test]
    fn the_last_attacker_far_below_is_selected_while_inside_the_radar_across_the_ground() {
        const BELOW: u32 = 0x7000_0065;
        const ON_THE_EDGE: u32 = 0x7000_0066;
        let mut world = staged_without_drudge();
        place_offset(&mut world, BELOW, Vec3::new(40.0, 0.0, -30.0), monster());
        place_offset(
            &mut world,
            ON_THE_EDGE,
            Vec3::new(75.0, 0.0, 0.0),
            monster(),
        );
        let attacked_by = |world: &mut World, sequence, attacker| {
            world.apply(&Inbound::PrivateUpdatePropertyInstanceId(
                ac_proto::PrivateUpdatePropertyInstanceId {
                    sequence,
                    property: PropertyInstanceId::CURRENT_ATTACKER.0,
                    value: attacker,
                },
            ));
        };
        attacked_by(&mut world, 1, BELOW);
        assert_eq!(find_last_attacker(&world), Some(BELOW));
        // Exactly on the edge is outside for this one: the range is strict.
        attacked_by(&mut world, 2, ON_THE_EDGE);
        assert_eq!(find_last_attacker(&world), None);
    }

    // OpenAC issue #220 `RetailRadarTests`
    // (SelectionCycleReach_IsGroundDistanceInclusive): height never takes a
    // candidate out of reach, only ground distance does.
    #[test]
    fn selection_cycle_reach_is_ground_distance_inclusive() {
        for (x, y, z, range, expected) in [
            (10.0, 0.0, 0.0, 25.0, true),
            (20.0, 0.0, -30.0, 25.0, true),
            (0.0, 20.0, 40.0, 25.0, true),
            // Ground exactly 25: inclusive.
            (15.0, 20.0, -12.0, 25.0, true),
            (25.01, 0.0, 0.0, 25.0, false),
            (40.0, 0.0, -30.0, 75.0, true),
            (76.0, 0.0, 0.0, 75.0, false),
        ] {
            assert_eq!(
                is_within_selection_cycle_range(Vec3::new(x, y, z), range),
                expected,
                "{x} {y} {z} {range}"
            );
        }
    }

    // RadarRange_ForLastAttackerIsGroundDistanceStrict.
    #[test]
    fn the_last_attacker_range_is_ground_distance_strict() {
        assert!(is_within_radar_range(Vec3::new(20.0, 0.0, -30.0), 25.0));
        assert!(!is_within_radar_range(Vec3::new(15.0, 20.0, 0.0), 25.0));
    }

    // SelectionOrder_IsGroundDistancePlusOnePointTwoTimesHeight.
    #[test]
    fn the_selection_order_is_ground_distance_plus_one_point_two_times_height() {
        assert!((selection_order(Vec3::new(3.0, 4.0, -10.0)) - 17.0).abs() < 1e-4);
        assert!((selection_order(Vec3::new(3.0, 4.0, 10.0)) - 17.0).abs() < 1e-4);
    }
}
