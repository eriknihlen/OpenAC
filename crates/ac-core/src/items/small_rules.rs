//! Small item rules: Aetheria slot unlocks, the cooldown overlay, salvage
//! suitability and the salvage result's words, and the toolbar's ammunition
//! counter.

use super::{ClientObject, EquipMask, PublicWeenieFlags};
use crate::wire_enum::wire_enum;

wire_enum! {
    flags
    /// Which of the three Aetheria paperdoll slots the character has
    /// unlocked (each by its own quest).
    AetheriaUnlockState {
        NONE = 0x0 => "None",
        BLUE = 0x1 => "Blue",
        YELLOW = 0x2 => "Yellow",
        RED = 0x4 => "Red",
        ALL = 0x7 => "All",
    }
}

/// The player int property holding the Aetheria unlock bits.
pub const AETHERIA_UNLOCK_PROPERTY: u32 = 0x142;

/// The unlocked Aetheria slots, from the player's int property; none when
/// the player or the property is missing.
#[must_use]
pub fn aetheria_unlocks(player: Option<&ClientObject>) -> AetheriaUnlockState {
    player
        .and_then(|player| player.properties.ints.get(&AETHERIA_UNLOCK_PROPERTY))
        .map_or(AetheriaUnlockState::NONE, |&bits| {
            AetheriaUnlockState(bits as u32)
        })
}

/// Which of the ten cooldown overlay frames to draw: 10 at the start of a
/// cooldown, counting down to 1 just before it ends; 0 when no cooldown is
/// running.
#[must_use]
pub fn cooldown_overlay_step(item_duration: f64, remaining: f64) -> i32 {
    // NaN counts as "not running", like a non-positive value.
    if item_duration.is_nan() || item_duration <= 0.0 || remaining.is_nan() || remaining <= 0.0 {
        return 0;
    }
    let step = ((remaining / item_duration) * 10.0 + 1.0).trunc();
    if step.is_nan() || step < 1.0 {
        return 0;
    }
    if step >= 10.0 {
        return 10;
    }
    step as i32
}

/// Whether `material` is a salvageable material type.
#[must_use]
pub fn is_valid_salvage_material(material: u32) -> bool {
    matches!(material, 1 | 2 | 4..=8 | 10..=55 | 57..=64 | 66..=71 | 73..=77)
}

/// Whether an item may go into the salvage window: a salvageable material,
/// fewer than 100 uses' structure, not retained, and, when the window
/// takes one material only, the selected one. Only the retained bit counts:
/// a wearable that wields on use, or one whose description carries the
/// second header (an icon underlay, a cooldown), salvages like any other.
#[must_use]
pub fn is_salvage_suitable(
    item: &ClientObject,
    selected_material: u32,
    allow_multiple_materials: bool,
) -> bool {
    let material = item.material_type.unwrap_or(0);
    is_valid_salvage_material(material)
        && item.structure < 100
        && !item.flags().contains(PublicWeenieFlags::RETAINED)
        && (allow_multiple_materials || selected_material == 0 || selected_material == material)
}

