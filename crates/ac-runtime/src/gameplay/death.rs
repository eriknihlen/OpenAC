//! The player's own death as the client sees it: the death message, the
//! vitae penalty that follows (and its recovery), and where the last corpse
//! outside lies. The respawn at the lifestone is the server's teleport,
//! which portal space carries like any other.

use ac_core::player::{VitaeStatus, vitae_status, vitae_text};
use ac_core::properties::PropertyInt;
use ac_core::spells::get_vitae_multiplier;

use crate::world::World;

/// The saved-position slot of the player's last death outside (where the
/// corpse lies).
pub const LAST_OUTSIDE_DEATH_POSITION: u32 = 0x0E;

/// The vitae penalty as a host shows it.
#[derive(Debug, Clone, PartialEq)]
pub struct VitaeView {
    /// The multiplier, percent lost and the experience to the next percent.
    pub status: VitaeStatus,
    /// The vitae panel's text.
    pub text: String,
}

/// The vitae penalty now: the vitae enchantment's multiplier, the
/// experience pooled toward the next percent, measured at the level the
/// player died at (their level when the server sent none).
#[must_use]
pub fn vitae_view(world: &World) -> VitaeView {
    let multiplier = world
        .spellbook()
        .map_or(1.0, |book| get_vitae_multiplier(book.active_enchantments()));
    let player = world.player_guid().and_then(|guid| world.item(guid));
    let int = |key: PropertyInt| player.and_then(|p| p.properties.ints.get(&key.0).copied());
    let pool = int(PropertyInt::VITAE_CP_POOL).unwrap_or(0);
    let level = int(PropertyInt::DEATH_LEVEL)
        .or_else(|| int(PropertyInt::LEVEL))
        .unwrap_or(0);
    let status = vitae_status(multiplier, pool, level);
    VitaeView {
        text: vitae_text(&status),
        status,
    }
}

/// The cell of the player's last corpse outside, when the server told.
#[must_use]
pub fn last_outside_corpse_cell(world: &World) -> Option<u32> {
    world
        .player()
        .and_then(|p| p.positions().get(&LAST_OUTSIDE_DEATH_POSITION))
        .map(|position| position.cell_id)
}

/// A corpse lying in the world, as a host lists it.
#[derive(Debug, Clone, PartialEq)]
pub struct CorpseSummary {
    /// The corpse.
    pub guid: u32,
    /// Its name ("Corpse of ...").
    pub name: String,
    /// Straight-line distance from the player, metres.
    pub distance: f32,
    /// The player has opened it.
    pub opened: bool,
    /// It is the player's own.
    pub own: bool,
}

/// The corpses lying in the world within `maximum_distance` metres of the
/// player, nearest first (ties by id): objects marked as corpses that no
/// container holds and whose place is known.
#[must_use]
pub fn corpses(
    world: &World,
    external: &ac_core::items::ExternalContainerState,
    maximum_distance: f32,
) -> Vec<CorpseSummary> {
    use ac_core::items::PublicWeenieFlags;
    let Some(here) = world
        .player_guid()
        .and_then(|guid| world.object(guid))
        .and_then(|o| o.position().copied())
    else {
        return Vec::new();
    };
    let own_name = world
        .player_guid()
        .and_then(|guid| world.name(guid))
        .map(|name| format!("Corpse of {name}"));
    let mut list: Vec<CorpseSummary> = world
        .items()
        .objects()
        .filter(|item| {
            item.public_weenie_bitfield
                .is_some_and(|f| f.intersects(PublicWeenieFlags::CORPSE))
                && item.container_id == 0
        })
        .filter_map(|item| {
            let place = world.object(item.object_id())?.position()?;
            let metres = crate::world::distance(place, &here);
            (metres <= maximum_distance).then(|| CorpseSummary {
                guid: item.object_id(),
                name: item.name.clone(),
                distance: metres,
                opened: external.has_corpse_been_opened(item.object_id()),
                own: own_name.as_deref() == Some(item.name.as_str()),
            })
        })
        .collect();
    list.sort_by(|a, b| a.distance.total_cmp(&b.distance).then(a.guid.cmp(&b.guid)));
    list
}

/// What the client remembers of the player's deaths this session.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct DeathState {
    last_death_message: Option<String>,
    deaths: u32,
    last_vitae: Option<VitaeView>,
}

impl DeathState {
    /// The last death message the server sent the player.
    #[must_use]
    pub fn last_death_message(&self) -> Option<&str> {
        self.last_death_message.as_deref()
    }

