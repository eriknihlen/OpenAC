//! One object as the client knows it: its description, where it sits, and
//! the properties the server has told us about.

use std::borrow::Cow;
use std::collections::BTreeMap;

use super::{EquipMask, HouseRestrictionRecord, ItemType, ItemUseable, PublicWeenieFlags};
use crate::names::ObjectNaming;

/// Which retained record a guid currently names. A guid can be reused for
/// a new object (a new incarnation, or a record replaced outright); each
/// record the object table installs gets a fresh identity, so a request made
/// against one record is never completed by an answer about its successor.
/// Zero means the record has not been installed in a table.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Default)]
pub struct ObjectIdentity(pub u64);

/// An object's typed property tables, keyed by raw property id (named ids
/// are in [`crate::properties`]).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct PropertyBundle {
    /// 32-bit integer properties.
    pub ints: BTreeMap<u32, i32>,
    /// 64-bit integer properties.
    pub int64s: BTreeMap<u32, i64>,
    /// True/false properties.
    pub bools: BTreeMap<u32, bool>,
    /// Decimal properties.
    pub floats: BTreeMap<u32, f64>,
    /// Text properties.
    pub strings: BTreeMap<u32, String>,
    /// Dat-id properties.
    pub data_ids: BTreeMap<u32, u32>,
    /// Object-id properties.
    pub instance_ids: BTreeMap<u32, u32>,
}

impl PropertyBundle {
    /// The int at `key`, or `default`.
    #[must_use]
    pub fn get_int(&self, key: u32, default: i32) -> i32 {
        self.ints.get(&key).copied().unwrap_or(default)
    }

    /// The 64-bit int at `key`, or `default`.
    #[must_use]
    pub fn get_int64(&self, key: u32, default: i64) -> i64 {
        self.int64s.get(&key).copied().unwrap_or(default)
    }

    /// The bool at `key`, or `default`.
    #[must_use]
    pub fn get_bool(&self, key: u32, default: bool) -> bool {
        self.bools.get(&key).copied().unwrap_or(default)
    }

    /// The float at `key`, or `default`.
    #[must_use]
    pub fn get_float(&self, key: u32, default: f64) -> f64 {
        self.floats.get(&key).copied().unwrap_or(default)
    }

    /// The string at `key`, or `default`.
    #[must_use]
    pub fn get_string<'a>(&'a self, key: u32, default: &'a str) -> &'a str {
        self.strings.get(&key).map_or(default, String::as_str)
    }

    /// Copies every entry of `incoming` over this bundle; keys it does not
    /// carry keep their values.
    pub fn merge(&mut self, incoming: &PropertyBundle) {
        self.ints.extend(&incoming.ints);
        self.int64s.extend(&incoming.int64s);
        self.bools.extend(&incoming.bools);
        self.floats.extend(&incoming.floats);
        self.strings.extend(
            incoming
                .strings
                .iter()
                .map(|(key, value)| (*key, value.clone())),
        );
        self.data_ids.extend(&incoming.data_ids);
        self.instance_ids.extend(&incoming.instance_ids);
    }
}

/// A weapon's numbers from its last successful appraisal, kept as sent.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ClientWeaponProfile {
    pub damage_type: u32,
    pub weapon_time: u32,
    pub weapon_skill: u32,
    /// `u32::MAX` means unknown; see [`normalized_damage`](Self::normalized_damage).
    pub damage: u32,
    pub damage_variance: f64,
    pub damage_mod: f64,
    pub weapon_length: f64,
    pub max_velocity: f64,
    pub weapon_offense: f64,
    pub max_velocity_estimated: u32,
}

impl ClientWeaponProfile {
    /// The damage as a signed number, with the unknown marker
    /// (`u32::MAX`) as -1.
    #[must_use]
    pub fn normalized_damage(&self) -> i32 {
        normalize_damage(self.damage)
    }
}

/// The damage field's unknown marker (`u32::MAX`) becomes -1; any other
/// value keeps its bits as a signed number.
#[must_use]
pub fn normalize_damage(damage: u32) -> i32 {
    if damage == u32::MAX {
        -1
    } else {
        damage as i32
    }
}