/// The material name the salvage message uses (the client's own list, not
/// the authored string table); "Unknown" for a type it does not have.
#[must_use]
pub fn salvage_material_name(material: u32) -> &'static str {
    const NAMES: [&str; 77] = [
        "Ceramic",
        "Porcelain",
        "Cloth",
        "Linen",
        "Satin",
        "Silk",
        "Velvet",
        "Wool",
        "Gem",
        "Agate",
        "Amber",
        "Amethyst",
        "Aquamarine",
        "Azurite",
        "Black Garnet",
        "Black Opal",
        "Bloodstone",
        "Carnelian",
        "Citrine",
        "Diamond",
        "Emerald",
        "Fire Opal",
        "Green Garnet",
        "Green Jade",
        "Hematite",
        "Imperial Topaz",
        "Jet",
        "Lapis Lazuli",
        "Lavender Jade",
        "Malachite",
        "Moonstone",
        "Onyx",
        "Opal",
        "Peridot",
        "Red Garnet",
        "Red Jade",
        "Rose Quartz",
        "Ruby",
        "Sapphire",
        "Smokey Quartz",
        "Sunstone",
        "Tiger Eye",
        "Tourmaline",
        "Turquoise",
        "White Jade",
        "White Quartz",
        "White Sapphire",
        "Yellow Garnet",
        "Yellow Topaz",
        "Zircon",
        "Ivory",
        "Leather",
        "Armoredillo Hide",
        "Gromnie Hide",
        "Reed Shark Hide",
        "Metal",
        "Brass",
        "Bronze",
        "Copper",
        "Gold",
        "Iron",
        "Pyreal",
        "Silver",
        "Steel",
        "Stone",
        "Alabaster",
        "Granite",
        "Marble",
        "Obsidian",
        "Sandstone",
        "Serpentine",
        "Wood",
        "Ebony",
        "Mahogany",
        "Oak",
        "Pine",
        "Teak",
    ];
    material
        .checked_sub(1)
        .and_then(|index| NAMES.get(index as usize))
        .copied()
        .unwrap_or("Unknown")
}

/// One bag a salvage produced: its material, workmanship and units.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SalvageBag {
    /// The material type.
    pub material_type: u32,
    /// The bag's workmanship.
    pub workmanship: f64,
    /// How many units.
    pub units: u32,
}

/// The line a salvage that produced something prints: each bag as
/// "units material (ws n.nn)", the skill from the client's built-in list
/// (blank where it has no name), then the augmentation's bonus when there
/// was one.
#[must_use]
pub fn salvage_success_text(skill_id: u32, bags: &[SalvageBag], bonus_percent: i32) -> String {
    let materials = bags
        .iter()
        .map(|bag| {
            format!(
                "{} {} (ws {:.2})",
                bag.units,
                salvage_material_name(bag.material_type),
                bag.workmanship
            )
        })
        .collect::<Vec<_>>()
        .join(", ");
    let skill = i32::try_from(skill_id)
        .ok()
        .and_then(crate::player::skill_names::try_get_name)
        .unwrap_or("");
    let bonus = if bonus_percent == 0 {
        String::new()
    } else {
        format!(" Your augmentation has given you a return bonus of {bonus_percent}%!")
    };
    format!("You obtain {materials} using your knowledge of {skill}.{bonus}")
}

/// The line naming the items the server would not salvage.
#[must_use]
pub fn salvage_unsuitable_text(names: &[String]) -> String {
    format!(
        " The following were not suitable for salvaging: {}",
        names.join(", ")
    )
}

/// The line a salvage that produced nothing and refused nothing prints.
pub const SALVAGE_FAILED_TEXT: &str = "Salvaging Failed!";

/// What the toolbar's ammunition counter shows.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ToolbarAmmo {
    /// The ammunition object; 0 when there is none (the counter is hidden).
    pub object_id: u32,
    pub display_count: i32,
}

impl ToolbarAmmo {
    /// The counter is shown.
    #[must_use]
    pub fn is_visible(&self) -> bool {
        self.object_id != 0
    }

    /// The counter for the player's equipment, in equipment-list order: a
    /// stackable missile weapon (thrown weapons) is its own ammunition,
    /// otherwise the first item in the ammunition slot. A stack of 0 shows 1.
    #[must_use]
    pub fn resolve(equipment: &[&ClientObject]) -> Self {
        let missile_weapon = find_at_location(equipment, EquipMask::MISSILE_WEAPON);
        let ammo = match missile_weapon {
            Some(weapon) if weapon.stack_size_max > 1 => Some(weapon),
            _ => find_at_location(equipment, EquipMask::MISSILE_AMMO),
        };
        ammo.map_or_else(Self::default, |ammo| Self {
            object_id: ammo.object_id(),
            display_count: if ammo.stack_size == 0 {
                1
            } else {
                ammo.stack_size
            },
        })
    }
}

