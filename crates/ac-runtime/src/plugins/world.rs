//! API group C: world reads (OpenAC `IWorldObjectAutomation`,
//! `IGameState`, `IWorldTimeAutomation`, `IDungeonMapAutomation`) over the
//! world, the inventory's appraisal slot, the contract tracker and the game
//! clock.

use std::collections::{HashMap, HashSet};

use ac_core::items::placement::PlaceCounter;
use ac_core::items::{ClientObject, ClientObjectTable, EquipMask};
use ac_core::properties::{PropertyInt, PropertyString};
use ac_core::world::{HOUR_TICKS, YEAR_TICKS, ZERO_YEAR};
use ac_plugin::abi::items::{ItemResult, ItemStatus};
use ac_plugin::abi::world::{
    self as abi, ActivationCompletion, Contract, DungeonCell, DungeonLayer, DungeonWall,
    EntitySnapshot, Floorplan, ItemProperties, ObjectClass, Position, WorldTime, capabilities_of,
};

use ac_core::items::PublicWeenieFlags;

use super::{PluginBinding, PluginEvent, Ran};
use crate::inventory::{ItemCommand, ObjectRef, REQUEST_NOT_SENT};
use crate::session::HostEvent;
use crate::session::SessionCommand;
use crate::world::World;
use ac_plugin::abi::LogLevel;

/// The public flag of a door.
const DOOR_FLAG: u32 = 0x0000_1000;
/// The physics state bit of something that does not collide (an open door).
const ETHEREAL_STATE: u32 = 0x0000_0004;
/// The `Open` boolean property.
const PROPERTY_BOOL_OPEN: u32 = 2;
/// A world object nothing says can be used, refused (OpenAC
/// `RuntimeAutomationSurface.MapWorldObjectUseOutcome`, `NotUseable`).
const NOT_USEABLE: &str = "That cannot be used.";

/// A server position as map coordinates.
pub(super) fn position_of(position: &ac_proto::Position) -> Position {
    let o = position.orientation;
    let heading = ac_motion::move_to_math::get_heading(glam::Quat::from_xyzw(o.x, o.y, o.z, o.w));
    Position::from_cell(
        position.cell_id,
        [position.origin.x, position.origin.y, position.origin.z],
        heading,
    )
}

/// Whether the player carries or wields `item`: see [`Ownership`]. For one
/// check; a walk over many objects builds the [`Ownership`] once.
pub(super) fn is_player_owned(item: &ClientObject, player: u32, items: &ClientObjectTable) -> bool {
    Ownership::of(items, player).owns(item, items)
}

/// An object with no placement of its own: in no container, worn by no one.
fn is_unplaced(item: &ClientObject) -> bool {
    item.container_id == 0
        && item.wielder_id == 0
        && item.currently_equipped_location == EquipMask::NONE
}

/// What the player owns, as plugins are told. An object is owned when its
/// own placement says so: wielded by the player, or in the player or in a
/// pack the player holds (up to four packs deep), as OpenAC's
/// `IsPlayerOwned`. An object the server has listed in the player's
/// contents, or in an owned pack's, before describing it has no placement
/// of its own yet (a listing names a container's members and moves none of
/// them); it is owned too, in the container whose list holds it. At login
/// every side pack's contents are listed before their descriptions arrive,
/// so without this the owned list would miss them while the inventory
/// already counts as complete.
pub(super) struct Ownership {
    player: u32,
    /// Each listed object with no placement of its own: the container whose
    /// list holds it and its place there among its kind.
    listed: HashMap<u32, (u32, i32)>,
}

impl Ownership {
    /// Walks the player's contents list and the list of every pack on it,
    /// nested. A member whose own placement names another container is
    /// that container's, so its lists are not followed from here.
    pub(super) fn of(items: &ClientObjectTable, player: u32) -> Self {
        let mut listed = HashMap::new();
        let mut pending = Vec::new();
        if player != 0 {
            pending.push(player);
        }
        let mut visited = HashSet::new();
        while let Some(container) = pending.pop() {
            if !visited.insert(container) {
                continue;
            }
            let mut places = PlaceCounter::default();
            for &id in items.get_contents(container) {
                let place = places.next(items, id);
                let Some(member) = items.get(id) else {
                    continue;
                };
                if is_unplaced(member) {
                    listed.insert(id, (container, place.unwrap_or(-1)));
                } else if member.container_id != container {
                    continue;
                }
                if items.contents_list(id).is_some() {
                    pending.push(id);
                }
            }
        }
        Self { player, listed }
    }

