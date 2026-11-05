//! The player description (game event [`GameEventType::PLAYER_DESCRIPTION`]):
//! the player's whole state, sent once after entering the world.
//!
//! Layout, little-endian, starting at the event payload:
//!
//! ```text
//! u32 property flags, u32 weenie type
//! property tables, each gated on a property flag, in this order:
//!     int, int64, bool, double, string, data id, instance id, position
//!     (u16 count, u16 buckets, then count × (u32 key, value))
//! u32 vector flags, u32 has-health
//! attribute block, skill table, spell table, enchantments (vector flags)
//! trailer: u32 option flags, u32 options1, then option-gated sections:
//!     shortcuts, hotbar spell lists, desired components, spellbook
//!     filters, options2, gameplay options, inventory, equipped objects
//! ```
//!
//! A body that ends right after the property tables is accepted as a
//! partial description. A trailer that is cut short or malformed keeps
//! everything read before it, keeps the trailer entries read so far, and
//! sets [`PlayerDescription::trailer_truncated`].

use std::collections::BTreeMap;

use ac_bits::{Reader, cp1252};

use crate::{Enchantment, GameEventType, Position, ProtoError, Result};

macro_rules! flag_set {
    (
        $(#[$meta:meta])*
        $name:ident { $($(#[$cmeta:meta])* $flag:ident = $value:literal,)* }
    ) => {
        $(#[$meta])*
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
        pub struct $name(pub u32);

        impl $name {
            $($(#[$cmeta])* pub const $flag: Self = Self($value);)*

            /// True if every bit of `other` is set here.
            #[must_use]
            pub const fn contains(self, other: Self) -> bool {
                self.0 & other.0 == other.0
            }
        }
    };
}

flag_set! {
    /// Which property tables the description carries.
    DescriptionPropertyFlags {
        /// Integer table.
        INT = 0x0001,
        /// Boolean table.
        BOOL = 0x0002,
        /// Double table.
        DOUBLE = 0x0004,
        /// Data-id table.
        DATA_ID = 0x0008,
        /// String table.
        STRING = 0x0010,
        /// Position table.
        POSITION = 0x0020,
        /// Instance-id table.
        INSTANCE_ID = 0x0040,
        /// 64-bit integer table.
        INT64 = 0x0080,
    }
}

flag_set! {
    /// Which creature blocks the description carries.
    DescriptionVectorFlags {
        /// Attributes and vitals.
        ATTRIBUTE = 0x0001,
        /// Skills.
        SKILL = 0x0002,
        /// Learned spells.
        SPELL = 0x0100,
        /// Active enchantments.
        ENCHANTMENT = 0x0200,
    }
}

flag_set! {
    /// Which optional sections the trailer carries.
    CharacterOptionDataFlags {
        /// Shortcut bar.
        SHORTCUT = 0x0000_0001,
        /// Squelch list.
        SQUELCH_LIST = 0x0000_0002,
        /// One hotbar spell list (older form).
        MULTI_SPELL_LIST = 0x0000_0004,
        /// Desired spell components.
        DESIRED_COMPS = 0x0000_0008,
        /// Extended hotbar spell lists.
        EXTENDED_MULTI_SPELL_LISTS = 0x0000_0010,
        /// Spellbook filters.
        SPELLBOOK_FILTERS = 0x0000_0020,
        /// Second character-options word.
        CHARACTER_OPTIONS2 = 0x0000_0040,
        /// Timestamp format.
        TIMESTAMP_FORMAT = 0x0000_0080,
        /// Generic qualities.
        GENERIC_QUALITIES_DATA = 0x0000_0100,
        /// Gameplay options blob.
        GAMEPLAY_OPTIONS = 0x0000_0200,
        /// Eight hotbar spell lists.
        SPELL_LISTS8 = 0x0000_0400,
    }
}

/// Enchantment-block section bits.
const ENCHANTMENT_MULTIPLICATIVE: u32 = 0x01;
const ENCHANTMENT_ADDITIVE: u32 = 0x02;
const ENCHANTMENT_VITAE: u32 = 0x04;
const ENCHANTMENT_COOLDOWN: u32 = 0x08;

/// Spellbook filters before the trailer overrides them: every school and
/// level shown.
pub const DEFAULT_SPELLBOOK_FILTERS: u32 = 0x3FFF;

/// Largest shortcut, hotbar-list, desired-component, inventory or equipped
/// count accepted in the trailer.
pub const MAX_TRAILER_COUNT: u32 = 10_000;

/// Inventory container types run 0 (not a container), 1 (container),
/// 2 (foci).
const MAX_CONTAINER_TYPE: u32 = 2;

/// Which enchantment-block section an enchantment came from; decides
/// whether its modifier multiplies or adds.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum EnchantmentBucket {
    /// Multiplies the stat.
    Multiplicative = 1,
    /// Adds to the stat.
    Additive = 2,
    /// The death penalty.
    Vitae = 4,
    /// An item cooldown.
    Cooldown = 8,
}

/// The property tables, keyed by raw property id. A key that appears
/// twice keeps its last value.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PropertyTables {
    /// Integer properties.
    pub ints: BTreeMap<u32, i32>,
    /// 64-bit integer properties.
    pub int64s: BTreeMap<u32, i64>,
    /// Boolean properties (any non-zero word is true).
    pub bools: BTreeMap<u32, bool>,
    /// Double properties.
    pub floats: BTreeMap<u32, f64>,
    /// String properties.
    pub strings: BTreeMap<u32, String>,
    /// Data-file id properties.
    pub data_ids: BTreeMap<u32, u32>,
    /// Object id properties.
    pub instance_ids: BTreeMap<u32, u32>,
}

/// One attribute-block entry. Ids 1–6 are the primary attributes, 7–9
/// the vitals (health, stamina, mana), which also carry a current value.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AttributeEntry {
    /// Attribute id, 1–9.
    pub id: u32,
    /// Ranks raised.
    pub ranks: u32,
    /// Starting value.
    pub start: u32,
    /// Experience spent.
    pub xp: u32,
    /// Current value; vitals only.
    pub current: Option<u32>,
}

/// One skill-table entry.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SkillEntry {
    /// Skill id.
    pub skill: u32,
    /// Ranks raised; a `u16` on the wire.
    pub ranks: u16,
    /// Rank adjustment word.
    pub adjust_pp: u16,
    /// Untrained, trained or specialized.
    pub advancement_class: u32,
    /// Experience spent.
    pub xp: u32,
    /// Initial level.
    pub init: u32,
    /// Resistance at the last check.
    pub resistance: u32,
    /// When the skill was last used, game time in seconds.
    pub last_used: f64,
}

/// An active enchantment and the section it came from.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlayerEnchantment {
    /// Section.
    pub bucket: EnchantmentBucket,
    /// The record.
    pub enchantment: Enchantment,
}

/// One shortcut-bar slot.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Shortcut {
    /// Slot index.
    pub index: i32,
    /// Object in the slot, or 0.
    pub object_id: u32,
    /// Spell in the slot (id and layer word), or 0.
    pub spell_id: u32,
}

/// One desired spell component and its amount.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DesiredComponent {
    /// Component id.
    pub id: u32,
    /// How many to keep.
    pub amount: u32,
}