/// An armor piece's protection against each damage kind from its last
/// successful appraisal, kept as sent.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ClientArmorProfile {
    pub slashing_protection: f32,
    pub piercing_protection: f32,
    pub bludgeoning_protection: f32,
    pub cold_protection: f32,
    pub fire_protection: f32,
    pub acid_protection: f32,
    pub nether_protection: f32,
    pub lightning_protection: f32,
}

/// One object: everything the server described about it, where it is
/// (container, slot, wielder, worn location) and its property tables.
///
/// Placement fields (`container_id`, `container_slot`, `wielder_id`,
/// `currently_equipped_location`) are read freely, but change them through
/// the object table so its container and equipment lists stay in step.
#[derive(Debug, Clone, PartialEq)]
pub struct ClientObject {
    object_id: u32,
    identity: ObjectIdentity,
    pub weenie_class_id: u32,
    pub name: String,
    pub plural_name: String,
    pub item_type: ItemType,
    pub valid_locations: EquipMask,
    pub currently_equipped_location: EquipMask,
    /// The icon (a `0x06xx_xxxx` dat id).
    pub icon_id: u32,
    pub icon_underlay_id: u32,
    pub icon_overlay_id: u32,
    /// The icon effect bits (glow, enchanted, ...).
    pub effects: u32,
    pub stack_size: i32,
    pub stack_size_max: i32,
    /// The whole stack's burden.
    pub burden: i32,
    /// The whole stack's value in pyreals.
    pub value: i32,
    pub container_id: u32,
    /// The slot in the container's list; -1 when unknown.
    pub container_slot: i32,
    /// The container-type field from a contents list: 0 an item, 1 a pack,
    /// 2 a spell focus.
    pub container_type_hint: u32,
    pub attuned: bool,
    pub bonded: bool,
    /// Who wields it; 0 when not wielded.
    pub wielder_id: u32,
    pub items_capacity: i32,
    pub containers_capacity: i32,
    pub hook_item_types: u32,
    pub hook_type: u32,
    /// Layering priority of a worn item (higher is worn over lower).
    pub priority: u32,
    pub useability: Option<ItemUseable>,
    /// The kinds of object this item can be used on.
    pub target_type: Option<ItemType>,
    pub public_weenie_bitfield: Option<PublicWeenieFlags>,
    pub pet_owner_id: u32,
    pub combat_use: Option<u8>,
    pub ammo_type: Option<u16>,
    pub spell_id: Option<u32>,
    /// The spells the last successful appraisal listed.
    pub appraised_spell_ids: Vec<u32>,
    /// When the last successful appraisal arrived, in milliseconds.
    pub last_appraisal_time_ms: i32,
    /// The server has answered an appraisal of this object, even
    /// unsuccessfully.
    pub appraisal_answered: bool,
    /// The latest thing the server said about this object was an appraisal
    /// answer saying it could not appraise it. A successful answer clears
    /// it, and so does anything else the server says about the object (a
    /// fresh description, a property or stack-size update, a confirmed move
    /// or wield, a pack listing or inventory list that names it). The
    /// server also refuses an object that resists appraisal and a repeat
    /// request made too soon, so it is not proof on its own that the object
    /// is gone.
    pub last_appraisal_unsuccessful: bool,
    /// When the latest unsuccessful appraisal answer arrived, in seconds of
    /// session time; meaningful only while
    /// [`Self::last_appraisal_unsuccessful`] is true.
    pub last_appraisal_unsuccessful_at_seconds: f64,
    /// Replaced by each successful appraisal; untouched by other updates.
    pub weapon_profile: Option<ClientWeaponProfile>,
    /// Replaced by each successful appraisal; untouched by other updates.
    pub armor_profile: Option<ClientArmorProfile>,
    pub cooldown_id: Option<u32>,
    pub cooldown_duration: Option<f64>,
    /// 1 while the item is in a secure-trade window.
    pub trade_state: i32,
    /// Non-zero while the item sits in a vendor sell list.
    pub sell_state: i32,
    pub is_component_pack: bool,
    pub radar_blip_color: Option<u8>,
    pub radar_behavior: Option<u8>,
    pub structure: i32,
    pub max_structure: i32,
    /// 0..10, fractional.
    pub workmanship: f32,
    pub material_type: Option<u32>,
    pub(crate) house_owner_id: Option<u32>,
    pub(crate) monarch_id: Option<u32>,
    pub(crate) restrictions: Option<HouseRestrictionRecord>,
    pub properties: PropertyBundle,
    /// How the latest full description was laid out, and its optional
    /// values as sent; none before one arrived.
    pub header: Option<ClientObjectHeader>,
}