    /// Whether the player owns `item`.
    pub(super) fn owns(&self, item: &ClientObject, items: &ClientObjectTable) -> bool {
        if self.player == 0 {
            return false;
        }
        // The item, then up to four packs around it.
        let mut current = item;
        for _ in 0..5 {
            if current.wielder_id == self.player || current.container_id == self.player {
                return true;
            }
            if current.container_id == 0 {
                return is_unplaced(current) && self.listed.contains_key(&current.object_id());
            }
            let Some(parent) = items.get(current.container_id) else {
                return false;
            };
            current = parent;
        }
        false
    }

    /// The owned container whose list holds `object_id`, and its place
    /// there, when the object has no placement of its own.
    pub(super) fn listing(&self, object_id: u32) -> Option<(u32, i32)> {
        self.listed.get(&object_id).copied()
    }
}

/// The object class of an item: OpenAC's classifier, and a writable item
/// that casts a spell is a scroll.
pub(super) fn classify(item: Option<&ClientObject>) -> ObjectClass {
    let Some(item) = item else {
        return ObjectClass::Unknown;
    };
    let item_type = item.item_type.bits();
    let flags = item.public_weenie_bitfield.map_or(0, |f| f.bits());
    let class = abi::classify(item_type, flags);
    if item_type & 0x0000_2000 != 0 && item.spell_id.is_some_and(|s| s > 0) {
        ObjectClass::Scroll
    } else {
        class
    }
}

/// An object placed in the world as a plugin sees it (OpenAC
/// `RuntimeWorldEntityProjection.Convert`): the client's local id, the
/// server id, where it stands. None for an object with no position.
#[must_use]
pub fn entity_snapshot(world: &World, guid: u32) -> Option<EntitySnapshot> {
    let object = world.object(guid)?;
    let p = object.position()?;
    Some(EntitySnapshot {
        id: object.key().local_id,
        source_id: object.guid(),
        position: (p.origin.x, p.origin.y, p.origin.z),
        rotation: (
            p.orientation.x,
            p.orientation.y,
            p.orientation.z,
            p.orientation.w,
        ),
    })
}

fn has_property_data(item: &ClientObject) -> bool {
    let p = &item.properties;
    !(p.ints.is_empty()
        && p.int64s.is_empty()
        && p.bools.is_empty()
        && p.floats.is_empty()
        && p.strings.is_empty()
        && p.data_ids.is_empty()
        && p.instance_ids.is_empty())
}

/// The spells in effect on the player (its active spell ids).
fn player_active_spells(world: &World) -> Vec<u32> {
    world.spellbook().map_or_else(Vec::new, |book| {
        book.enchantments_in_effect_snapshot()
            .iter()
            .map(|e| e.spell_id)
            .collect()
    })
}

/// One object as a plugin sees it (OpenAC
/// `RuntimeWorldObjectProjection.Project`): the body's place when the
/// session moves it, else the server's last word; owned, landscape and
/// door state as OpenAC reads them.
pub(super) fn project_object(world: &World, object_id: u32) -> Option<abi::WorldObject> {
    let ownership = Ownership::of(world.items(), world.player_guid().unwrap_or(0));
    project_object_owned(world, object_id, &ownership)
}