/// One object in the player's main pack.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct InventoryEntry {
    /// The object.
    pub guid: u32,
    /// 0 not a container, 1 container, 2 foci.
    pub container_type: u32,
}

/// One object the player wields or wears.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct EquippedEntry {
    /// The object.
    pub guid: u32,
    /// Equip-slot bit field.
    pub equip_location: u32,
    /// Orders overlapping equips in the same slot.
    pub priority: u32,
}

/// A parsed player description.
#[derive(Debug, Clone, PartialEq)]
pub struct PlayerDescription {
    /// Which property tables were present.
    pub property_flags: DescriptionPropertyFlags,
    /// The player's weenie type.
    pub weenie_type: u32,
    /// Property tables.
    pub properties: PropertyTables,
    /// Saved positions, keyed by position slot.
    pub positions: BTreeMap<u32, Position>,
    /// Which creature blocks were present.
    pub vector_flags: DescriptionVectorFlags,
    /// The has-health word was non-zero.
    pub has_health: bool,
    /// Attribute entries in wire order.
    pub attributes: Vec<AttributeEntry>,
    /// Skill entries in wire order.
    pub skills: Vec<SkillEntry>,
    /// Learned spells: spell id to power.
    pub spells: BTreeMap<u32, f32>,
    /// Active enchantments in wire order.
    pub enchantments: Vec<PlayerEnchantment>,
    /// Which trailer sections were present.
    pub option_flags: CharacterOptionDataFlags,
    /// First character-options word.
    pub options1: u32,
    /// Second character-options word, 0 when absent.
    pub options2: u32,
    /// Shortcut bar.
    pub shortcuts: Vec<Shortcut>,
    /// Hotbar spell lists: eight, one (older form) or none.
    pub hotbar_spells: Vec<Vec<u32>>,
    /// Desired spell components.
    pub desired_comps: Vec<DesiredComponent>,
    /// Spellbook filters. [`DEFAULT_SPELLBOOK_FILTERS`] when the trailer
    /// does not set them; 0 for a partial description.
    pub spellbook_filters: u32,
    /// The gameplay-options blob, kept raw.
    pub gameplay_options: Vec<u8>,
    /// Objects in the main pack.
    pub inventory: Vec<InventoryEntry>,
    /// Objects wielded or worn.
    pub equipped: Vec<EquippedEntry>,
    /// The trailer was cut short or malformed; trailer fields may be
    /// partly filled.
    pub trailer_truncated: bool,
}

impl PlayerDescription {
    /// The event type this payload belongs to.
    pub const EVENT_TYPE: GameEventType = GameEventType::PLAYER_DESCRIPTION;

    /// Parse a player-description event payload (the bytes after the game
    /// event header).
    ///
    /// Truncation or a bad value up to the end of the enchantments is an
    /// error, except that a body ending right after the property tables
    /// (fewer than 8 bytes left) is returned as a partial description.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        let property_flags = DescriptionPropertyFlags(r.u32()?);
        let weenie_type = r.u32()?;
        let mut d = Self {
            property_flags,
            weenie_type,
            properties: PropertyTables::default(),
            positions: BTreeMap::new(),
            vector_flags: DescriptionVectorFlags::default(),
            has_health: false,
            attributes: Vec::new(),
            skills: Vec::new(),
            spells: BTreeMap::new(),
            enchantments: Vec::new(),
            option_flags: CharacterOptionDataFlags::default(),
            options1: 0,
            options2: 0,
            shortcuts: Vec::new(),
            hotbar_spells: Vec::new(),
            desired_comps: Vec::new(),
            spellbook_filters: 0,
            gameplay_options: Vec::new(),
            inventory: Vec::new(),
            equipped: Vec::new(),
            trailer_truncated: false,
        };

        read_property_tables(&mut r, property_flags, &mut d)?;

        if r.remaining() < 8 {
            return Ok(d);
        }
        d.vector_flags = DescriptionVectorFlags(r.u32()?);
        d.has_health = r.u32()? != 0;

        if d.vector_flags.contains(DescriptionVectorFlags::ATTRIBUTE) {
            read_attribute_block(&mut r, &mut d.attributes)?;
        }
        if d.vector_flags.contains(DescriptionVectorFlags::SKILL) {
            read_skill_table(&mut r, &mut d.skills)?;
        }
        if d.vector_flags.contains(DescriptionVectorFlags::SPELL) {
            let count = read_table_header(&mut r)?;
            for _ in 0..count {
                let spell = r.u32()?;
                let power = r.f32()?;
                d.spells.insert(spell, power);
            }
        }
        if d.vector_flags.contains(DescriptionVectorFlags::ENCHANTMENT) {
            read_enchantment_block(&mut r, &mut d.enchantments)?;
        }

        d.spellbook_filters = DEFAULT_SPELLBOOK_FILTERS;
        if r.remaining() >= 8 && read_trailer(&mut r, payload, &mut d).is_err() {
            d.trailer_truncated = true;
        }
        Ok(d)
    }
}

/// Table header: u16 count, u16 bucket count (ignored).
fn read_table_header(r: &mut Reader<'_>) -> Result<u16> {
    let count = r.u16()?;
    r.u16()?;
    Ok(count)
}

/// A u16-length Windows-1252 string padded to 4 bytes, where padding
/// missing at the end of the payload is tolerated: the cursor stops at the
/// end and the next read fails or finds nothing left.
fn read_string(r: &mut Reader<'_>) -> Result<String> {
    let len = usize::from(r.u16()?);
    let text = cp1252::decode(r.bytes(len)?);
    let pad = (4 - ((2 + len) & 3)) & 3;
    r.skip(pad.min(r.remaining()))?;
    Ok(text)
}

