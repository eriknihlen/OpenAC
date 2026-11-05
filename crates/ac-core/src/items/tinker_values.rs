//! The numbers a tinkering calculator reads off one item: workmanship,
//! tinks, imbues, armor and damage, retained. Projected from the object
//! table's record (its description and its last appraisal), so whatever
//! reads them (the plugin surface, a panel) sees one reading.

use super::ClientObject;
use crate::properties::{PropertyBool, PropertyFloat, PropertyInt};

/// The crafting values of one item. An item that carries none of them
/// reads zero and not retained.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct TinkerValues {
    /// The item's workmanship, which its salvage takes over.
    pub salvage_workmanship: f64,
    /// How many tinkers have taken.
    pub num_times_tinkered: i32,
    /// The imbue bits.
    pub imbued_effect: i32,
    /// The armor level property.
    pub armor_level: i32,
    /// The appraised weapon damage (the server's unset marker reads -1),
    /// else the damage property.
    pub damage: i32,
    /// The damage with the unset marker folded to zero, so a sum never
    /// goes negative.
    pub max_damage: i32,
    /// What the appraised weapon strikes with, else the damage-type
    /// property.
    pub damage_type: i32,
    /// A wand's element: the damage-type property, whatever the appraised
    /// weapon profile says.
    pub wand_elemental_damage_type: i32,
    /// The appraised weapon's variance, else the variance property (the
    /// roll's floor).
    pub damage_variance: f64,
    /// The retained property.
    pub retained: bool,
    /// The slot mask.
    pub valid_locations: u32,
    /// The uses left.
    pub structure: i32,
    /// The material, zero when the server sent none.
    pub material_type: u32,
}

impl TinkerValues {
    /// Read `item`'s crafting values.
    #[must_use]
    pub fn of(item: &ClientObject) -> Self {
        let p = &item.properties;
        let weapon = item.weapon_profile.as_ref();
        let damage = weapon.map_or_else(
            || p.get_int(PropertyInt::DAMAGE.0, 0),
            |w| w.normalized_damage(),
        );
        Self {
            salvage_workmanship: f64::from(item.workmanship),
            num_times_tinkered: p.get_int(PropertyInt::NUM_TIMES_TINKERED.0, 0),
            imbued_effect: p.get_int(PropertyInt::IMBUED_EFFECT.0, 0),
            armor_level: p.get_int(PropertyInt::ARMOR_LEVEL.0, 0),
            damage,
            max_damage: damage.max(0),
            damage_type: weapon.map_or_else(
                || p.get_int(PropertyInt::DAMAGE_TYPE.0, 0),
                |w| w.damage_type as i32,
            ),
            wand_elemental_damage_type: p.get_int(PropertyInt::DAMAGE_TYPE.0, 0),
            damage_variance: weapon.map_or_else(
                || p.get_float(PropertyFloat::DAMAGE_VARIANCE.0, 0.0),
                |w| w.damage_variance,
            ),
            retained: p.get_bool(PropertyBool::RETAINED.0, false),
            valid_locations: item.valid_locations.bits(),
            structure: item.structure,
            material_type: item.material_type.unwrap_or(0),
        }
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.Runtime.Tests/Plugins/RuntimeInventoryItemTinkerValuesTests.cs`
    //! (the plugin snapshot's crafting values), staged on the record the
    //! snapshot is projected from.

    use super::*;
    use crate::items::{ClientWeaponProfile, EquipMask};

    fn staged(stage: impl FnOnce(&mut ClientObject)) -> TinkerValues {
        let mut item = ClientObject::new(0x7000_5001);
        item.name = "a staged item".into();
        stage(&mut item);
        TinkerValues::of(&item)
    }

    #[test]
    fn the_snapshot_carries_every_crafting_value_the_item_holds() {
        let v = staged(|s| {
            s.workmanship = 7.25;
            s.structure = 83;
            s.max_structure = 100;
            s.valid_locations = EquipMask::NECK_WEAR;
            let p = &mut s.properties;
            p.ints.insert(PropertyInt::NUM_TIMES_TINKERED.0, 4);
            p.ints.insert(PropertyInt::IMBUED_EFFECT.0, 0x0080);
            p.ints.insert(PropertyInt::ARMOR_LEVEL.0, 312);
            p.ints.insert(PropertyInt::DAMAGE.0, 46);
            p.ints.insert(PropertyInt::DAMAGE_TYPE.0, 0x40);
            p.floats.insert(PropertyFloat::DAMAGE_VARIANCE.0, 0.42);
            p.bools.insert(PropertyBool::RETAINED.0, true);
        });
        assert!((v.salvage_workmanship - 7.25).abs() < 1e-4);
        assert_eq!(v.num_times_tinkered, 4);
        assert_eq!(v.imbued_effect, 0x0080);
        assert_eq!(v.armor_level, 312);
        assert_eq!(v.max_damage, 46);
        assert_eq!(v.wand_elemental_damage_type, 0x40);
        assert!(v.retained);
        // The slot mask, the uses left and the roll's floor.
        assert_eq!(v.valid_locations, 0x8000);
        assert_eq!(v.structure, 83);
        assert!((v.damage_variance - 0.42).abs() < 1e-4);
    }

    #[test]
    fn an_item_that_carries_none_of_them_reads_inert() {
        let v = staged(|_| {});
        assert_eq!(v.salvage_workmanship, 0.0);
        assert_eq!(v.num_times_tinkered, 0);
        assert_eq!(v.imbued_effect, 0);
        assert_eq!(v.armor_level, 0);
        assert_eq!(v.max_damage, 0);
        assert_eq!(v.wand_elemental_damage_type, 0);
        assert!(!v.retained);
    }

    /// The server's "damage was never set" answer is a marker, not a
    /// number: `damage` keeps saying unset (-1), `max_damage` folds it to
    /// zero.
    #[test]
    fn max_damage_folds_the_servers_unset_damage_to_zero() {
        let v = staged(|s| {
            s.weapon_profile = Some(ClientWeaponProfile {
                damage: u32::MAX,
                ..ClientWeaponProfile::default()
            });
        });
        assert_eq!(v.damage, -1);
        assert_eq!(v.max_damage, 0);
    }

    /// A wand's element lives in the item's own properties; the appraised
    /// weapon profile says what it strikes with in melee. Both survive.
    #[test]
    fn the_wand_element_survives_an_appraised_weapon_profile() {
        let v = staged(|s| {
            s.properties.ints.insert(PropertyInt::DAMAGE_TYPE.0, 0x40);
            s.weapon_profile = Some(ClientWeaponProfile {
                damage: 12,
                damage_type: 0x01,
                ..ClientWeaponProfile::default()
            });
        });
        assert_eq!(v.damage_type, 0x01);
        assert_eq!(v.wand_elemental_damage_type, 0x40);
        assert_eq!(v.max_damage, 12);
    }
}
