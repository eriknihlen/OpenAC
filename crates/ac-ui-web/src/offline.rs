//! The panels without a session: one window (the inventory or the
//! character window) shown read-only from a character's stored data, as a
//! dashboard holds it, instead of from a session's stream.
//!
//! The data is what the companion plugin reports: each item as its
//! inventory record (the raw property tables keyed by game property id,
//! mixed with the older client framework's own keys for what it read from
//! the object's header and appraisal profiles), and the character's stats.
//! [`OfflineData::events`] turns them into the stream messages a session
//! would have sent for the same character (the world with the player in
//! it, the item table, the player's numbers, the interaction facts), so
//! the panels show them through exactly the code the live window runs.
//!
//! The framework keys and what they mean are the plugin's; this module is
//! the one place in the client that knows them.

use std::collections::{BTreeMap, HashMap, HashSet};

use ac_core::items::{ClientObject, EquipMask, ItemType, ItemUseable, PublicWeenieFlags};
use ac_core::player::LocalPlayerState;
use ac_runtime::session::PresentationEvent;
use ac_runtime::session::interface::{InterfaceEvent, InterfaceState, ItemsUpdate, PlayerFacts};
use ac_runtime::world::{PlayerView, WorldSnapshot};
use ac_ui::panels::character::{
    ReportedSheet, ReportedSkill, ReportedVital, SkillAdvancement, reported_skill_key,
};
use serde_json::Value;

/// Which window the module shows without a session.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OfflineKind {
    /// The inventory: the paperdoll (in its slot view), the packs, the open
    /// pack's contents and the burden.
    Inventory,
    /// The character window.
    Character,
}

impl OfflineKind {
    /// From the page's number: 1 the inventory, 2 the character window.
    #[must_use]
    pub fn from_number(kind: u32) -> Option<Self> {
        match kind {
            1 => Some(Self::Inventory),
            2 => Some(Self::Character),
            _ => None,
        }
    }
}

/// The framework's keys in an item record's tables (the plugin's
/// `builder::keys`). Keys from [`FRAMEWORK_KEYS_FROM`] up are the
/// framework's own; below it they are game property ids.
mod keys {
    pub const WEENIE_CLASS_ID: u32 = 218_103_808;
    pub const ICON: u32 = 218_103_809;
    pub const CONTAINER: u32 = 218_103_810;
    pub const ITEM_SLOTS: u32 = 218_103_812;
    pub const PACK_SLOTS: u32 = 218_103_813;
    pub const STACK_COUNT: u32 = 218_103_814;
    pub const STACK_MAX: u32 = 218_103_815;
    pub const SPELL_DID: u32 = 218_103_816;
    pub const COVERAGE: u32 = 218_103_821;
    pub const EQUIPABLE_SLOTS: u32 = 218_103_822;
    pub const EQUIP_TYPE: u32 = 218_103_823;
    pub const ICON_OUTLINE: u32 = 218_103_824;
    pub const MISSILE_TYPE: u32 = 218_103_825;
    pub const USAGE_MASK: u32 = 218_103_826;
    pub const HOOK_TYPE: u32 = 218_103_828;
    pub const CATEGORY: u32 = 218_103_834;
    pub const BEHAVIOR: u32 = 218_103_835;
    pub const USABLE: u32 = 218_103_843;
    pub const ICON_OVERLAY: u32 = 218_103_849;
    pub const ICON_UNDERLAY: u32 = 218_103_850;
    pub const SALVAGE_WORKMANSHIP: u32 = 167_772_169;
    pub const SECONDARY_NAME: u32 = 184_549_376;

    /// The lowest framework key (the floating-point table's first).
    pub const FRAMEWORK_KEYS_FROM: u32 = 167_772_160;

    // Game property ids the record carries in its integer table.
    pub const BURDEN: u32 = 5;
    pub const EQUIPPED_SLOTS: u32 = 10;
    pub const VALUE: u32 = 19;
    pub const MAX_STRUCTURE: u32 = 91;
    pub const STRUCTURE: u32 = 92;
    pub const MATERIAL: u32 = 131;
    // In the text table.
    pub const NAME: u32 = 1;
}