fn read_property_tables(
    r: &mut Reader<'_>,
    flags: DescriptionPropertyFlags,
    d: &mut PlayerDescription,
) -> Result<()> {
    let p = &mut d.properties;
    if flags.contains(DescriptionPropertyFlags::INT) {
        for _ in 0..read_table_header(r)? {
            let key = r.u32()?;
            p.ints.insert(key, r.i32()?);
        }
    }
    if flags.contains(DescriptionPropertyFlags::INT64) {
        for _ in 0..read_table_header(r)? {
            let key = r.u32()?;
            p.int64s.insert(key, r.i64()?);
        }
    }
    if flags.contains(DescriptionPropertyFlags::BOOL) {
        for _ in 0..read_table_header(r)? {
            let key = r.u32()?;
            p.bools.insert(key, r.u32()? != 0);
        }
    }
    if flags.contains(DescriptionPropertyFlags::DOUBLE) {
        for _ in 0..read_table_header(r)? {
            let key = r.u32()?;
            p.floats.insert(key, r.f64()?);
        }
    }
    if flags.contains(DescriptionPropertyFlags::STRING) {
        for _ in 0..read_table_header(r)? {
            let key = r.u32()?;
            p.strings.insert(key, read_string(r)?);
        }
    }
    if flags.contains(DescriptionPropertyFlags::DATA_ID) {
        for _ in 0..read_table_header(r)? {
            let key = r.u32()?;
            p.data_ids.insert(key, r.u32()?);
        }
    }
    if flags.contains(DescriptionPropertyFlags::INSTANCE_ID) {
        for _ in 0..read_table_header(r)? {
            let key = r.u32()?;
            p.instance_ids.insert(key, r.u32()?);
        }
    }
    if flags.contains(DescriptionPropertyFlags::POSITION) {
        for _ in 0..read_table_header(r)? {
            let key = r.u32()?;
            d.positions.insert(key, Position::read(r)?);
        }
    }
    Ok(())
}

/// u32 presence bits (bit `id - 1`), then each present entry in id order:
/// ranks, start, xp, and for the vitals 7–9 also current.
fn read_attribute_block(r: &mut Reader<'_>, out: &mut Vec<AttributeEntry>) -> Result<()> {
    let present = r.u32()?;
    for id in 1..=9u32 {
        if present & (1 << (id - 1)) == 0 {
            continue;
        }
        let ranks = r.u32()?;
        let start = r.u32()?;
        let xp = r.u32()?;
        let current = if id >= 7 { Some(r.u32()?) } else { None };
        out.push(AttributeEntry {
            id,
            ranks,
            start,
            xp,
            current,
        });
    }
    Ok(())
}

fn read_skill_table(r: &mut Reader<'_>, out: &mut Vec<SkillEntry>) -> Result<()> {
    for _ in 0..read_table_header(r)? {
        out.push(SkillEntry {
            skill: r.u32()?,
            ranks: r.u16()?,
            adjust_pp: r.u16()?,
            advancement_class: r.u32()?,
            xp: r.u32()?,
            init: r.u32()?,
            resistance: r.u32()?,
            last_used: r.f64()?,
        });
    }
    Ok(())
}

/// u32 section mask (absent when fewer than 4 bytes remain), then the
/// multiplicative, additive and cooldown lists, then the single vitae
/// record.
fn read_enchantment_block(r: &mut Reader<'_>, out: &mut Vec<PlayerEnchantment>) -> Result<()> {
    if r.remaining() < 4 {
        return Ok(());
    }
    let mask = r.u32()?;
    for (bit, bucket) in [
        (
            ENCHANTMENT_MULTIPLICATIVE,
            EnchantmentBucket::Multiplicative,
        ),
        (ENCHANTMENT_ADDITIVE, EnchantmentBucket::Additive),
        (ENCHANTMENT_COOLDOWN, EnchantmentBucket::Cooldown),
    ] {
        if mask & bit != 0 {
            for enchantment in Enchantment::read_list(r)? {
                out.push(PlayerEnchantment {
                    bucket,
                    enchantment,
                });
            }
        }
    }
    if mask & ENCHANTMENT_VITAE != 0 {
        out.push(PlayerEnchantment {
            bucket: EnchantmentBucket::Vitae,
            enchantment: Enchantment::read(r)?,
        });
    }
    Ok(())
}

fn check_trailer_count(what: &'static str, count: u32) -> Result<()> {
    if count > MAX_TRAILER_COUNT {
        Err(ProtoError::CountTooLarge {
            what,
            count,
            max: MAX_TRAILER_COUNT,
        })
    } else {
        Ok(())
    }
}

fn read_spell_list(r: &mut Reader<'_>) -> Result<Vec<u32>> {
    let count = r.u32()?;
    check_trailer_count("hotbar spell list", count)?;
    let mut list = Vec::new();
    for _ in 0..count {
        list.push(r.u32()?);
    }
    Ok(list)
}

/// The trailer, written straight into `d` so a failure part-way keeps what
/// was read. Called only with at least 8 bytes left.
fn read_trailer(r: &mut Reader<'_>, payload: &[u8], d: &mut PlayerDescription) -> Result<()> {
    d.option_flags = CharacterOptionDataFlags(r.u32()?);
    d.options1 = r.u32()?;
    let flags = d.option_flags;

    if flags.contains(CharacterOptionDataFlags::SHORTCUT) {
        let count = r.u32()?;
        check_trailer_count("shortcut", count)?;
        for _ in 0..count {
            d.shortcuts.push(Shortcut {
                index: r.i32()?,
                object_id: r.u32()?,
                spell_id: r.u32()?,
            });
        }
    }

    if flags.contains(CharacterOptionDataFlags::SPELL_LISTS8) {
        for _ in 0..8 {
            let list = read_spell_list(r)?;
            d.hotbar_spells.push(list);
        }
    } else if r.remaining() >= 4 {
        let list = read_spell_list(r)?;
        d.hotbar_spells.push(list);
    }

    if flags.contains(CharacterOptionDataFlags::DESIRED_COMPS) {
        let count = r.u16()?;
        r.u16()?;
        check_trailer_count("desired component", u32::from(count))?;
        for _ in 0..count {
            d.desired_comps.push(DesiredComponent {
                id: r.u32()?,
                amount: r.u32()?,
            });
        }
    }

    if r.remaining() >= 4 {
        d.spellbook_filters = r.u32()?;
    }

    if flags.contains(CharacterOptionDataFlags::CHARACTER_OPTIONS2) {
        d.options2 = r.u32()?;
    }

    if flags.contains(CharacterOptionDataFlags::GAMEPLAY_OPTIONS) {
        // The gameplay-options blob has no length on the wire: find the
        // first 4-aligned offset after it from which the inventory and
        // equipped lists parse and end exactly at the end of the payload.
        let start = r.position();
        if let Some(found) = find_inventory_after_gameplay_options(payload, start) {
            d.gameplay_options = payload[start..found.start].to_vec();
            d.inventory = found.inventory;
            d.equipped = found.equipped;
            r.seek(payload.len())?;
        }
    } else {
        // Inventory and equipped follow directly. A failure here leaves the
        // entries read so far and does not mark the trailer truncated.
        unpack_inventory_strict(r, &mut d.inventory, &mut d.equipped);
    }
    Ok(())
}