/// [`project_object`] with the player's [`Ownership`] already walked. An
/// owned object listed before it is described is in the container whose
/// list holds it.
pub(super) fn project_object_owned(
    world: &World,
    object_id: u32,
    ownership: &Ownership,
) -> Option<abi::WorldObject> {
    let record = world.object(object_id);
    let item = world.item(object_id);
    if record.is_none() && item.is_none() {
        return None;
    }
    let player = world.player_guid().unwrap_or(0);
    let source = record.and_then(|r| r.position());
    let owned = item.is_some_and(|i| ownership.owns(i, world.items()));
    let flags = item
        .and_then(|i| i.public_weenie_bitfield)
        .map_or(0, |f| f.bits());
    let class = classify(item);
    let container_id = ownership
        .listing(object_id)
        .map_or_else(|| item.map_or(0, |i| i.container_id), |(c, _)| c);
    let wielder_id = item.map_or(0, |i| i.wielder_id);
    let is_door_open = flags & DOOR_FLAG != 0
        && match record {
            Some(record) => record.physics().state & ETHEREAL_STATE != 0,
            None => item.is_some_and(|i| {
                i.properties
                    .bools
                    .get(&PROPERTY_BOOL_OPEN)
                    .copied()
                    .unwrap_or(false)
            }),
        };
    Some(abi::WorldObject {
        object_id,
        weenie_class_id: item.map_or(0, |i| i.weenie_class_id),
        name: item
            .map(|i| i.name.clone())
            .filter(|n| !n.is_empty())
            .or_else(|| world.name(object_id).map(str::to_owned))
            .unwrap_or_else(|| format!("0x{object_id:08X}")),
        object_class: class,
        item_type: item.map_or(0, |i| i.item_type.bits()),
        container_id,
        wielder_id,
        capabilities: capabilities_of(class),
        is_owned: owned,
        is_landscape: source.is_some() && !owned && container_id == 0 && wielder_id == 0,
        position: source.map(position_of),
        has_appraisal_data: item.is_some_and(has_property_data),
        last_id_time: item.map_or(0, |i| i.last_appraisal_time_ms),
        is_door_open,
        stack_size: item.map_or(1, |i| i.stack_size.max(1)),
        items_capacity: item.map_or(0, |i| i.items_capacity),
        containers_capacity: item.map_or(0, |i| i.containers_capacity),
        spell_ids: item.map_or_else(Vec::new, |i| i.appraised_spell_ids.clone()),
        active_spell_ids: if object_id == player {
            player_active_spells(world)
        } else {
            Vec::new()
        },
        icon_id: item.map_or(0, |i| i.icon_id),
        last_appraisal_unsuccessful: item.is_some_and(|i| i.last_appraisal_unsuccessful),
        coverage_mask: item.map_or(0, |i| i.priority),
        header: item
            .and_then(|i| i.header.as_ref())
            .map(super::items::project_header),
        portal_destination: portal(class, item)
            .map(|i| {
                i.properties
                    .get_string(PropertyString::APPRAISAL_PORTAL_DESTINATION.0, "")
            })
            .filter(|label| !label.trim().is_empty())
            .map(str::to_owned),
        portal_minimum_level: portal(class, item)
            .map(|i| i.properties.get_int(PropertyInt::MIN_LEVEL.0, 0))
            .filter(|&level| level > 0),
        portal_maximum_level: portal(class, item)
            .map(|i| i.properties.get_int(PropertyInt::MAX_LEVEL.0, 0))
            .filter(|&level| level > 0),
        hook_item_types: item.map_or(0, |i| i.hook_item_types),
        server_position: record
            .and_then(crate::world::WorldObject::accepted_position)
            .map(position_of),
    })
}

/// The item behind a portal, for the portal's appraised details; none for
/// anything else.
fn portal(class: ObjectClass, item: Option<&ClientObject>) -> Option<&ClientObject> {
    item.filter(|_| class == ObjectClass::Portal)
}