/// How the server laid out an object's latest full description, and the
/// optional values it carried, each exactly as sent (`None` when not sent).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ClientObjectHeader {
    /// Which optional item values follow.
    pub weenie_header_flags: Option<u32>,
    /// The second such word, when sent.
    pub weenie_header_flags2: Option<u32>,
    /// Which optional physics values follow.
    pub physics_description_flags: Option<u32>,
    /// The physics state word.
    pub physics_state: Option<u32>,
    /// The object's description bit field.
    pub object_description_flags: Option<u32>,
    /// The physics setup, when sent.
    pub setup_id: Option<u32>,
    /// The object's scale, when sent.
    pub scale: Option<f32>,
    /// What kind of hook it hangs on, when sent.
    pub hook_type: Option<u32>,
    /// What it is attached to, when sent.
    pub parent_id: Option<u32>,
    /// Where on that parent, when sent.
    pub parent_location: Option<u32>,
    /// How close a user has to be, metres, when sent.
    pub use_radius: Option<f32>,
}

impl ClientObject {
    /// An object with this id and every other field at its default.
    #[must_use]
    pub fn new(object_id: u32) -> Self {
        Self {
            object_id,
            identity: ObjectIdentity::default(),
            weenie_class_id: 0,
            name: String::new(),
            plural_name: String::new(),
            item_type: ItemType::NONE,
            valid_locations: EquipMask::NONE,
            currently_equipped_location: EquipMask::NONE,
            icon_id: 0,
            icon_underlay_id: 0,
            icon_overlay_id: 0,
            effects: 0,
            stack_size: 1,
            stack_size_max: 1,
            burden: 0,
            value: 0,
            container_id: 0,
            container_slot: -1,
            container_type_hint: 0,
            attuned: false,
            bonded: false,
            wielder_id: 0,
            items_capacity: 0,
            containers_capacity: 0,
            hook_item_types: 0,
            hook_type: 0,
            priority: 0,
            useability: None,
            target_type: None,
            public_weenie_bitfield: None,
            pet_owner_id: 0,
            combat_use: None,
            ammo_type: None,
            spell_id: None,
            appraised_spell_ids: Vec::new(),
            last_appraisal_time_ms: 0,
            appraisal_answered: false,
            last_appraisal_unsuccessful: false,
            last_appraisal_unsuccessful_at_seconds: 0.0,
            weapon_profile: None,
            armor_profile: None,
            cooldown_id: None,
            cooldown_duration: None,
            trade_state: 0,
            sell_state: 0,
            is_component_pack: false,
            radar_blip_color: None,
            radar_behavior: None,
            structure: 0,
            max_structure: 0,
            workmanship: 0.0,
            material_type: None,
            house_owner_id: None,
            monarch_id: None,
            restrictions: None,
            properties: PropertyBundle::default(),
            header: None,
        }
    }

    /// The server's guid for the object.
    #[must_use]
    pub fn object_id(&self) -> u32 {
        self.object_id
    }

    /// Which retained record this is (see [`ObjectIdentity`]).
    #[must_use]
    pub fn identity(&self) -> ObjectIdentity {
        self.identity
    }

    pub(crate) fn set_identity(&mut self, identity: ObjectIdentity) {
        self.identity = identity;
    }

    /// The house this object belongs to's owner, if it is a house object.
    #[must_use]
    pub fn house_owner_id(&self) -> Option<u32> {
        self.house_owner_id
    }

    /// The object's monarch, when the server sent one.
    #[must_use]
    pub fn monarch_id(&self) -> Option<u32> {
        self.monarch_id
    }