/// Icons are reported without the icon table's prefix.
const ICON_BASE: u32 = 0x0600_0000;
/// An item's own running enchantments are marked so in its spell list.
const ACTIVE_SPELL_BIT: u32 = 0x8000_0000;
/// The player's carried burden and its augmentation (int properties).
const PROP_ENCUMBRANCE_VAL: u32 = 5;
const PROP_CARRYING_CAPACITY_AUGS: u32 = 0xE6;
/// Burden capacity per strength point, and per augmentation rank per
/// strength point (at most five ranks count).
const BURDEN_PER_STRENGTH: i64 = 150;
const AUG_BURDEN_PER_RANK: i64 = 30;
const AUG_RANKS_COUNTED: i64 = 5;
/// The player's level and unspent skill credits (int properties).
const PROP_LEVEL: u32 = 25;
const PROP_AVAILABLE_SKILL_CREDITS: u32 = 24;
/// His total and unassigned experience, and his luminance now and at
/// most (64-bit int properties).
const INT64_TOTAL_XP: u32 = 1;
const INT64_AVAILABLE_XP: u32 = 2;
const INT64_AVAILABLE_LUMINANCE: u32 = 6;
const INT64_MAXIMUM_LUMINANCE: u32 = 7;
/// The stats' attribute names in the game's attribute order (ids 1..=6).
const ATTRIBUTE_NAMES: [&str; 6] = [
    "strength",
    "endurance",
    "quickness",
    "coordination",
    "focus",
    "self",
];

/// The stats' vital names: health, stamina, mana.
const VITAL_NAMES: [&str; 3] = ["health", "stamina", "mana"];
/// The character sheet's row of each attribute in the game's order
/// (its rows: strength, endurance, coordination, quickness, focus, self).
const SHEET_ROW_OF_ATTRIBUTE: [usize; 6] = [0, 1, 3, 2, 4, 5];

/// Each attribute as (ranks, start), in the game's order (strength,
/// endurance, quickness, coordination, focus, self).
pub type Attributes = [Option<(u32, u32)>; 6];

/// One character's data as the page hands it in.
#[derive(Debug, Clone, Default)]
pub struct OfflineData {
    /// The player's object id.
    pub player: u32,
    /// The player's name.
    pub name: String,
    /// Every item, with its place.
    pub items: Vec<ClientObject>,
    /// Contents lists, in order: the player's (his items and packs) and
    /// each pack's.
    pub contents: Vec<(u32, Vec<u32>)>,
    /// The player's numbers from the character's stats.
    pub stats: Stats,
}

/// What the character's stats give.
#[derive(Debug, Clone, Default)]
pub struct Stats {
    /// The player's int properties (level, burden, heritage, ...).
    pub ints: BTreeMap<u32, i32>,
    /// His 64-bit int properties (experience, luminance).
    pub int64s: BTreeMap<u32, i64>,
    /// Each attribute without spells as (ranks, start), in the game's
    /// order.
    pub attributes: Attributes,
    /// Strength now, as the burden capacity says (the burden meter reads
    /// it); none when the capacity says no more than strength without
    /// spells.
    pub strength_now: Option<u32>,
    /// What the character sheet shows of the stats over the numbers.
    pub reported: ReportedSheet,
}

/// Reading the page's JSON failed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DataError(pub String);