/// Every property of an item, with its kept profiles (OpenAC
/// `CaptureProperties`).
pub(super) fn properties_of(item: &ClientObject) -> ItemProperties {
    let p = &item.properties;
    ItemProperties {
        ints: p.ints.iter().map(|(k, v)| (*k, *v)).collect(),
        int64s: p.int64s.iter().map(|(k, v)| (*k, *v)).collect(),
        bools: p.bools.iter().map(|(k, v)| (*k, *v)).collect(),
        floats: p.floats.iter().map(|(k, v)| (*k, *v)).collect(),
        strings: p.strings.iter().map(|(k, v)| (*k, v.clone())).collect(),
        data_ids: p.data_ids.iter().map(|(k, v)| (*k, *v)).collect(),
        instance_ids: p.instance_ids.iter().map(|(k, v)| (*k, *v)).collect(),
        weapon: item.weapon_profile.map(|w| abi::WeaponProfile {
            damage_type: w.damage_type.cast_signed(),
            weapon_time: w.weapon_time.cast_signed(),
            weapon_skill: w.weapon_skill,
            damage: w.normalized_damage(),
            damage_variance: w.damage_variance,
            damage_mod: w.damage_mod,
            weapon_length: w.weapon_length,
            max_velocity: w.max_velocity,
            weapon_offense: w.weapon_offense,
            max_velocity_estimated: w.max_velocity_estimated.cast_signed(),
        }),
        armor: item.armor_profile.map(|a| abi::ArmorProfile {
            armor_level: p
                .ints
                .get(&ac_core::properties::PropertyInt::ARMOR_LEVEL.0)
                .copied()
                .unwrap_or(0),
            slash_mod: a.slashing_protection,
            pierce_mod: a.piercing_protection,
            bludgeon_mod: a.bludgeoning_protection,
            cold_mod: a.cold_protection,
            fire_mod: a.fire_protection,
            acid_mod: a.acid_protection,
            nether_mod: a.nether_protection,
            electric_mod: a.lightning_protection,
        }),
        creature: item.creature_profile.map(|c| abi::CreatureProfile {
            flags: c.flags,
            health: c.health,
            max_health: c.max_health,
            attribute_highlights: c.attribute_highlights,
            attribute_colors: c.attribute_colors,
            attributes: c.attributes.map(|a| abi::CreatureAttributes {
                strength: a.strength,
                endurance: a.endurance,
                quickness: a.quickness,
                coordination: a.coordination,
                focus: a.focus,
                self_: a.self_,
                stamina: a.stamina,
                max_stamina: a.max_stamina,
                mana: a.mana,
                max_mana: a.max_mana,
            }),
        }),
    }
}