    /// Who may enter, when this is a house object with restrictions.
    #[must_use]
    pub fn restrictions(&self) -> Option<&HouseRestrictionRecord> {
        self.restrictions.as_ref()
    }

    /// A hook: something that holds items of the hook item types.
    #[must_use]
    pub fn is_hook(&self) -> bool {
        self.hook_type != 0 && self.hook_item_types != 0
    }

    /// The public description bits, or none when the server sent none.
    #[must_use]
    pub fn flags(&self) -> PublicWeenieFlags {
        self.public_weenie_bitfield.unwrap_or_default()
    }

    fn naming(&self) -> ObjectNaming<'_> {
        ObjectNaming {
            name: &self.name,
            plural_name: &self.plural_name,
            stack_size: self.stack_size,
        }
    }

    /// The singular name for one item, the plural for a stack.
    #[must_use]
    pub fn appropriate_name(&self) -> Cow<'_, str> {
        self.naming().appropriate_name()
    }

    /// The hover caption: the name with the stack count in front when the
    /// stack holds more than one. `composed_name` replaces the plain name
    /// when the caller has built a decorated one.
    #[must_use]
    pub fn tooltip_display_name<'a>(&'a self, composed_name: Option<&'a str>) -> Cow<'a, str> {
        self.naming().tooltip_display_name(composed_name)
    }
}

/// An object description as the server sends it on create or update. A
/// `None` field was not sent and leaves the retained value alone.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct WeenieData {
    pub guid: u32,
    pub name: Option<String>,
    pub item_type: Option<ItemType>,
    /// 0 when not sent.
    pub weenie_class_id: u32,
    /// 0 when not sent.
    pub icon_id: u32,
    /// 0 when not sent.
    pub icon_overlay_id: u32,
    /// 0 when not sent.
    pub icon_underlay_id: u32,
    /// Always applied: an absent value clears the effects.
    pub effects: u32,
    pub value: Option<i32>,
    pub stack_size: Option<i32>,
    pub stack_size_max: Option<i32>,
    pub burden: Option<i32>,
    pub container_id: Option<u32>,
    pub wielder_id: Option<u32>,
    pub valid_locations: Option<EquipMask>,
    pub current_wielded_location: Option<EquipMask>,
    pub priority: Option<u32>,
    pub items_capacity: Option<i32>,
    pub containers_capacity: Option<i32>,
    pub structure: Option<i32>,
    pub max_structure: Option<i32>,
    pub workmanship: Option<f32>,
    pub useability: Option<ItemUseable>,
    pub target_type: Option<ItemType>,
    pub radar_blip_color: Option<u8>,
    pub radar_behavior: Option<u8>,
    pub public_weenie_bitfield: Option<PublicWeenieFlags>,
    pub combat_use: Option<u8>,
    pub plural_name: Option<String>,
    pub pet_owner_id: Option<u32>,
    pub ammo_type: Option<u16>,
    pub spell_id: Option<u32>,
    pub cooldown_id: Option<u32>,
    pub cooldown_duration: Option<f64>,
    pub hook_item_types: Option<u32>,
    pub hook_type: Option<u32>,
    pub material_type: Option<u32>,
    pub house_owner_id: Option<u32>,
    pub monarch_id: Option<u32>,
    pub restrictions: Option<HouseRestrictionRecord>,
    /// The description's layout and optional values; kept when sent.
    pub header: Option<ClientObjectHeader>,
}

/// Player-killer status values and how they set the public description
/// bits: each status sets its own bit and clears the other two.
pub mod pk_status {
    /// Player killer.
    pub const PK: i32 = 0x04;
    /// Free player killer.
    pub const FREE: i32 = 0x20;
    /// Player killer lite.
    pub const PK_LITE: i32 = 0x40;