impl std::fmt::Display for DataError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl OfflineData {
    /// Read the page's JSON:
    /// `{ "character": <stats>|null, "player_id": <id>|null, "items": [<record>] }`.
    pub fn parse(bytes: &[u8]) -> Result<Self, DataError> {
        let root: Value = serde_json::from_slice(bytes)
            .map_err(|e| DataError(format!("offline data is not JSON: {e}")))?;
        let character = root.get("character").filter(|c| c.is_object());
        let records = root
            .get("items")
            .and_then(Value::as_array)
            .map_or(&[][..], Vec::as_slice);
        let mut slotted: Vec<(ClientObject, Option<i64>, usize)> = Vec::new();
        for (index, record) in records.iter().enumerate() {
            if let Some(item) = item_of(record) {
                let slot = record.get("slot").and_then(Value::as_i64);
                slotted.push((item, slot, index));
            }
        }
        let player = root
            .get("player_id")
            .and_then(Value::as_i64)
            .map(|id| id as u32)
            .filter(|&id| id != 0)
            .unwrap_or_else(|| guess_player(slotted.iter().map(|(i, _, _)| i)));
        let name = character
            .and_then(|c| c.get("character_name"))
            .and_then(Value::as_str)
            .unwrap_or_default()
            .to_owned();
        let stats = character.map(player_numbers).unwrap_or_default();

        // Contents lists: each container's items by their slot, then in the
        // order they came.
        let mut by_container: HashMap<u32, Vec<(Option<i64>, usize, u32)>> = HashMap::new();
        for (item, slot, index) in &slotted {
            if item.container_id != 0 {
                by_container.entry(item.container_id).or_default().push((
                    *slot,
                    *index,
                    item.object_id(),
                ));
            }
        }
        let mut contents: Vec<(u32, Vec<u32>)> = by_container
            .into_iter()
            .map(|(container, mut members)| {
                members.sort_by_key(|&(slot, index, _)| (slot.unwrap_or(i64::MAX), index));
                (
                    container,
                    members.into_iter().map(|(_, _, id)| id).collect(),
                )
            })
            .collect();
        contents.sort_by_key(|(container, _)| *container);
        let mut items: Vec<ClientObject> = slotted.into_iter().map(|(i, _, _)| i).collect();
        for item in &mut items {
            if let Some(list) = contents.iter().find(|(c, _)| *c == item.container_id) {
                item.container_slot = list
                    .1
                    .iter()
                    .position(|&id| id == item.object_id())
                    .map_or(-1, |p| p as i32);
            }
        }
        Ok(Self {
            player,
            name,
            items,
            contents,
            stats,
        })
    }

    /// The stream messages a session would have sent for this character,
    /// with `selected` selected. The first time (`before` is `None`) they
    /// enter the world and replace the item table; after that they change
    /// the table in place (what the panels show open stays open), and
    /// `before`, the objects the last data held, says what left.
    #[must_use]
    pub fn events(
        &self,
        kind: OfflineKind,
        selected: Option<u32>,
        revision: u64,
        before: Option<&[u32]>,
    ) -> Vec<PresentationEvent> {
        let mut player_object = ClientObject::new(self.player);
        player_object.name.clone_from(&self.name);
        player_object.item_type = ItemType::CREATURE;
        player_object.properties.ints = self.stats.ints.clone();
        player_object.properties.int64s = self.stats.int64s.clone();
        let mut objects = vec![player_object];
        objects.extend(self.items.iter().cloned());
        let contents = self
            .contents
            .iter()
            .map(|(c, list)| (*c, Some(list.clone())))
            .collect();
        let mut numbers = LocalPlayerState::new(None);
        for (index, attribute) in self.stats.attributes.iter().enumerate() {
            if let Some((mut ranks, start)) = *attribute {
                // The inventory's burden meter reads strength now.
                if index == 0
                    && kind == OfflineKind::Inventory
                    && let Some(now) = self.stats.strength_now
                {
                    ranks = now.saturating_sub(start);
                }
                let _ = numbers.on_attribute_update(index as u32 + 1, ranks, start, 0);
            }
        }
        numbers.on_int_properties(self.stats.ints.clone());
        let snapshot = WorldSnapshot {
            player_guid: Some(self.player),
            objects: vec![player_view_object(self.player, &self.name)],
        };
        let removed: Vec<u32> = before.map_or_else(Vec::new, |ids| {
            let now: HashSet<u32> = self.object_ids().into_iter().collect();
            ids.iter().copied().filter(|id| !now.contains(id)).collect()
        });
        let mut events = Vec::new();
        if before.is_none() {
            events.push(PresentationEvent::Snapshot(snapshot));
        }
        events.extend([
            PresentationEvent::Player(PlayerView {
                selected,
                ..PlayerView::default()
            }),
            PresentationEvent::Interface(InterfaceEvent::Items(ItemsUpdate {
                reset: before.is_none(),
                objects,
                removed,
                contents,
            })),
            PresentationEvent::Interface(InterfaceEvent::Player(Box::new(PlayerFacts {
                revision,
                numbers,
                session_seconds: 0.0,
            }))),
            PresentationEvent::Interface(InterfaceEvent::State(Box::new(InterfaceState {
                player: self.player,
                ..InterfaceState::default()
            }))),
        ]);
        events
    }