fn find_at_location<'a>(
    equipment: &[&'a ClientObject],
    location: EquipMask,
) -> Option<&'a ClientObject> {
    equipment
        .iter()
        .copied()
        .find(|item| item.currently_equipped_location.intersects(location))
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/AetheriaUnlockStateTests.cs`,
    //! `ItemCooldownDisplayTests.cs` and `ToolbarAmmoPolicyTests.cs`.

    use super::*;

    #[test]
    fn missing_property_means_all_slots_locked() {
        let player = ClientObject::new(1);
        assert_eq!(aetheria_unlocks(Some(&player)), AetheriaUnlockState::NONE);
        assert_eq!(aetheria_unlocks(None), AetheriaUnlockState::NONE);
    }

    #[test]
    fn property_322_preserves_the_three_independent_bits() {
        let mut player = ClientObject::new(1);
        player.properties.ints.insert(
            AETHERIA_UNLOCK_PROPERTY,
            (AetheriaUnlockState::BLUE | AetheriaUnlockState::RED).bits() as i32,
        );
        let actual = aetheria_unlocks(Some(&player));
        assert!(actual.contains(AetheriaUnlockState::BLUE));
        assert!(!actual.contains(AetheriaUnlockState::YELLOW));
        assert!(actual.contains(AetheriaUnlockState::RED));
    }

    #[test]
    fn overlay_step_matches_retail_truncation() {
        for (duration, remaining, expected) in [
            (10.0, 10.0, 10),
            (10.0, 9.0, 10),
            (10.0, 8.999, 9),
            (10.0, 5.0, 6),
            (10.0, 0.001, 1),
            (10.0, 0.0, 0),
            (0.0, 5.0, 0),
        ] {
            assert_eq!(
                cooldown_overlay_step(duration, remaining),
                expected,
                "{duration} {remaining}"
            );
        }
    }

    fn item(id: u32, location: EquipMask, stack: i32, max_stack: i32) -> ClientObject {
        let mut item = ClientObject::new(id);
        item.currently_equipped_location = location;
        item.stack_size = stack;
        item.stack_size_max = max_stack;
        item
    }

    #[test]
    fn stackable_missile_weapon_is_its_own_ammo() {
        let thrown = item(1, EquipMask::MISSILE_WEAPON, 7, 25);
        let separate = item(2, EquipMask::MISSILE_AMMO, 80, 100);
        assert_eq!(
            ToolbarAmmo::resolve(&[&thrown, &separate]),
            ToolbarAmmo {
                object_id: 1,
                display_count: 7
            }
        );
    }

    #[test]
    fn non_stackable_launcher_uses_separate_ammo_slot() {
        let bow = item(1, EquipMask::MISSILE_WEAPON, 1, 1);
        let arrows = item(2, EquipMask::MISSILE_AMMO, 42, 100);
        assert_eq!(
            ToolbarAmmo::resolve(&[&bow, &arrows]),
            ToolbarAmmo {
                object_id: 2,
                display_count: 42
            }
        );
    }

    #[test]
    fn missing_ammo_is_hidden_and_wire_zero_stack_displays_one() {
        assert!(!ToolbarAmmo::resolve(&[]).is_visible());
        let arrows = item(3, EquipMask::MISSILE_AMMO, 0, 100);
        assert_eq!(
            ToolbarAmmo::resolve(&[&arrows]),
            ToolbarAmmo {
                object_id: 3,
                display_count: 1
            }
        );
    }

    #[test]
    fn first_intersecting_placement_wins() {
        let first = item(1, EquipMask::MISSILE_AMMO | EquipMask::HELD, 10, 100);
        let second = item(2, EquipMask::MISSILE_AMMO, 20, 100);
        assert_eq!(
            ToolbarAmmo::resolve(&[&first, &second]),
            ToolbarAmmo {
                object_id: 1,
                display_count: 10
            }
        );
    }

    // OpenAC `tests/AcDream.Core.Net.Tests/GameEventWiringTests.cs`
    // WireAll_SalvageOperationsResult_EmitsTheSalvagingSuccessLine and
    // _NamesTheSkillFromTheSharedList.
    #[test]
    fn salvage_success_reads_bags_and_the_built_in_skill_name() {
        let silver = SalvageBag {
            material_type: 63,
            workmanship: 8.0,
            units: 1,
        };
        assert_eq!(
            salvage_success_text(28, &[silver], 0),
            "You obtain 1 Silver (ws 8.00) using your knowledge of Weapon Tinkering."
        );
        for skill in 1..=54u32 {
            let name = crate::player::skill_names::try_get_name(skill as i32).unwrap_or("");
            assert_eq!(
                salvage_success_text(skill, &[silver], 0),
                format!("You obtain 1 Silver (ws 8.00) using your knowledge of {name}.")
            );
        }
        let oak = SalvageBag {
            material_type: 75,
            workmanship: 6.4567,
            units: 12,
        };
        assert_eq!(
            salvage_success_text(18, &[silver, oak], 25),
            "You obtain 1 Silver (ws 8.00), 12 Oak (ws 6.46) using your knowledge of Item \
             Tinkering. Your augmentation has given you a return bonus of 25%!"
        );
    }

    // OpenAC GameEventWiring.SalvageMaterialName: the client's own list.
    #[test]
    fn salvage_material_names_follow_the_client_list() {
        for (material, name) in [
            (0, "Unknown"),
            (1, "Ceramic"),
            (9, "Gem"),
            (53, "Armoredillo Hide"),
            (56, "Metal"),
            (63, "Silver"),
            (77, "Teak"),
            (78, "Unknown"),
        ] {
            assert_eq!(salvage_material_name(material), name);
        }
    }

    // WireAll_SalvageOperationsResult_EmitsUnsuitableItemsInSalvagingLog,
    // _EmitsFailureInSalvagingLog.
    #[test]
    fn unsuitable_items_and_failure_lines() {
        assert_eq!(
            salvage_unsuitable_text(&["Broken Item".into()]),
            " The following were not suitable for salvaging: Broken Item"
        );
        assert_eq!(
            salvage_unsuitable_text(&["A".into(), "B".into()]),
            " The following were not suitable for salvaging: A, B"
        );
        assert_eq!(SALVAGE_FAILED_TEXT, "Salvaging Failed!");
    }

    #[test]
    fn salvage_suitability_checks_material_structure_bits_and_selection() {
        let mut item = ClientObject::new(1);
        item.material_type = Some(10);
        item.structure = 50;
        assert!(is_salvage_suitable(&item, 0, true));
        assert!(!is_salvage_suitable(&item, 11, false));
        assert!(is_salvage_suitable(&item, 10, false));
        item.structure = 100;
        assert!(!is_salvage_suitable(&item, 0, true));
        item.structure = 0;
        item.public_weenie_bitfield = Some(PublicWeenieFlags::RETAINED);
        assert!(!is_salvage_suitable(&item, 0, true));
        // Retail tests the retained bit alone (the salvage window's
        // suitability check reads the bitfield's top byte and keeps its
        // lowest bit); OpenAC's whole-top-byte mask also refused these.
        for bits in [
            PublicWeenieFlags::PLAYER_KILLER_LITE,
            PublicWeenieFlags::INCLUDES_SECOND_HEADER,
            PublicWeenieFlags::BINDSTONE,
            PublicWeenieFlags::VOLATILE_RARE,
            PublicWeenieFlags::WIELD_ON_USE,
            PublicWeenieFlags::WIELD_LEFT,
            PublicWeenieFlags::INCLUDES_SECOND_HEADER | PublicWeenieFlags::WIELD_ON_USE,
        ] {
            item.public_weenie_bitfield = Some(bits);
            assert!(is_salvage_suitable(&item, 0, true), "{bits:?}");
        }
        item.public_weenie_bitfield =
            Some(PublicWeenieFlags::RETAINED | PublicWeenieFlags::WIELD_ON_USE);
        assert!(!is_salvage_suitable(&item, 0, true));
        for material in [0, 3, 9, 56, 65, 72, 78] {
            assert!(!is_valid_salvage_material(material), "{material}");
        }
    }
}