/// Read the inventory count and entries, then the equipped count and
/// entries. Returns false at the first short read, count above the limit
/// or container type above 2; the entries read before that stay in the
/// vectors.
fn unpack_inventory_strict(
    r: &mut Reader<'_>,
    inventory: &mut Vec<InventoryEntry>,
    equipped: &mut Vec<EquippedEntry>,
) -> bool {
    inventory.clear();
    equipped.clear();
    let Ok(count) = r.u32() else { return false };
    if count > MAX_TRAILER_COUNT {
        return false;
    }
    for _ in 0..count {
        if r.remaining() < 8 {
            return false;
        }
        let (Ok(guid), Ok(container_type)) = (r.u32(), r.u32()) else {
            return false;
        };
        if container_type > MAX_CONTAINER_TYPE {
            return false;
        }
        inventory.push(InventoryEntry {
            guid,
            container_type,
        });
    }
    let Ok(count) = r.u32() else { return false };
    if count > MAX_TRAILER_COUNT {
        return false;
    }
    for _ in 0..count {
        if r.remaining() < 12 {
            return false;
        }
        let (Ok(guid), Ok(equip_location), Ok(priority)) = (r.u32(), r.u32(), r.u32()) else {
            return false;
        };
        equipped.push(EquippedEntry {
            guid,
            equip_location,
            priority,
        });
    }
    true
}

/// Whether lists found by the search can be the real ones: every entry
/// names an object, and every equipped entry a slot. Without this, the last
/// zero word of the gameplay options reads as an empty inventory and the
/// real inventory as equipped entries, which also ends at the payload end.
fn plausible(inventory: &[InventoryEntry], equipped: &[EquippedEntry]) -> bool {
    inventory.iter().all(|e| e.guid != 0)
        && equipped
            .iter()
            .all(|e| e.guid != 0 && e.equip_location != 0)
}

struct FoundInventory {
    start: usize,
    inventory: Vec<InventoryEntry>,
    equipped: Vec<EquippedEntry>,
}