    /// The public description bits after the player-killer status changes
    /// to `status`. Bits other than the three status bits are untouched;
    /// any other status clears all three.
    #[must_use]
    pub fn apply(bitfield: u32, status: i32) -> u32 {
        match status {
            PK => (bitfield & 0xFDDF_FFFF) | 0x0000_0020,
            PK_LITE => (bitfield & 0xFFDF_FFDF) | 0x0200_0000,
            FREE => (bitfield & 0xFDFF_FFDF) | 0x0020_0000,
            _ => bitfield & 0xFDDF_FFDF,
        }
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/ClientObjectNameTests.cs`,
    //! `PlayerKillerStatusBitfieldTests.cs` and the field half of
    //! `ClientObjectTableTests.ClientObject_NewFields_DefaultAndSettable`.

    use super::*;

    #[test]
    fn appropriate_name_uses_wire_plural_for_stack() {
        let mut item = ClientObject::new(1);
        item.name = "Pyreal Scarab".into();
        item.plural_name = "Pyreal Scarabs".into();
        item.stack_size = 2;
        assert_eq!(item.appropriate_name(), "Pyreal Scarabs");
    }

    #[test]
    fn appropriate_name_without_wire_plural_uses_suffix_fallback() {
        for (singular, expected) in [
            ("Arrow", "Arrows"),
            ("Pyreal", "Pyreals"),
            ("Compass", "Compasses"),
        ] {
            let mut item = ClientObject::new(1);
            item.name = singular.into();
            item.stack_size = 2;
            assert_eq!(item.appropriate_name(), expected);
        }
    }

    #[test]
    fn appropriate_name_for_single_item_remains_singular() {
        let mut item = ClientObject::new(1);
        item.name = "Pyreal Scarab".into();
        item.plural_name = "Pyreal Scarabs".into();
        assert_eq!(item.appropriate_name(), "Pyreal Scarab");
    }

    #[test]
    fn new_fields_default_and_settable() {
        let mut o = ClientObject::new(1);
        assert_eq!(o.container_slot, -1);
        assert_eq!(o.stack_size, 1);
        o.wielder_id = 0x42;
        o.items_capacity = 24;
        o.containers_capacity = 7;
        o.hook_item_types = ItemType::CONTAINER.bits();
        o.hook_type = 4;
        o.container_type_hint = 1;
        o.priority = 8;
        o.structure = 5;
        o.max_structure = 10;
        o.workmanship = 7.5;
        o.weenie_class_id = 0xABCD;
        assert!(o.is_hook());
        assert_eq!(o.weenie_class_id, 0xABCD);
    }

    #[test]
    fn pk_sets_pk_bit_clears_free_and_pk_lite() {
        assert_eq!(pk_status::apply(0x220_0000, pk_status::PK), 0x20);
    }

    #[test]
    fn pk_lite_sets_pk_lite_bit_clears_pk_and_free() {
        assert_eq!(pk_status::apply(0x20_0020, pk_status::PK_LITE), 0x200_0000);
    }

    #[test]
    fn free_sets_free_bit_clears_pk_and_pk_lite() {
        assert_eq!(pk_status::apply(0x200_0020, pk_status::FREE), 0x20_0000);
    }

    #[test]
    fn other_value_clears_all_three_bits() {
        assert_eq!(pk_status::apply(0x220_0020, 0), 0);
    }

    #[test]
    fn never_touches_unrelated_bits() {
        let unrelated = 0x8 | 0x100 | 0x40_0000;
        for status in [pk_status::PK, pk_status::PK_LITE, pk_status::FREE, 0] {
            assert_eq!(pk_status::apply(unrelated, status) & unrelated, unrelated);
        }
    }

    #[test]
    fn mutual_exclusivity_transitioning_pk_to_free_to_pk_lite() {
        let mut bits = 0;
        bits = pk_status::apply(bits, pk_status::PK);
        assert_eq!(bits, 0x20);
        bits = pk_status::apply(bits, pk_status::FREE);
        assert_eq!(bits, 0x20_0000);
        bits = pk_status::apply(bits, pk_status::PK_LITE);
        assert_eq!(bits, 0x200_0000);
        bits = pk_status::apply(bits, -1);
        assert_eq!(bits, 0);
    }

    #[test]
    fn unknown_damage_normalizes_to_minus_one() {
        assert_eq!(normalize_damage(u32::MAX), -1);
        assert_eq!(normalize_damage(12), 12);
    }
}