    /// How many times the player died this session.
    #[must_use]
    pub fn deaths(&self) -> u32 {
        self.deaths
    }

    /// The player died; the server's message says how.
    pub fn on_local_death(&mut self, message: &str) {
        self.last_death_message = Some(message.to_owned());
        self.deaths = self.deaths.saturating_add(1);
    }

    /// After a message: the vitae view when it changed since the last one
    /// (the first one after entering the world counts as a change only when
    /// there is a penalty).
    pub fn observe(&mut self, world: &World) -> Option<VitaeView> {
        world.player()?;
        let now = vitae_view(world);
        let changed = match &self.last_vitae {
            Some(last) => last != &now,
            None => now.status.lost_percent > 0,
        };
        self.last_vitae = Some(now.clone());
        changed.then_some(now)
    }
}

#[cfg(test)]
mod tests {
    //! The vitae numbers follow OpenAC `VitaeUiController` (its golden case in
    //! `IndicatorDetailPanelControllerTests.Vitae_UsesDeathLevelPoolAndRetailThresholdFormula`);
    //! the corpse slot is `RuntimeClientCommandBindings.LastOutsideCorpsePosition`
    //! (saved position 0x0E).

    use std::collections::BTreeMap;

    use ac_core::spells::ActiveEnchantmentRecord;
    use ac_proto::{Inbound, PrivateUpdatePropertyInt};

    use super::*;
    use crate::world::tests::empty_description;

    const PLAYER: u32 = 0x5000_000A;

    fn described(positions: BTreeMap<u32, ac_proto::Position>) -> World {
        let mut world = World::new();
        world.set_player(PLAYER);
        world.apply_player_description(&ac_proto::PlayerDescription {
            positions,
            ..empty_description()
        });
        world
    }

    fn set_int(world: &mut World, property: PropertyInt, value: i32) {
        world.apply(&Inbound::PrivateUpdatePropertyInt(
            PrivateUpdatePropertyInt {
                sequence: 1,
                property: property.0,
                value,
            },
        ));
    }

    #[test]
    fn the_vitae_view_reads_the_enchantment_the_death_level_and_the_pool() {
        let mut world = described(BTreeMap::new());
        let mut deaths = DeathState::default();
        // Full strength says nothing at first.
        assert_eq!(deaths.observe(&world), None);
        assert_eq!(vitae_view(&world).status.lost_percent, 0);
        set_int(&mut world, PropertyInt::DEATH_LEVEL, 10);
        set_int(&mut world, PropertyInt::VITAE_CP_POOL, 100);
        world.update_spellbook(|book| {
            book.on_enchantment_added(ActiveEnchantmentRecord {
                spell_id: 666,
                layer_id: 1,
                duration: -1.0,
                caster_guid: PLAYER,
                stat_mod_value: Some(0.9),
                bucket: 4,
                ..ActiveEnchantmentRecord::default()
            });
        });
        let view = deaths.observe(&world).expect("the penalty is news");
        assert_eq!(view.status.lost_percent, 10);
        assert_eq!(view.status.experience_to_next_percent, 379);
        assert!(view.text.contains("earn 379 more experience"));
        // Unchanged, it is not repeated.
        assert_eq!(deaths.observe(&world), None);
        // The pool fills: the experience still to earn goes down.
        set_int(&mut world, PropertyInt::VITAE_CP_POOL, 200);
        assert_eq!(
            deaths
                .observe(&world)
                .map(|v| v.status.experience_to_next_percent),
            Some(279)
        );
    }

    #[test]
    fn a_death_is_remembered_with_its_message() {
        let mut deaths = DeathState::default();
        deaths.on_local_death("You were killed by an Olthoi Worker!");
        assert_eq!(
            deaths.last_death_message(),
            Some("You were killed by an Olthoi Worker!")
        );
        assert_eq!(deaths.deaths(), 1);
    }

    #[test]
    fn the_last_corpse_outside_is_the_saved_position_of_that_kind() {
        assert_eq!(last_outside_corpse_cell(&described(BTreeMap::new())), None);
        let mut positions = BTreeMap::new();
        positions.insert(
            LAST_OUTSIDE_DEATH_POSITION,
            ac_proto::Position {
                cell_id: 0xA9B4_0019,
                ..ac_proto::Position::default()
            },
        );
        assert_eq!(
            last_outside_corpse_cell(&described(positions)),
            Some(0xA9B4_0019)
        );
    }
}