fn find_inventory_after_gameplay_options(payload: &[u8], start: usize) -> Option<FoundInventory> {
    let last = payload.len().checked_sub(8)?;
    if start > last {
        return None;
    }
    let mut candidate = start.next_multiple_of(4);
    while candidate <= last {
        let mut r = Reader::new(payload);
        r.seek(candidate).ok()?;
        let (mut inventory, mut equipped) = (Vec::new(), Vec::new());
        if unpack_inventory_strict(&mut r, &mut inventory, &mut equipped)
            && r.position() == payload.len()
            && plausible(&inventory, &equipped)
        {
            return Some(FoundInventory {
                start: candidate,
                inventory,
                equipped,
            });
        }
        candidate += 4;
    }
    None
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC
    //! `tests/AcDream.Core.Net.Tests/PlayerDescriptionParserTests.cs`, plus
    //! truncation and edge cases of `PlayerDescriptionParser.TryParse`.

    use ac_bits::Writer;

    use super::*;
    use crate::enchantment::tests::{MULTIPLICATIVE, write_enchantment};
    use crate::{Quaternion, Vector3};

    fn parse(w: &Writer) -> PlayerDescription {
        PlayerDescription::parse(w.as_slice()).unwrap()
    }

    fn words(w: &mut Writer, words: &[u32]) {
        for &word in words {
            w.u32(word);
        }
    }

    /// Header with no property tables, then vector flags and has-health.
    fn header(vector_flags: u32) -> Writer {
        let mut w = Writer::new();
        words(&mut w, &[0, 0x52, vector_flags, 1]);
        w
    }

    /// No tables, attribute and enchantment flags, both blocks empty: the
    /// trailer starts next.
    fn before_trailer() -> Writer {
        let mut w = header(0x201);
        words(&mut w, &[0, 0]);
        w
    }

    fn vital(w: &mut Writer, ranks: u32, start: u32, xp: u32, current: u32) {
        words(w, &[ranks, start, xp, current]);
    }

    #[test]
    fn truncated_header_is_an_error() {
        assert!(PlayerDescription::parse(&[0; 4]).is_err());
        assert!(PlayerDescription::parse(&[0; 7]).is_err());
    }

    #[test]
    fn empty_body_parses_header_only() {
        let mut w = Writer::new();
        words(&mut w, &[0, 0x52, 0, 0]);
        let d = parse(&w);
        assert_eq!(d.weenie_type, 0x52);
        assert_eq!(d.property_flags, DescriptionPropertyFlags(0));
        assert_eq!(d.vector_flags, DescriptionVectorFlags(0));
        assert!(!d.has_health);
        assert!(d.attributes.is_empty());
        // The full path sets the default filters and leaves the trailer
        // alone when fewer than 8 bytes remain.
        assert_eq!(d.spellbook_filters, DEFAULT_SPELLBOOK_FILTERS);
        assert!(!d.trailer_truncated);
    }

    #[test]
    fn login_int64_table_populates_total_and_available_experience() {
        let bytes: &[u8] = &[
            0x80, 0x00, 0x00, 0x00, // property flags: INT64
            0x01, 0x00, 0x00, 0x00, // weenie type 1
            0x02, 0x00, 0x40, 0x00, // 2 entries, 64 buckets
            0x01, 0x00, 0x00, 0x00, // key 1
            0xD2, 0x02, 0x96, 0x49, 0x00, 0x00, 0x00, 0x00, // 1_234_567_890
            0x02, 0x00, 0x00, 0x00, // key 2
            0xF8, 0x24, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, // 75_000
            0x00, 0x00, 0x00, 0x00, // vector flags
            0x00, 0x00, 0x00, 0x00, // has health
        ];
        let d = PlayerDescription::parse(bytes).unwrap();
        assert_eq!(d.properties.int64s.get(&1), Some(&1_234_567_890));
        assert_eq!(d.properties.int64s.get(&2), Some(&75_000));
    }

    fn full_attribute_block(health: u32, stamina: u32, mana: u32) -> Writer {
        let mut w = header(0x01);
        w.u32(0x1FF);
        for i in 1..=6u32 {
            words(&mut w, &[10 * i, 50 + i, 1000]);
        }
        vital(&mut w, 20, 80, 5000, health);
        vital(&mut w, 20, 80, 5000, stamina);
        vital(&mut w, 20, 80, 5000, mana);
        w
    }

    #[test]
    fn attribute_block_populates_all_nine_entries() {
        let w = full_attribute_block(90, 75, 60);
        assert_eq!(w.len(), 140);
        let d = parse(&w);
        assert!(d.has_health);
        assert_eq!(d.attributes.len(), 9);
        for i in 1..=6u32 {
            let a = d.attributes.iter().find(|a| a.id == i).unwrap();
            assert_eq!(
                (a.ranks, a.start, a.xp, a.current),
                (10 * i, 50 + i, 1000, None)
            );
        }
        let current = |id| d.attributes.iter().find(|a| a.id == id).unwrap().current;
        let health = d.attributes.iter().find(|a| a.id == 7).unwrap();
        assert_eq!((health.ranks, health.start), (20, 80));
        assert_eq!(current(7), Some(90));
        assert_eq!(current(8), Some(75));
        assert_eq!(current(9), Some(60));
    }

    #[test]
    fn skips_attributes_whose_presence_bit_is_clear() {
        // Strength (bit 0) and health (bit 6) only.
        let mut w = header(0x01);
        w.u32(0x41);
        words(&mut w, &[100, 60, 0]);
        vital(&mut w, 25, 75, 0, 99);
        assert_eq!(w.len(), 48);
        let d = parse(&w);
        assert_eq!(d.attributes.len(), 2);
        assert_eq!(d.attributes[0].id, 1);
        assert_eq!(d.attributes[1].id, 7);
        assert_eq!(d.attributes[1].current, Some(99));
    }

    #[test]
    fn property_tables_are_walked_to_the_attribute_block() {
        let mut w = Writer::new();
        words(&mut w, &[0x0011, 0x52]); // INT | STRING
        w.u16(2);
        w.u16(32);
        w.u32(101);
        w.i32(42);
        w.u32(102);
        w.i32(-7);
        w.u16(1);
        w.u16(32);
        w.u32(1);
        w.string16l("Acdream").unwrap();
        words(&mut w, &[0x01, 1, 0x40]);
        vital(&mut w, 30, 70, 0, 88);

        let d = parse(&w);
        assert_eq!(d.properties.ints.len(), 2);
        assert_eq!(d.properties.ints[&101], 42);
        assert_eq!(d.properties.ints[&102], -7);
        assert_eq!(d.properties.strings[&1], "Acdream");
        assert_eq!(d.attributes.len(), 1);
        assert_eq!(d.attributes[0].id, 7);
        assert_eq!(d.attributes[0].current, Some(88));
    }

    #[test]
    fn every_table_kind_and_position_round_trip() {
        let mut w = Writer::new();
        words(&mut w, &[0xFF, 0x52]);
        // In wire order: int, int64, bool, double, string, data id,
        // instance id, position.
        w.u16(1);
        w.u16(0);
        w.u32(1);
        w.i32(-5);
        w.u16(1);
        w.u16(0);
        w.u32(2);
        w.i64(-6);
        w.u16(2);
        w.u16(0);
        words(&mut w, &[3, 2, 4, 0]);
        w.u16(1);
        w.u16(0);
        w.u32(5);
        w.f64(0.25);
        w.u16(1);
        w.u16(0);
        w.u32(6);
        w.string16l("é").unwrap();
        w.u16(1);
        w.u16(0);
        words(&mut w, &[7, 0x0600_0001]);
        w.u16(1);
        w.u16(0);
        words(&mut w, &[8, 0x5000_0002]);
        w.u16(1);
        w.u16(0);
        words(&mut w, &[0x0E, 0xA9B4_0031]);
        for f in [84.0f32, 132.5, 12.0, 1.0, 0.0, 0.0, 0.0] {
            w.f32(f);
        }
        words(&mut w, &[0, 0]);

        let d = parse(&w);
        let p = &d.properties;
        assert_eq!(p.ints[&1], -5);
        assert_eq!(p.int64s[&2], -6);
        assert_eq!((p.bools[&3], p.bools[&4]), (true, false));
        assert_eq!(p.floats[&5], 0.25);
        assert_eq!(p.strings[&6], "é");
        assert_eq!(p.data_ids[&7], 0x0600_0001);
        assert_eq!(p.instance_ids[&8], 0x5000_0002);
        assert_eq!(
            d.positions[&0x0E],
            Position {
                cell_id: 0xA9B4_0031,
                origin: Vector3 {
                    x: 84.0,
                    y: 132.5,
                    z: 12.0
                },
                orientation: Quaternion::IDENTITY,
            }
        );
    }

    #[test]
    fn duplicate_keys_keep_the_last_value() {
        let mut w = Writer::new();
        words(&mut w, &[0x01, 0x52]);
        w.u16(2);
        w.u16(0);
        words(&mut w, &[9, 1, 9, 2]);
        let d = parse(&w);
        assert_eq!(d.properties.ints[&9], 2);
        assert_eq!(d.properties.ints.len(), 1);
    }

    #[test]
    fn skill_table_reads_32_byte_entries() {
        let mut w = header(0x02);
        w.u16(1);
        w.u16(32);
        w.u32(6);
        w.u16(50);
        w.u16(1);
        words(&mut w, &[3, 1000, 10, 0]);
        w.f64(12345.678);
        let d = parse(&w);
        assert_eq!(
            d.skills,
            [SkillEntry {
                skill: 6,
                ranks: 50,
                adjust_pp: 1,
                advancement_class: 3,
                xp: 1000,
                init: 10,
                resistance: 0,
                last_used: 12345.678
            }]
        );
    }

    #[test]
    fn enchantment_block_keeps_stat_mod_and_bucket() {
        let mut w = header(0x301);
        w.u32(0); // no attributes
        w.u16(0); // empty spell table
        w.u16(0);
        w.u32(0x03); // multiplicative | additive
        w.u32(1);
        write_enchantment(&mut w, &MULTIPLICATIVE);
        let additive = Enchantment {
            spell_id: 5678,
            layer: 6,
            spell_category: 101,
            power_level: 100,
            start_time: 13.0,
            duration: 1500.0,
            caster_guid: 0xCAFE_0002,
            stat_mod_type: 0x0002_0000,
            stat_mod_key: 5,
            stat_mod_value: 25.0,
            ..MULTIPLICATIVE
        };
        w.u32(1);
        write_enchantment(&mut w, &additive);

        let d = parse(&w);
        assert_eq!(
            d.enchantments,
            [
                PlayerEnchantment {
                    bucket: EnchantmentBucket::Multiplicative,
                    enchantment: MULTIPLICATIVE
                },
                PlayerEnchantment {
                    bucket: EnchantmentBucket::Additive,
                    enchantment: additive
                },
            ]
        );
        assert_eq!(d.enchantments[0].enchantment.spell_id, 1234);
        assert_eq!(d.enchantments[0].enchantment.stat_mod_key, 3);
        assert_eq!(d.enchantments[1].enchantment.stat_mod_value, 25.0);
    }

    #[test]
    fn vitae_singleton_appears_in_enchantments() {
        let mut w = header(0x201);
        w.u32(0);
        w.u32(0x04);
        let vitae = Enchantment {
            spell_id: 7777,
            layer: 0,
            spell_category: 0,
            power_level: 0,
            start_time: 0.0,
            duration: -1.0,
            caster_guid: 0,
            degrade_modifier: 0.0,
            degrade_limit: 0.0,
            stat_mod_type: 0,
            stat_mod_key: 1,
            stat_mod_value: 0.95,
            ..MULTIPLICATIVE
        };
        write_enchantment(&mut w, &vitae);
        let d = parse(&w);
        assert_eq!(d.enchantments.len(), 1);
        assert_eq!(d.enchantments[0].bucket, EnchantmentBucket::Vitae);
        assert_eq!(d.enchantments[0].enchantment.stat_mod_value, 0.95);
    }

    #[test]
    fn enchantment_sections_read_multiplicative_additive_cooldown_then_vitae() {
        let mut w = header(0x200);
        w.u32(0x0F);
        for spell_id in [1u16, 2, 3] {
            w.u32(1);
            write_enchantment(
                &mut w,
                &Enchantment {
                    spell_id,
                    ..MULTIPLICATIVE
                },
            );
        }
        write_enchantment(
            &mut w,
            &Enchantment {
                spell_id: 4,
                ..MULTIPLICATIVE
            },
        );
        let d = parse(&w);
        let order: Vec<_> = d
            .enchantments
            .iter()
            .map(|e| (e.bucket, e.enchantment.spell_id))
            .collect();
        assert_eq!(
            order,
            [
                (EnchantmentBucket::Multiplicative, 1),
                (EnchantmentBucket::Additive, 2),
                (EnchantmentBucket::Cooldown, 3),
                (EnchantmentBucket::Vitae, 4),
            ]
        );
    }

    #[test]
    fn missing_enchantment_mask_is_no_enchantments() {
        let d = parse(&header(0x200));
        assert!(d.enchantments.is_empty());
        assert!(!d.trailer_truncated);
    }

    #[test]
    fn bad_enchantment_fails_the_whole_parse() {
        let mut w = header(0x200);
        w.u32(0x04);
        write_enchantment(
            &mut w,
            &Enchantment {
                duration: f64::NAN,
                ..MULTIPLICATIVE
            },
        );
        assert_eq!(
            PlayerDescription::parse(w.as_slice()),
            Err(ProtoError::NonFinite {
                what: "enchantment"
            })
        );
    }

    #[test]
    fn spell_table_populates_spells() {
        let mut w = header(0x101);
        w.u32(0);
        w.u16(2);
        w.u16(64);
        w.u32(1234);
        w.f32(2.0);
        w.u32(5678);
        w.f32(2.0);
        let d = parse(&w);
        assert_eq!(d.spells.len(), 2);
        assert_eq!(d.spells[&1234], 2.0);
        assert_eq!(d.spells[&5678], 2.0);
    }

    #[test]
    fn trailer_option_flags_and_options1_follow_the_enchantments() {
        let mut w = before_trailer();
        words(&mut w, &[0, 0xDEAD_BEEF]);
        let d = parse(&w);
        assert_eq!(d.option_flags, CharacterOptionDataFlags(0));
        assert_eq!(d.options1, 0xDEAD_BEEF);
        assert!(d.shortcuts.is_empty());
        assert!(d.inventory.is_empty());
        assert_eq!(d.options2, 0);
        assert_eq!(d.spellbook_filters, DEFAULT_SPELLBOOK_FILTERS);
        assert!(d.hotbar_spells.is_empty());
        assert!(d.desired_comps.is_empty());
        assert!(d.gameplay_options.is_empty());
        assert!(d.equipped.is_empty());
        assert!(!d.trailer_truncated);
    }

    #[test]
    fn trailer_shortcuts_populate() {
        let mut w = before_trailer();
        words(&mut w, &[0x01, 0xCAFE, 2]);
        words(&mut w, &[0, 0xAABB_CCDD, 0]);
        words(&mut w, &[7, 0, 0x0005_04D2]);
        let d = parse(&w);
        assert_eq!(
            d.shortcuts,
            [
                Shortcut {
                    index: 0,
                    object_id: 0xAABB_CCDD,
                    spell_id: 0
                },
                Shortcut {
                    index: 7,
                    object_id: 0,
                    spell_id: 0x0005_04D2
                },
            ]
        );
    }

    #[test]
    fn shortcuts_cut_mid_list_flag_truncation_and_keep_earlier_entries() {
        let mut w = before_trailer();
        words(&mut w, &[0x01, 0, 3]);
        w.u32(1);
        w.u32(0xAAAA);
        w.u16(10);
        w.u16(1);
        // Second entry ends before its spell word.
        words(&mut w, &[2, 0xBBBB]);
        let d = parse(&w);
        assert!(d.trailer_truncated);
        assert_eq!(d.shortcuts.len(), 1);
        assert_eq!(d.shortcuts[0].index, 1);
        assert_eq!(d.shortcuts[0].spell_id, 0x0001_000A);
    }

    #[test]
    fn spell_lists8_reads_eight_lists() {
        let mut w = before_trailer();
        words(&mut w, &[0x400, 0]);
        words(&mut w, &[2, 11, 12, 1, 21, 0, 0, 0, 0, 0, 3, 81, 82, 83]);
        let d = parse(&w);
        assert_eq!(d.hotbar_spells.len(), 8);
        assert_eq!(d.hotbar_spells[0], [11, 12]);
        assert_eq!(d.hotbar_spells[1], [21]);
        assert!(d.hotbar_spells[2].is_empty());
        assert_eq!(d.hotbar_spells[7], [81, 82, 83]);
    }

    #[test]
    fn without_spell_lists8_one_older_list_is_read() {
        let mut w = before_trailer();
        words(&mut w, &[0, 0, 2, 101, 102]);
        let d = parse(&w);
        assert_eq!(d.hotbar_spells, [vec![101, 102]]);
    }

    #[test]
    fn a_hotbar_list_cut_short_is_not_kept() {
        let mut w = before_trailer();
        words(&mut w, &[0x400, 0, 2, 11]);
        let d = parse(&w);
        assert!(d.trailer_truncated);
        assert!(d.hotbar_spells.is_empty());
    }

    #[test]
    fn trailer_absent_keeps_upstream_without_flagging_truncation() {
        let mut w = header(0x201);
        words(&mut w, &[0x01, 50, 10, 0]); // strength only
        w.u32(0); // empty enchantment mask
        w.u32(0xCAFE); // 4 bytes: too few for a trailer
        let d = parse(&w);
        assert_eq!(d.attributes.len(), 1);
        assert_eq!(d.attributes[0].id, 1);
        assert!(!d.trailer_truncated);
        assert_eq!(d.option_flags, CharacterOptionDataFlags(0));
        assert_eq!(d.options1, 0);
    }

    #[test]
    fn desired_comps_read_id_amount_pairs() {
        let mut w = before_trailer();
        words(&mut w, &[0x08, 0, 0]);
        w.u16(2);
        w.u16(0);
        words(&mut w, &[0xAA, 50, 0xBB, 75]);
        let d = parse(&w);
        assert_eq!(
            d.desired_comps,
            [
                DesiredComponent {
                    id: 0xAA,
                    amount: 50
                },
                DesiredComponent {
                    id: 0xBB,
                    amount: 75
                },
            ]
        );
    }

    #[test]
    fn spellbook_filters_are_an_optional_word() {
        let mut w = before_trailer();
        words(&mut w, &[0, 0, 0, 0xF00D_BA42]);
        assert_eq!(parse(&w).spellbook_filters, 0xF00D_BA42);
    }

    #[test]
    fn options2_is_gated_on_its_flag() {
        let mut w = before_trailer();
        words(&mut w, &[0x40, 0, 0, 0, 0xC0FF_EE01]);
        assert_eq!(parse(&w).options2, 0xC0FF_EE01);
    }

    #[test]
    fn options2_flag_without_its_word_flags_truncation() {
        let mut w = before_trailer();
        words(&mut w, &[0x40, 0, 0, 0x1234]);
        let d = parse(&w);
        assert_eq!(d.spellbook_filters, 0x1234);
        assert!(d.trailer_truncated);
    }

    #[test]
    fn inventory_and_equipped_follow_directly_without_gameplay_options() {
        let mut w = before_trailer();
        words(&mut w, &[0, 0, 0, 0]);
        words(&mut w, &[2, 0x5000_00A0, 0, 0x5000_00A1, 1]);
        words(&mut w, &[1, 0x5000_00B0, 0x200, 1]);
        let d = parse(&w);
        assert_eq!(
            d.inventory,
            [
                InventoryEntry {
                    guid: 0x5000_00A0,
                    container_type: 0
                },
                InventoryEntry {
                    guid: 0x5000_00A1,
                    container_type: 1
                },
            ]
        );
        assert_eq!(
            d.equipped,
            [EquippedEntry {
                guid: 0x5000_00B0,
                equip_location: 0x200,
                priority: 1
            }]
        );
    }

    #[test]
    fn strict_inventory_failure_keeps_earlier_entries_without_flagging() {
        let mut w = before_trailer();
        words(&mut w, &[0, 0, 0, 0]);
        // The second entry's container type 3 is out of range.
        words(&mut w, &[2, 0x5000_00A0, 0, 0x5000_00A1, 3]);
        let d = parse(&w);
        assert_eq!(d.inventory.len(), 1);
        assert!(d.equipped.is_empty());
        assert!(!d.trailer_truncated);
    }

    #[test]
    fn gameplay_options_are_skipped_by_finding_the_inventory() {
        let mut w = before_trailer();
        words(&mut w, &[0x200, 0, 0, 0]);
        let blob_start = w.len();
        words(
            &mut w,
            &[0xDEAD_BEEF, 0xCAFE_BABE, 0x1234_5678, 0x8765_4321],
        );
        words(&mut w, &[1, 0x5000_0200, 0]);
        words(&mut w, &[1, 0x5000_0300, 0x200, 1]);
        let d = parse(&w);
        assert_eq!(d.inventory.len(), 1);
        assert_eq!(d.inventory[0].guid, 0x5000_0200);
        assert_eq!(d.equipped.len(), 1);
        assert_eq!(d.equipped[0].guid, 0x5000_0300);
        assert_eq!(
            d.gameplay_options,
            &w.as_slice()[blob_start..blob_start + 16]
        );
    }

    #[test]
    fn a_zero_word_ending_the_gameplay_options_is_not_an_empty_inventory() {
        // Shape of a live description: the gameplay options end
        // in a zero word; read from there, the inventory count is 0 and the
        // real inventory reads as equipped entries, ending at the payload end
        // too. Those entries name slot 0 and are refused; the real lists win.
        let mut w = before_trailer();
        words(&mut w, &[0x200, 0, 0, 0]);
        words(&mut w, &[0x0000_8910, 0x0000_1A10, 0]);
        // Four inventory pairs, the equipped count and one triple are 12
        // words: four triples, so a count of 4 read as the equipped count
        // also ends exactly at the end.
        words(
            &mut w,
            &[
                4,
                0x8000_B1CC,
                0,
                0x8000_B1CB,
                0,
                0x8000_C069,
                0,
                0x8000_048E,
                1,
            ],
        );
        words(&mut w, &[1, 0x8000_0577, 0x0100_0000, 0]);
        let d = parse(&w);
        assert_eq!(
            d.inventory.iter().map(|e| e.guid).collect::<Vec<_>>(),
            [0x8000_B1CC, 0x8000_B1CB, 0x8000_C069, 0x8000_048E]
        );
        assert_eq!(d.equipped.len(), 1);
        assert_eq!(d.equipped[0].guid, 0x8000_0577);
    }

    #[test]
    fn gameplay_options_without_a_matching_inventory_are_left_empty() {
        let mut w = before_trailer();
        words(&mut w, &[0x200, 0, 0, 0]);
        // Every candidate count runs past the end.
        words(&mut w, &[0xFFFF_FFFF, 0xFFFF_FFFF, 0xFFFF_FFFF]);
        let d = parse(&w);
        assert!(d.gameplay_options.is_empty());
        assert!(d.inventory.is_empty());
        assert!(!d.trailer_truncated);
    }

    fn full_trailer() -> Writer {
        let mut w = before_trailer();
        words(&mut w, &[0x449, 0xAA00_0001]);
        words(&mut w, &[1, 3, 0xCAFE_FACE, 0x0002_0064]);
        for _ in 0..8 {
            w.u32(0);
        }
        w.u16(1);
        w.u16(0);
        words(&mut w, &[0xC1, 99]);
        w.u32(0xF11D);
        w.u32(0xBB00_0002);
        words(&mut w, &[1, 0x5000_0400, 0]);
        words(&mut w, &[1, 0x5000_0500, 0x200, 1]);
        w
    }

    #[test]
    fn full_trailer_populates_every_section() {
        let d = parse(&full_trailer());
        assert_eq!(d.options1, 0xAA00_0001);
        assert_eq!(d.options2, 0xBB00_0002);
        assert_eq!(d.spellbook_filters, 0xF11D);
        assert_eq!(d.shortcuts.len(), 1);
        assert_eq!(d.shortcuts[0].object_id, 0xCAFE_FACE);
        assert_eq!(d.shortcuts[0].spell_id, 0x0002_0064);
        assert_eq!(d.hotbar_spells.len(), 8);
        assert!(d.hotbar_spells.iter().all(Vec::is_empty));
        assert_eq!(
            d.desired_comps,
            [DesiredComponent {
                id: 0xC1,
                amount: 99
            }]
        );
        assert_eq!(d.inventory.len(), 1);
        assert_eq!(d.inventory[0].guid, 0x5000_0400);
        assert_eq!(d.equipped.len(), 1);
        assert_eq!(d.equipped[0].guid, 0x5000_0500);
        assert!(!d.trailer_truncated);
    }

    #[test]
    fn trailer_counts_above_the_limit_flag_truncation() {
        let mut w = before_trailer();
        words(&mut w, &[0x01, 0, MAX_TRAILER_COUNT + 1]);
        let d = parse(&w);
        assert!(d.trailer_truncated);
        assert!(d.shortcuts.is_empty());
    }

    #[test]
    fn body_ending_after_the_tables_is_a_partial_description() {
        let mut w = Writer::new();
        words(&mut w, &[0x01, 0x52]);
        w.u16(1);
        w.u16(0);
        words(&mut w, &[25, 9]);
        w.u32(0x0001); // only 4 of the 8 vector-header bytes
        let d = parse(&w);
        assert_eq!(d.properties.ints[&25], 9);
        assert_eq!(d.vector_flags, DescriptionVectorFlags(0));
        assert!(!d.has_health);
        // A partial description does not get the default filters.
        assert_eq!(d.spellbook_filters, 0);
        assert!(!d.trailer_truncated);
    }

    #[test]
    fn string_padding_cut_at_the_end_is_tolerated() {
        let mut w = Writer::new();
        words(&mut w, &[0x10, 0x52]);
        w.u16(1);
        w.u16(0);
        w.u32(1);
        w.u16(1);
        w.u8(b'a'); // record is 3 bytes; its padding byte is missing
        let d = parse(&w);
        assert_eq!(d.properties.strings[&1], "a");
        assert_eq!(d.vector_flags, DescriptionVectorFlags(0));
    }

    #[test]
    fn truncation_before_the_vector_header_or_inside_a_block_is_an_error() {
        // Up to the end of the property tables every cut is an error, and
        // so is every cut inside the attribute block; a cut leaving fewer
        // than 8 bytes after the tables is the partial form.
        let mut w = Writer::new();
        words(&mut w, &[0x01, 0x52]);
        w.u16(1);
        w.u16(0);
        words(&mut w, &[25, 9]);
        let tables_end = w.len();
        words(&mut w, &[0x01, 1, 0x41, 100, 60, 0]);
        vital(&mut w, 25, 75, 0, 99);
        let body = w.into_vec();
        for len in 0..body.len() {
            let result = PlayerDescription::parse(&body[..len]);
            if (tables_end..tables_end + 8).contains(&len) {
                assert!(result.is_ok(), "len {len}");
            } else if len < body.len() {
                assert!(result.is_err(), "len {len}");
            }
        }
        assert!(PlayerDescription::parse(&body).is_ok());
    }

    #[test]
    fn no_truncation_of_a_full_description_panics() {
        let mut w = Writer::new();
        words(&mut w, &[0x10, 0x52]);
        w.u16(1);
        w.u16(0);
        w.u32(1);
        w.string16l("Acdream").unwrap();
        words(&mut w, &[0x303, 1, 0x41, 100, 60, 0]);
        vital(&mut w, 25, 75, 0, 99);
        w.u16(1);
        w.u16(0);
        w.u32(6);
        w.u16(50);
        w.u16(1);
        words(&mut w, &[3, 1000, 10, 0]);
        w.f64(1.0);
        w.u16(1);
        w.u16(0);
        w.u32(1234);
        w.f32(2.0);
        w.u32(0x01);
        w.u32(1);
        write_enchantment(&mut w, &MULTIPLICATIVE);
        let mut body = w.into_vec();
        body.extend_from_slice(&full_trailer().as_slice()[24..]);
        let full = PlayerDescription::parse(&body).unwrap();
        assert!(!full.trailer_truncated);
        assert_eq!(full.equipped.len(), 1);
        for len in 0..body.len() {
            // Errors or partial results are both fine; panics are not.
            let _ = PlayerDescription::parse(&body[..len]);
        }
    }
}