    /// Every object the data puts in the table: the player and his items.
    #[must_use]
    pub fn object_ids(&self) -> Vec<u32> {
        std::iter::once(self.player)
            .chain(self.items.iter().map(ClientObject::object_id))
            .collect()
    }
}

/// The player's numbers from the character's stats (the plugin's
/// `character_stats`): his int properties with the level, credits and
/// burden put in, his experience and luminance, each attribute as its
/// value before spells and its starting value, strength now from the
/// burden capacity, and what the sheet shows over the numbers (values now
/// where the stats give them, the vitals, the skills).
fn player_numbers(stats: &Value) -> Stats {
    let number = |v: &Value, key: &str| v.get(key).and_then(Value::as_i64);
    let mut ints = BTreeMap::new();
    if let Some(properties) = stats.get("properties").and_then(Value::as_object) {
        for (key, value) in properties {
            if let (Ok(key), Some(value)) = (key.parse::<u32>(), value.as_i64()) {
                ints.insert(key, value as i32);
            }
        }
    }
    for (key, property) in [
        ("burden_units", PROP_ENCUMBRANCE_VAL),
        ("level", PROP_LEVEL),
        ("skill_credits", PROP_AVAILABLE_SKILL_CREDITS),
    ] {
        if let Some(v) = number(stats, key) {
            ints.insert(property, v as i32);
        }
    }
    let mut int64s = BTreeMap::new();
    for (key, property) in [
        ("total_xp", INT64_TOTAL_XP),
        ("unassigned_xp", INT64_AVAILABLE_XP),
        ("luminance_earned", INT64_AVAILABLE_LUMINANCE),
        ("luminance_total", INT64_MAXIMUM_LUMINANCE),
    ] {
        if let Some(v) = number(stats, key) {
            int64s.insert(property, v);
        }
    }

    let mut attributes = [None; 6];
    let mut reported = ReportedSheet::default();
    for (index, name) in ATTRIBUTE_NAMES.iter().enumerate() {
        let Some(a) = stats.get("attributes").and_then(|a| a.get(*name)) else {
            continue;
        };
        let base = number(a, "base").unwrap_or(0).max(0);
        let start = number(a, "creation").unwrap_or(base).clamp(0, base);
        attributes[index] = Some(((base - start) as u32, start as u32));
        if let Some(now) = number(a, "current") {
            reported.attributes[SHEET_ROW_OF_ATTRIBUTE[index]] = Some(now as i32);
        }
    }
    for (index, name) in VITAL_NAMES.iter().enumerate() {
        let Some(v) = stats.get("vitals").and_then(|v| v.get(*name)) else {
            continue;
        };
        let Some(base) = number(v, "base") else {
            continue;
        };
        let max = number(v, "max").unwrap_or(base);
        let current = number(v, "current").unwrap_or(max);
        reported.vitals[index] = Some(ReportedVital {
            base_max: base as i32,
            max: max as i32,
            current: current as i32,
        });
    }
    if let Some(skills) = stats.get("skills").and_then(Value::as_object) {
        for (key, s) in skills {
            let Some(base) = number(s, "base") else {
                continue;
            };
            let advancement = match s.get("training").and_then(Value::as_str) {
                Some("Specialized") => SkillAdvancement::Specialized,
                Some("Trained") => SkillAdvancement::Trained,
                Some("Untrained") => SkillAdvancement::Untrained,
                _ => SkillAdvancement::Inactive,
            };
            reported.skills.push(ReportedSkill {
                key: reported_skill_key(key),
                base: base as i32,
                current: number(s, "current").unwrap_or(base) as i32,
                advancement,
            });
        }
    }

    // Strength now, from the capacity: capacity = strength x (150 + 30 x
    // min(augs, 5)).
    let augs = i64::from(ints.get(&PROP_CARRYING_CAPACITY_AUGS).copied().unwrap_or(0))
        .clamp(0, AUG_RANKS_COUNTED);
    let per_point = BURDEN_PER_STRENGTH + AUG_BURDEN_PER_RANK * augs;
    let strength_now = number(stats, "encumbrance_capacity")
        .filter(|&c| c > 0)
        .map(|c| c / per_point)
        .zip(attributes[0])
        .filter(|&(now, (ranks, start))| now > i64::from(ranks + start))
        .map(|(now, _)| now as u32);
    Stats {
        ints,
        int64s,
        attributes,
        strength_now,
        reported,
    }
}