impl PluginBinding<'_> {
    /// Every object the client knows: those placed in the world, then the
    /// rest of the item table, by id (OpenAC `CaptureObjects`).
    pub(super) fn world_objects(&self) -> Vec<abi::WorldObject> {
        if !self.facts.in_world {
            return Vec::new();
        }
        let world = &self.state.world;
        let mut ids: Vec<u32> = world.objects().map(|o| o.guid()).collect();
        ids.extend(world.items().objects().map(ClientObject::object_id));
        ids.sort_unstable();
        ids.dedup();
        let ownership = Ownership::of(world.items(), world.player_guid().unwrap_or(0));
        ids.into_iter()
            .filter_map(|id| project_object_owned(world, id, &ownership))
            .collect()
    }

    /// One object.
    pub(super) fn world_object(&self, object_id: u32) -> Option<abi::WorldObject> {
        if !self.facts.in_world || object_id == 0 {
            return None;
        }
        project_object(&self.state.world, object_id)
    }

    /// Every property the client holds for an object.
    pub(super) fn world_properties(&self, object_id: u32) -> Option<ItemProperties> {
        if !self.facts.in_world {
            return None;
        }
        self.state.world.item(object_id).map(properties_of)
    }

    /// One integer property.
    pub(super) fn world_int_property(&self, (object_id, property): (u32, u32)) -> Option<i32> {
        if !self.facts.in_world {
            return None;
        }
        self.state
            .world
            .item(object_id)
            .and_then(|item| item.properties.ints.get(&property).copied())
    }

    /// Ask for an appraisal without opening the assess window: any object
    /// in the item table; only another description in flight holds it up
    /// (OpenAC `Objects.Identify`).
    pub(super) fn world_identify(&mut self, object_id: u32) -> ItemResult {
        if !self.in_world() {
            return ItemResult::of(ItemStatus::Unavailable);
        }
        if object_id == 0 || self.state.world.item(object_id).is_none() {
            return ItemResult::of(ItemStatus::InvalidItem);
        }
        let now = self.session_ms();
        if !self.state.inventory.interaction().can_begin_appraisal(now) {
            return ItemResult::of(ItemStatus::Busy);
        }
        let Some(link) = self.link.as_mut() else {
            return ItemResult::of(ItemStatus::Unavailable);
        };
        if self
            .state
            .inventory
            .appraise_quietly(object_id, now, link.active, link.outbox)
        {
            ItemResult::of(ItemStatus::Started)
        } else {
            ItemResult::of(ItemStatus::Refused)
        }
    }

    /// Use a world object, walking to it first when it is out of reach
    /// (OpenAC `Objects.Activate`); an owned item is refused (that is
    /// `items_use`). A started activation ends with an
    /// [`ac_plugin::abi::Event::ActivationCompleted`].
    pub(super) fn world_activate(&mut self, object_id: u32) -> ItemResult {
        // Every outcome is logged: a use refused as busy, or a walk begun
        // that never arrives, leaves no other trace (OpenAC
        // `RuntimeWorldObjectUse.TryUse`).
        let result = self.world_activate_core(object_id);
        let _ = self.events.send(HostEvent::Plugin(PluginEvent::Log {
            plugin: "plugins".to_owned(),
            level: LogLevel::Info,
            message: format!(
                "[interaction] automation use guid=0x{object_id:08X} outcome={:?}",
                result.status
            ),
        }));
        result
    }

    fn world_activate_core(&mut self, object_id: u32) -> ItemResult {
        if !self.in_world() {
            return ItemResult::of(ItemStatus::Unavailable);
        }
        let player = self.state.world.player_guid().unwrap_or(0);
        let Some(item) = self.state.world.item(object_id).filter(|_| object_id != 0) else {
            return ItemResult::of(ItemStatus::InvalidTarget);
        };
        if is_player_owned(item, player, self.state.world.items()) {
            return ItemResult::because(
                ItemStatus::InvalidTarget,
                "Activate is for world objects; use Items.Use for owned items.",
            );
        }
        // Another character is not used from here (that would open a secure
        // trade); the trade has its own calls.
        let other_player = item
            .public_weenie_bitfield
            .is_some_and(|f| f.contains(PublicWeenieFlags::PLAYER));
        if other_player && object_id != player {
            return ItemResult::because(ItemStatus::Refused, NOT_USEABLE);
        }
        // Nothing known about it says it can be used.
        let useable = item.useability.is_some_and(|u| u.is_useable());
        // Busy also while a walk or turn is armed (a person's own click or
        // an earlier call): the armed use holds the busy count and the armed
        // pickup the request, so this route never cuts in front of either.
        if self.items_busy() {
            return ItemResult::of(ItemStatus::Busy);
        }
        let result = self.use_world_object(object_id, useable);
        if result.status == ItemStatus::Started {
            self.state.plugin_services.activations.started(object_id);
        }
        result
    }

    /// A plugin's use of a world object the character does not own, walking
    /// there first when it is out of reach (OpenAC `RuntimeWorldObjectUse`,
    /// the route `Objects.Activate`, `Items.Use` and `Loot.Open` share): the
    /// container it opens is asked for once the use goes out.
    pub(super) fn use_world_object(&mut self, object_id: u32, useable: bool) -> ItemResult {
        let command = SessionCommand::Item(ItemCommand::UseWorldObject(ObjectRef::Guid(object_id)));
        match self.run(command) {
            Ran::NoConnection => ItemResult::of(ItemStatus::Unavailable),
            Ran::Refused(why) if why == REQUEST_NOT_SENT && !useable => {
                ItemResult::because(ItemStatus::Refused, NOT_USEABLE)
            }
            Ran::Refused(why) => ItemResult::because(ItemStatus::Refused, &why),
            Ran::Done => ItemResult::of(ItemStatus::Started),
        }
    }

    /// The last activation that ended.
    pub(super) fn world_last_activation(&self) -> ActivationCompletion {
        self.state.plugin_services.activations.last.clone()
    }

    /// The open outside container.
    pub(super) fn world_open_container(&self) -> u32 {
        self.state
            .inventory
            .external_containers()
            .current_container_id()
    }

    /// Every object placed in the world with its place (OpenAC
    /// `State.Entities`): landblock-local metres and orientation.
    pub(super) fn world_entities(&self) -> Vec<EntitySnapshot> {
        if !self.facts.in_world {
            return Vec::new();
        }
        let mut entities: Vec<EntitySnapshot> = self
            .state
            .world
            .objects()
            .filter_map(|o| entity_snapshot(&self.state.world, o.guid()))
            .collect();
        entities.sort_by_key(|e| e.id);
        entities
    }

    /// The fixed decoration around the character (OpenAC
    /// `IGameState.SceneryObjects`): every static object the landblocks the
    /// session holds collision for placed, with its model and where it
    /// stands; a separate list from the objects, whose ids never meet it.
    /// Empty out of the world.
    pub(super) fn world_scenery(&self) -> Vec<EntitySnapshot> {
        if !self.facts.in_world {
            return Vec::new();
        }
        self.state
            .movement
            .residency()
            .placed()
            .map(|s| EntitySnapshot {
                id: s.id,
                source_id: s.source_id,
                position: (s.position.x, s.position.y, s.position.z),
                rotation: (s.rotation.x, s.rotation.y, s.rotation.z, s.rotation.w),
            })
            .collect()
    }

    /// The tracked contracts, with names and status from the catalog.
    pub(super) fn world_contracts(&self) -> Vec<Contract> {
        self.state
            .communication
            .contract_summaries(self.wall_unix_ms)
            .into_iter()
            .map(|c| Contract {
                contract_id: c.contract_id,
                stage: c.stage,
                progress: c.progress_count,
                is_displayed: c.displayed,
                name: c.name,
                description: c.description,
                status: c.progress,
            })
            .collect()
    }

    /// The game's calendar now (OpenAC `WorldTime.Snapshot`): day is hours
    /// 4 to 11; the minutes until day or night count whole hours from the
    /// current one.
    pub(super) fn world_time(&self) -> WorldTime {
        let Some(link) = self.link.as_ref().filter(|_| self.facts.in_world) else {
            return WorldTime::default();
        };
        let clock = self.state.world.clock();
        let Some(raw) = clock.now_ticks(link.clocks.session) else {
            return WorldTime::default();
        };
        let calendar = clock.calendar();
        let Ok(date) = calendar.to_calendar(raw) else {
            return WorldTime::default();
        };
        let hour = date.hour as i32;
        let is_day = (4..12).contains(&hour);
        let shifted = raw.max(0.0) + calendar.origin_offset_ticks();
        let game_ticks = shifted + f64::from(ZERO_YEAR) * YEAR_TICKS;
        let within_hour = shifted - (shifted / HOUR_TICKS).floor() * HOUR_TICKS;
        let until_night = if is_day {
            (f64::from(12 - hour) * HOUR_TICKS - within_hour) / 60.0
        } else {
            0.0
        };
        let day_hour = if hour <= 4 { hour + 16 } else { hour };
        let until_day = if is_day {
            0.0
        } else {
            (f64::from(20 - day_hour) * HOUR_TICKS - within_hour) / 60.0
        };
        let name = |text: String| text.replace("AndHalf", "-and-Half");
        WorldTime {
            is_available: true,
            game_ticks,
            year: date.year,
            month: date.month as i32,
            day: date.day,
            hour,
            month_name: name(format!("{:?}", date.month)),
            hour_name: name(format!("{:?}", date.hour)),
            is_day,
            minutes_until_day: until_day.max(0.0),
            minutes_until_night: until_night.max(0.0),
        }
    }

    /// The landblock the character stands in.
    pub(super) fn dungeon_landblock(&self) -> u32 {
        let world = &self.state.world;
        world
            .player_guid()
            .and_then(|guid| world.object(guid))
            .and_then(|o| o.position())
            .map_or(0, |p| p.cell_id & 0xFFFF_0000)
    }

    /// Whether a cell belongs to a sealed dungeon (read from the dats).
    pub(super) fn dungeon_is_sealed(&self, cell_id: u32) -> bool {
        self.state
            .movement
            .content()
            .is_some_and(|content| ac_nav::dungeon::is_sealed_dungeon(content.dats(), cell_id))
    }

    /// Every indoor cell the landblock lists, as stored; read once per
    /// landblock and kept (the 64 landblocks read last).
    pub(super) fn dungeon_indoor_cells(&mut self, landblock_id: u32) -> Vec<abi::IndoorCell> {
        let Some(content) = self.state.movement.content().cloned() else {
            return Vec::new();
        };
        let Ok(cells) = self
            .state
            .plugin_services
            .indoor_cells
            .capture(content.dats(), landblock_id)
        else {
            return Vec::new();
        };
        cells
            .iter()
            .map(|c| abi::IndoorCell {
                cell_id: c.cell_id,
                environment_id: c.environment_id,
                cell_structure: c.cell_structure,
                origin: (c.origin.x, c.origin.y, c.origin.z),
                orientation: (
                    c.orientation.w,
                    c.orientation.x,
                    c.orientation.y,
                    c.orientation.z,
                ),
                sees_outside: c.sees_outside,
                doorways: c
                    .doorways
                    .iter()
                    .map(|d| abi::CellDoorway {
                        other_cell_id: d.other_cell_id,
                        leads_outside: d.leads_outside,
                        exact_match: d.exact_match,
                        portal_side: d.portal_side,
                        center: (d.center.x, d.center.y, d.center.z),
                    })
                    .collect(),
                visible_cells: c.visible_cells.clone(),
            })
            .collect()
    }

    /// The outdoor terrain of each landblock asked for, in the order asked,
    /// at most [`abi::LANDSCAPE_TERRAIN_MAX`]; a landblock the files have
    /// no terrain for is left out.
    ///
    /// Each landblock is read once and kept (the 1024 read last). The
    /// terrain files are read past the shared dat cache: a map asks for
    /// the whole world a page at a time, and that would push out what the
    /// session's own loading keeps there.
    pub(super) fn landscape_terrain(&mut self, landblocks: &[u16]) -> Vec<abi::LandblockTerrain> {
        let Some(content) = self.state.movement.content().cloned() else {
            return Vec::new();
        };
        let cache = &mut self.state.plugin_services.landscape;
        landblocks
            .iter()
            .take(abi::LANDSCAPE_TERRAIN_MAX)
            .filter_map(|&landblock| {
                // A read failure leaves that landblock out, as one the
                // files lack is.
                cache
                    .terrain(content.dats().uncached(), content.height_table(), landblock)
                    .ok()
                    .flatten()
            })
            .map(|terrain| abi::LandblockTerrain {
                landblock: terrain.landblock,
                vertices: terrain
                    .vertices
                    .iter()
                    .map(|v| abi::TerrainVertex {
                        terrain_type: v.terrain_type,
                        road: v.road,
                        height_meters: v.height_meters,
                    })
                    .collect(),
            })
            .collect()
    }

    /// Each terrain type the region defines with its colour and name,
    /// worked out once (decoding every terrain texture, about 20 ms) and
    /// kept; empty, and tried again on the next call, when the files
    /// cannot be read.
    pub(super) fn landscape_terrain_palette(&mut self) -> Vec<abi::TerrainColor> {
        if let Some(kept) = &self.state.plugin_services.terrain_palette {
            return kept.clone();
        }
        let Some(content) = self.state.movement.content().cloned() else {
            return Vec::new();
        };
        let Some(region) = content.region() else {
            return Vec::new();
        };
        // Past the shared cache: each texture is decoded once, here.
        let Ok(colours) = ac_nav::landscape::read_terrain_palette(
            content.dats().uncached(),
            &region.terrain_info,
        ) else {
            return Vec::new();
        };
        let colours: Vec<abi::TerrainColor> = colours
            .into_iter()
            .map(|c| abi::TerrainColor {
                terrain_type: c.terrain_type,
                rgb: c.rgb,
                map_rgb: c.map_rgb,
                name: c.name,
            })
            .collect();
        self.state.plugin_services.terrain_palette = Some(colours.clone());
        colours
    }

    /// A landblock's floorplan, built once from the dats and kept.
    pub(super) fn dungeon_floorplan(&mut self, landblock_id: u32) -> Option<Floorplan> {
        let content = self.state.movement.content()?.clone();
        let plan = self
            .state
            .plugin_services
            .floorplans
            .try_build(content.dats(), landblock_id)
            .ok()
            .flatten()?;
        let v3 = |v: glam::Vec3| (v.x, v.y, v.z);
        Some(Floorplan {
            landblock_id: plan.landblock_id,
            layers: plan
                .layers
                .iter()
                .map(|layer| DungeonLayer {
                    z: layer.z,
                    floors: layer
                        .floors
                        .iter()
                        .map(|poly| poly.iter().map(|p| (p.x, p.y)).collect())
                        .collect(),
                    walls: layer
                        .walls
                        .iter()
                        .map(|w| DungeonWall {
                            start: (w.start.x, w.start.y),
                            end: (w.end.x, w.end.y),
                        })
                        .collect(),
                })
                .collect(),
            cells: plan
                .cells
                .iter()
                .map(|c| DungeonCell {
                    cell_id: c.cell_id,
                    center: v3(c.center),
                    layer_z: c.layer_z,
                    bounds_min: v3(c.bounds.min),
                    bounds_max: v3(c.bounds.max),
                    floor_z: c.floor_z,
                })
                .collect(),
            bounds_min: v3(plan.bounds.min),
            bounds_max: v3(plan.bounds.max),
        })
    }
}