/// The player's object in the world: only what the window reads of it
/// (its name and that it is there).
fn player_view_object(guid: u32, name: &str) -> ac_runtime::world::ObjectView {
    ac_runtime::world::ObjectView {
        key: ac_runtime::world::ObjectKey {
            local_id: 1,
            incarnation: 1,
        },
        guid,
        name: Some(name.to_owned()),
        setup_id: None,
        position: None,
        parent: None,
        placement: None,
        rest_placement: 0,
        scale: None,
        translucency: None,
        state: 0,
        velocity: None,
        model: ac_proto::ModelData::default(),
        motion_table_id: None,
        sound_table_id: None,
        radar: ac_runtime::world::RadarFacts::default(),
        motion: None,
        effects: ac_runtime::world::EffectFacts::default(),
        step_blend: None,
    }
}

/// When the page names no player: the container that holds the most items
/// and is not itself an item.
fn guess_player<'a>(items: impl Iterator<Item = &'a ClientObject> + Clone) -> u32 {
    let ids: HashSet<u32> = items.clone().map(ClientObject::object_id).collect();
    let mut counts: HashMap<u32, usize> = HashMap::new();
    for item in items {
        let holder = if item.wielder_id != 0 {
            item.wielder_id
        } else {
            item.container_id
        };
        if holder != 0 && !ids.contains(&holder) {
            *counts.entry(holder).or_default() += 1;
        }
    }
    counts
        .into_iter()
        .max_by_key(|&(id, n)| (n, std::cmp::Reverse(id)))
        .map_or(0, |(id, _)| id)
}

/// A table of a record, keyed by number.
fn table<T>(record: &Value, name: &str, read: impl Fn(&Value) -> Option<T>) -> BTreeMap<u32, T> {
    let mut out = BTreeMap::new();
    if let Some(map) = record.get(name).and_then(Value::as_object) {
        for (key, value) in map {
            if let (Ok(key), Some(value)) = (key.parse::<i64>(), read(value)) {
                out.insert(key as u32, value);
            }
        }
    }
    out
}

/// A list of spell ids.
fn spells(record: &Value, name: &str) -> Vec<u32> {
    record
        .get(name)
        .and_then(Value::as_array)
        .map(|a| {
            a.iter()
                .filter_map(Value::as_i64)
                .map(|v| v as u32)
                .collect()
        })
        .unwrap_or_default()
}

/// An icon as the client names it.
fn icon(short: i32) -> u32 {
    if short == 0 {
        0
    } else if (short as u32) < ICON_BASE {
        ICON_BASE + short as u32
    } else {
        short as u32
    }
}

/// One item record as the client's object.
fn item_of(record: &Value) -> Option<ClientObject> {
    let id = record.get("Id").and_then(Value::as_i64)? as u32;
    if id == 0 {
        return None;
    }
    let ints = table(record, "IntValues", |v| v.as_i64().map(|v| v as i32));
    let floats = table(record, "DoubleValues", Value::as_f64);
    let strings = table(record, "StringValues", |v| v.as_str().map(str::to_owned));
    let bools = table(record, "BoolValues", Value::as_bool);
    let int = |key: u32| ints.get(&key).copied();

    let mut o = ClientObject::new(id);
    o.name = strings
        .get(&keys::NAME)
        .cloned()
        .or_else(|| {
            record
                .get("Name")
                .and_then(Value::as_str)
                .map(str::to_owned)
        })
        .unwrap_or_default();
    o.plural_name = strings
        .get(&keys::SECONDARY_NAME)
        .cloned()
        .unwrap_or_default();
    o.weenie_class_id = int(keys::WEENIE_CLASS_ID).unwrap_or(0) as u32;
    o.item_type = ItemType(int(keys::CATEGORY).unwrap_or(0) as u32);
    let short_icon = int(keys::ICON)
        .or_else(|| record.get("Icon").and_then(Value::as_i64).map(|v| v as i32))
        .unwrap_or(0);
    o.icon_id = icon(short_icon);
    o.icon_overlay_id = icon(int(keys::ICON_OVERLAY).unwrap_or(0));
    o.icon_underlay_id = icon(int(keys::ICON_UNDERLAY).unwrap_or(0));
    o.effects = int(keys::ICON_OUTLINE).unwrap_or(0) as u32;
    if let Some(max) = int(keys::STACK_MAX) {
        o.stack_size_max = max.max(1);
        o.stack_size = int(keys::STACK_COUNT).unwrap_or(1).max(1);
    }
    o.burden = int(keys::BURDEN).unwrap_or(0);
    o.value = int(keys::VALUE).unwrap_or(0).max(0);
    // The framework's container is the pack holding the item, or the
    // creature wielding it.
    let holder = int(keys::CONTAINER)
        .or_else(|| {
            record
                .get("ContainerId")
                .and_then(Value::as_i64)
                .map(|v| v as i32)
        })
        .unwrap_or(0) as u32;
    let equipped = int(keys::EQUIPPED_SLOTS).unwrap_or(0) as u32;
    o.currently_equipped_location = EquipMask(equipped);
    if equipped != 0 {
        o.wielder_id = holder;
    } else {
        o.container_id = holder;
    }
    o.valid_locations = EquipMask(int(keys::EQUIPABLE_SLOTS).unwrap_or(0) as u32);
    o.items_capacity = int(keys::ITEM_SLOTS).unwrap_or(0);
    o.containers_capacity = int(keys::PACK_SLOTS).unwrap_or(0);
    if o.item_type.intersects(ItemType::CONTAINER) || o.items_capacity > 0 {
        o.container_type_hint = 1;
    }
    o.priority = int(keys::COVERAGE).unwrap_or(0) as u32;
    o.useability = int(keys::USABLE).map(|v| ItemUseable(v as u32));
    o.target_type = int(keys::USAGE_MASK).map(|v| ItemType(v as u32));
    o.public_weenie_bitfield = int(keys::BEHAVIOR).map(|v| PublicWeenieFlags(v as u32));
    o.combat_use = int(keys::EQUIP_TYPE).map(|v| v as u8);
    o.ammo_type = int(keys::MISSILE_TYPE).map(|v| v as u16);
    o.hook_type = int(keys::HOOK_TYPE).unwrap_or(0) as u32;
    o.spell_id = int(keys::SPELL_DID).map(|v| v as u32).filter(|&v| v != 0);
    o.structure = int(keys::STRUCTURE).unwrap_or(0);
    o.max_structure = int(keys::MAX_STRUCTURE).unwrap_or(0);
    o.workmanship = floats
        .get(&keys::SALVAGE_WORKMANSHIP)
        .copied()
        .unwrap_or(0.0) as f32;
    o.material_type = int(keys::MATERIAL).map(|v| v as u32).filter(|&v| v != 0);
    let mut appraised = spells(record, "Spells");
    appraised.extend(
        spells(record, "ActiveSpells")
            .into_iter()
            .map(|s| s | ACTIVE_SPELL_BIT),
    );
    o.appraised_spell_ids = appraised;

    // The game's own properties: everything below the framework's keys.
    let game = |key: &u32| *key < keys::FRAMEWORK_KEYS_FROM;
    o.properties.ints = ints.into_iter().filter(|(k, _)| game(k)).collect();
    o.properties.floats = floats.into_iter().filter(|(k, _)| game(k)).collect();
    o.properties.strings = strings.into_iter().filter(|(k, _)| game(k)).collect();
    o.properties.bools = bools.into_iter().filter(|(k, _)| game(k)).collect();
    Some(o)
}
