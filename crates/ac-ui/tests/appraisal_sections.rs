//! The item report's weapon, armor, requirement and item-detail sections.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/ItemAppraisalTextFormatterTests.cs`
//! (the sections `tests/appraisal.rs` does not cover), with the golden
//! strings of those tests.

use std::collections::HashMap;

use ac_core::items::{ClientObject, EquipMask, ItemType, PublicWeenieFlags};
use ac_core::spells::SpellMetadata;
use ac_proto::appraise::{
    ArmorProfile, EnchantmentHighlight, IdentifyObjectResponse, WeaponProfile,
};
use ac_ui::panels::appraisal::{
    self, AppraisalNames, AppraisalPlayerSkill, NameTables, NoNames, ReportStyle,
};

fn parsed(f: impl FnOnce(&mut IdentifyObjectResponse)) -> IdentifyObjectResponse {
    let mut a = IdentifyObjectResponse {
        guid: 0x5000_0001,
        success: true,
        ..IdentifyObjectResponse::default()
    };
    f(&mut a);
    a
}

fn object(name: &str, t: ItemType, locations: EquipMask) -> ClientObject {
    let mut o = ClientObject::new(0x5000_0001);
    o.name = name.into();
    o.item_type = t;
    o.valid_locations = locations;
    o
}

fn none(_: u32) -> Option<SpellMetadata> {
    None
}

fn report_with(
    obj: &ClientObject,
    a: &IdentifyObjectResponse,
    names: &dyn AppraisalNames,
) -> String {
    appraisal::item_report(obj, a, &none, names).to_string()
}

fn report(obj: &ClientObject, a: &IdentifyObjectResponse) -> String {
    report_with(obj, a, &NoNames)
}

/// The tests' authored skill names (`AuthoredSkills`).
fn authored_skills() -> NameTables {
    NameTables {
        skills: HashMap::from([(6, "Melee Defense".to_owned()), (48, "Shield".to_owned())]),
        ..NameTables::default()
    }
}

fn weapon(damage_type: u32, time: u32, skill: u32, damage: u32, variance: f64) -> WeaponProfile {
    WeaponProfile {
        damage_type,
        weapon_time: time,
        weapon_skill: skill,
        damage,
        damage_variance: variance,
        damage_mod: 1.0,
        weapon_length: 1.0,
        max_velocity: 0.0,
        weapon_offense: 1.0,
        max_velocity_estimated: 0,
    }
}

// TheBuiltInList_NamesEverySkillItEverKnew_AndNotShield.
#[test]
fn the_built_in_list_names_every_skill_but_shield() {
    for skill in (1..=54).filter(|s| *s != 48) {
        assert!(
            ac_core::player::skill_names::try_get_name(skill).is_some(),
            "{skill}"
        );
    }
    assert!(ac_core::player::skill_names::try_get_name(48).is_none());
}

// UseRequirement_ForShield_NamesItFromTheAuthoredData.
#[test]
fn a_use_requirement_names_shield_from_the_authored_data() {
    let obj = object("Buckler", ItemType::NONE, EquipMask::NONE);
    let a = parsed(|a| {
        a.properties.ints.insert(366, 48);
        a.properties.ints.insert(367, 200);
    });
    let text = report_with(&obj, &a, &authored_skills());
    assert!(
        text.contains("Use requires Shield of at least 200."),
        "{text}"
    );
    assert!(!text.contains("Unknown Skill"));
    // Without the authored name the line still prints.
    assert!(report(&obj, &a).contains("Use requires Unknown Skill of at least 200."));
}

struct Skilled(AppraisalPlayerSkill);

impl AppraisalNames for Skilled {
    fn player_skill(&self, skill: u32) -> AppraisalPlayerSkill {
        if skill == 48 {
            self.0
        } else {
            AppraisalPlayerSkill::default()
        }
    }
}

// ShieldAppraisal_ShowsTheEffectiveLevelFromTheViewersShieldSkill.
#[test]
fn a_shield_shows_the_level_the_viewer_can_use() {
    let obj = object("Kite Shield", ItemType::ARMOR, EquipMask::SHIELD);
    let a = parsed(|a| {
        a.properties.ints.insert(28, 200);
    });
    for (level, class, expected) in [
        (300, 2, 150),
        (301, 1, 150),
        (150, 3, 150),
        (500, 3, 200),
        (0, 0, 0),
    ] {
        let names = Skilled(AppraisalPlayerSkill {
            level,
            advancement_class: class,
        });
        let text = report_with(&obj, &a, &names);
        assert!(text.contains("Base Shield Level: 200"), "{text}");
        assert!(
            text.contains(&format!(
                "Effective Shield Level : {expected} (with Shield skill)"
            )),
            "{level} {class}: {text}"
        );
    }
}

// WeaponAndMagic_AreProjectedInRetailOrderWithDatDescriptions (the weapon
// lines and their place before the spells).
#[test]
fn the_weapon_lines_come_in_retail_order() {
    let obj = object(
        "Flaming Sword",
        ItemType::MELEE_WEAPON,
        EquipMask::MELEE_WEAPON,
    );
    let a = parsed(|a| {
        let p = &mut a.properties;
        p.ints.insert(353, 2);
        p.ints.insert(19, 12_500);
        p.ints.insert(5, 450);
        p.ints.insert(204, 4);
        p.ints.insert(105, 8);
        p.floats.insert(29, 1.05);
        a.spell_book = vec![101];
        a.weapon_profile = Some(WeaponProfile {
            weapon_offense: 1.15,
            ..weapon(1, 30, 44, 40, 0.25)
        });
    });
    let text = report(&obj, &a);
    for line in [
        "Skill: Heavy Weapons (Sword)",
        "Damage: 30 - 40, Slashing",
        "Elemental Damage Bonus: 4, Slashing.",
        "Speed: Fast (30)",
        "Bonus to Attack Skill: +15%.",
        "Bonus to Melee Defense: +5%.",
    ] {
        assert!(text.contains(line), "{line}: {text}");
    }
    assert!(text.find("Skill:") < text.find("Spells:"), "{text}");
}

// Boots_PreserveRetailBlankSectionBoundaries.
#[test]
fn boots_keep_the_blank_section_boundaries() {
    let mut obj = object("Steel Toed Boots", ItemType::CLOTHING, EquipMask::FOOT_WEAR);
    obj.priority = 0x0001_0000;
    let a = parsed(|a| {
        let p = &mut a.properties;
        p.ints.insert(19, 1_250);
        p.ints.insert(5, 180);
        p.ints.insert(105, 7);
        p.ints.insert(28, 80);
        a.armor_profile = Some(ArmorProfile {
            slashing: 1.0,
            piercing: 1.0,
            bludgeoning: 1.0,
            cold: 1.0,
            fire: 1.0,
            acid: 1.0,
            nether: 1.0,
            lightning: 1.0,
        });
    });
    let text = report(&obj, &a);
    assert!(
        text.contains("Workmanship: Flawless (7)\n\nCovers Feet\n\nArmor Level: 80"),
        "{text}"
    );
}

// WeaponDamage_UsesRetailEnchantmentFontSelection.
#[test]
fn the_damage_line_takes_its_enchantment_colour() {
    let obj = object("Sword", ItemType::MELEE_WEAPON, EquipMask::MELEE_WEAPON);
    for (high, expected) in [
        (0x0000, ReportStyle::Detrimental),
        (0x0008, ReportStyle::Beneficial),
    ] {
        let a = parsed(|a| {
            a.weapon_profile = Some(weapon(1, 30, 44, 40, 0.0));
            a.weapon_enchantments = Some(EnchantmentHighlight {
                highlight: 0x0008,
                color: high,
            });
        });
        let r = appraisal::item_report(&obj, &a, &none, &NoNames);
        let damage: Vec<_> = r
            .fragments
            .iter()
            .filter(|f| f.text.starts_with("Damage:"))
            .collect();
        assert_eq!(damage.len(), 1);
        assert_eq!(damage[0].style, expected);
    }
}

// ArmorRequirementsAndUses_UseRetailLabelsAndResistanceBands.
#[test]
fn armor_requirements_and_uses_use_retail_labels() {
    let obj = object("Platemail Hauberk", ItemType::ARMOR, EquipMask::CHEST_ARMOR);
    let a = parsed(|a| {
        let p = &mut a.properties;
        p.ints.insert(28, 200);
        p.ints.insert(158, 1);
        p.ints.insert(159, 6);
        p.ints.insert(160, 250);
        p.ints.insert(324, 4);
        p.ints.insert(369, 50);
        p.ints.insert(370, 3);
        p.ints.insert(92, 8);
        p.bools.insert(69, false);
        p.strings.insert(25, "Tester".into());
        a.armor_profile = Some(ArmorProfile {
            slashing: 1.2,
            piercing: 0.8,
            bludgeoning: 0.4,
            cold: 0.0,
            fire: 1.6,
            acid: 2.0,
            nether: 1.0,
            lightning: 0.2,
        });
    });
    let text = report_with(&obj, &a, &authored_skills());
    for line in [
        "Ratings: Dam 3",
        "Armor Level: 200",
        "Slashing: Above Average  (240)",
        "Piercing: Average  (160)",
        "Bludgeoning: Below Average  (80)",
        "Cold: None  (0)",
        "Fire: Excellent  (320)",
        "Acid: Unparalleled  (400)",
        "Electric: Poor  (40)",
        "Wield requires Viamontian",
        "Wield requires Melee Defense 250",
        "Use requires level 50.",
        "Number of uses remaining: 8",
        "Created by Tester.",
        "This item cannot be sold.",
    ] {
        assert!(text.contains(line), "{line}: {text}");
    }
}

// Launcher_UsesDamageBonusModifierAndAmmunitionWording.
#[test]
fn a_launcher_shows_its_bonus_modifier_range_and_ammunition() {
    let mut obj = object(
        "Longbow",
        ItemType::MISSILE_WEAPON,
        EquipMask::MISSILE_WEAPON,
    );
    obj.ammo_type = Some(1);
    let a = parsed(|a| {
        a.weapon_profile = Some(WeaponProfile {
            damage_mod: 1.13,
            max_velocity: 50.0,
            max_velocity_estimated: 1,
            ..weapon(2, 45, 47, 12, 0.0)
        });
    });
    let text = report(&obj, &a);
    for line in [
        "Skill: Missile Weapons",
        "Damage Bonus: 12",
        "Damage Modifier: +13%.",
        "Speed: Average (45)",
        "Range: 85 yds. (based on STRENGTH 100)",
        "Uses arrows as ammunition.",
    ] {
        assert!(text.contains(line), "{line}: {text}");
    }
    assert!(!text.contains("Damage Bonus: 12, Piercing"));
}

// OpenAC ThrownWeapon_ShowsRangeAfterSpeed: a thrown weapon
// fills the missile slot but carries no ammunition type, so it keeps plain
// "Damage" wording and still gets the range line right after its speed. The
// rock's 45 m/s is far past the 85-yard cap; the server's default 1 m/s
// rounds up to a single yard.
#[test]
fn a_thrown_weapon_shows_its_range_after_its_speed() {
    for (name, damage, variance, damage_type, time, speed, velocity, yards) in [
        ("Rock", 80, 0.5, 4, 20, "Fast", 45.0, 85),
        ("Greater Deadly Spike", 20, 0.25, 2, 10, "Very Fast", 1.0, 1),
    ] {
        let obj = object(name, ItemType::MISSILE_WEAPON, EquipMask::MISSILE_WEAPON);
        let a = parsed(|a| {
            a.properties.ints.insert(353, 10);
            a.weapon_profile = Some(WeaponProfile {
                max_velocity: velocity,
                ..weapon(damage_type, time, 47, damage, variance)
            });
        });
        let text = report(&obj, &a);
        assert!(text.contains("Skill: Missile Weapons (Thrown)"), "{text}");
        assert!(!text.contains("Damage Bonus"), "{text}");
        let pair = format!("Speed: {speed} ({time})\nRange: {yards} yds.");
        assert!(text.contains(&pair), "{pair}: {text}");
    }
}

// OpenAC MissileRange_UsesSpeedSquaredOverGravityInYards:
// below ten yards the range rounds up, from ten on it rounds down to a
// multiple of five.
#[test]
fn missile_range_is_speed_squared_over_gravity_in_yards() {
    for (velocity, yards) in [(5.0, 3), (10.0, 10), (15.0, 25), (27.0, 80)] {
        let obj = object("Dart", ItemType::MISSILE_WEAPON, EquipMask::MISSILE_WEAPON);
        let a = parsed(|a| {
            a.weapon_profile = Some(WeaponProfile {
                max_velocity: velocity,
                ..weapon(2, 10, 47, 10, 0.0)
            });
        });
        let text = report(&obj, &a);
        assert!(
            text.contains(&format!("Range: {yards} yds.")),
            "{velocity}: {text}"
        );
    }
}

// SpecializedItemBranches_FollowRetailNamesWordingAndOrder (the sections
// this file ports).
#[test]
fn set_ratings_coverage_limits_item_level_and_activation() {
    let mut obj = object("Ruby Coat", ItemType::CLOTHING, EquipMask::CHEST_WEAR);
    obj.priority = 0x0008 | 0x0010 | 0x0020 | 0x0040;
    let a = parsed(|a| {
        let p = &mut a.properties;
        p.ints.insert(265, 13);
        p.ints.insert(370, 3);
        p.ints.insert(371, -4);
        p.ints.insert(379, 2);
        p.ints.insert(86, 50);
        p.ints.insert(87, 50);
        p.int64s.insert(4, 300);
        p.int64s.insert(5, 100);
        p.ints.insert(319, 10);
        p.ints.insert(320, 3);
        p.ints.insert(109, 250);
        p.ints.insert(188, 4);
        p.ints.insert(17, 42);
    });
    let text = report(&obj, &a);
    for line in [
        "Set: Soldier's",
        "Ratings: Dam 3",
        "This item adds 2 Vitality.",
        "Covers Chest, Abdomen, Upper Arms, Lower Arms",
        "Restricted to characters of Level 50.",
        "Item Level: 3 / 10",
        "Item XP: 300 / 600",
        "Activation requires Arcane Lore: 250, Viamontian",
    ] {
        assert!(text.contains(line), "{line}: {text}");
    }
    assert!(!text.contains("Dam Resist"));
    assert!(text.find("Set: Soldier's") < text.find("Covers Chest"));
}

// SlayerAndWieldCreatureRequirements_UseRetailEnumNames.
#[test]
fn a_wield_creature_requirement_names_the_creature() {
    let obj = object("Drudge Slayer", ItemType::MELEE_WEAPON, EquipMask::NONE);
    let a = parsed(|a| {
        let p = &mut a.properties;
        p.ints.insert(166, 10);
        p.ints.insert(158, 11);
        p.ints.insert(159, 0);
        p.ints.insert(160, 10);
    });
    let names = NameTables {
        creatures: HashMap::from([(10, "Drudge".to_owned())]),
        ..NameTables::default()
    };
    let text = report_with(&obj, &a, &names);
    assert!(text.contains("Properties: Drudge slayer"), "{text}");
    assert!(text.contains("Wield requires Drudge type"), "{text}");
}

// HealerProfile_UsesHealingRowsInsteadOfConsumableBoostProse.
#[test]
fn a_healer_shows_healing_rows_not_the_boost() {
    let mut obj = object("Healing Kit", ItemType::MISC, EquipMask::NONE);
    obj.public_weenie_bitfield = Some(PublicWeenieFlags::HEALER);
    let a = parsed(|a| {
        let p = &mut a.properties;
        p.ints.insert(89, 2);
        p.ints.insert(90, 12);
        p.floats.insert(100, 1.25);
    });
    let text = report(&obj, &a);
    assert!(text.contains("Bonus to Healing Skill: 12"), "{text}");
    assert!(text.contains("Restoration Bonus: 125%"), "{text}");
    assert!(!text.contains("Restores 12 Health when used."));
}

// FailedMagicAndLauncher_PreserveRetailUnknownRows.
#[test]
fn a_failed_appraisal_keeps_the_unknown_rows() {
    let mut obj = object(
        "Unknown Bow",
        ItemType::MISSILE_WEAPON,
        EquipMask::MISSILE_WEAPON,
    );
    obj.ammo_type = Some(1);
    let a = parsed(|a| {
        a.success = false;
        a.spell_book = vec![101];
        a.weapon_profile = Some(weapon(2, u32::MAX, 47, u32::MAX, 0.0));
    });
    let text = report(&obj, &a);
    assert_eq!(text.matches("Spells: unknown.").count(), 2, "{text}");
    for line in [
        "Damage Bonus: Unknown",
        "Damage Modifier: Unknown",
        "Speed:  Unknown",
        "Range:  Unknown",
    ] {
        assert!(text.contains(line), "{line}: {text}");
    }
}

// OrdinaryStaminaConsumable_UsesRetailConsumedWording.
#[test]
fn a_stamina_consumable_is_consumed() {
    let obj = object("Stamina Food", ItemType::FOOD, EquipMask::NONE);
    let a = parsed(|a| {
        a.properties.ints.insert(89, 4);
        a.properties.ints.insert(90, -20);
    });
    assert!(report(&obj, &a).contains("Depletes 20 Stamina when consumed."));
}

// ItemExperienceStyles_UseRetailCumulativeCurves.
#[test]
fn item_experience_follows_the_cumulative_curves() {
    let obj = object("Leveling Item", ItemType::MISC, EquipMask::NONE);
    for (style, experience, level, xp) in [
        (1, 250, "Item Level: 3 / 10", "Item XP: 250 / 300"),
        (2, 300, "Item Level: 3 / 10", "Item XP: 300 / 700"),
    ] {
        let a = parsed(|a| {
            let p = &mut a.properties;
            p.int64s.insert(4, experience);
            p.int64s.insert(5, 100);
            p.ints.insert(319, 10);
            p.ints.insert(320, style);
        });
        let text = report(&obj, &a);
        assert!(text.contains(level) && text.contains(xp), "{style}: {text}");
    }
}

// WeaponDamageLine_NamesEverySetDamageType.
#[test]
fn the_damage_line_names_every_set_damage_type() {
    let obj = object(
        "Spiked Sword",
        ItemType::MELEE_WEAPON,
        EquipMask::MELEE_WEAPON,
    );
    for (mask, expected) in [
        (0x0003, "Damage: 30 - 40, Slashing/Piercing"),
        (0x0006, "Damage: 30 - 40, Piercing/Bludgeoning"),
        (0x0040, "Damage: 30 - 40, Electrical"),
        (0x0400, "Damage: 30 - 40, Nether"),
        (0x1000_0000, "Damage: 30 - 40, Prismatic"),
        (0x1000_0001, "Damage: 30 - 40, Slashing/Prismatic"),
        (0x0000, "Damage: 30 - 40, unknown type"),
    ] {
        let a = parsed(|a| {
            a.properties.ints.insert(353, 2);
            a.weapon_profile = Some(weapon(mask, 30, 44, 40, 0.25));
        });
        let text = report(&obj, &a);
        assert!(text.contains(expected), "{expected}: {text}");
    }
}

// ElementalDamageBonus_NamesTheCombinedTypeTheSameWay.
#[test]
fn the_elemental_bonus_names_the_combined_type() {
    let obj = object(
        "Crackling Sword",
        ItemType::MELEE_WEAPON,
        EquipMask::MELEE_WEAPON,
    );
    let a = parsed(|a| {
        a.properties.ints.insert(353, 2);
        a.properties.ints.insert(204, 4);
        a.weapon_profile = Some(weapon(0x0041, 30, 44, 40, 0.25));
    });
    let text = report(&obj, &a);
    assert!(
        text.contains("Damage: 30 - 40, Slashing/Electrical"),
        "{text}"
    );
    assert!(text.contains("Elemental Damage Bonus: 4, Slashing/Electrical."));
}

/// The number forms: the general form's significant digits, the signed
/// whole percent (a custom format reads 15 digits, then rounds half away
/// from zero).
#[test]
fn the_number_forms_follow_the_retail_formats() {
    use appraisal::{format_general, format_signed_percent};
    assert_eq!(format_general(30.0, 4), "30");
    assert_eq!(format_general(7.5, 3), "7.5");
    assert_eq!(format_general(12.346, 4), "12.35");
    assert_eq!(format_general(9.999, 3), "10");
    assert_eq!(format_general(0.000_012_3, 3), "1.23E-05");
    assert_eq!(format_general(12_345.0, 4), "1.235E+04");
    assert_eq!(format_signed_percent(0.13), "+13%");
    assert_eq!(format_signed_percent(-0.05), "-5%");
    assert_eq!(format_signed_percent(-0.004), "0%");
    assert_eq!(format_signed_percent(0.285), "+29%");
    assert_eq!(
        appraisal::equipment_set_name(94).as_deref(),
        Some("Minor Stinging Shrouded Soul")
    );
    assert_eq!(appraisal::equipment_set_name(34), None);
    assert_eq!(appraisal::weapon_time_name(10), "Very Fast");
    assert_eq!(appraisal::weapon_time_name(80), "Very Slow");
}

mod real_dats {
    use super::*;

    // EndOfRetailDatNameResolver_LoadsMaterialAndCreatureNames, plus the
    // skill table's names.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_name_tables_load_materials_creatures_and_skills() {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(std::path::PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        let dats = ac_dat::DatCollection::open(&dir).expect("the dats open");
        let names = NameTables::load(&dats);
        assert_eq!(names.material(0x26), "Ruby");
        assert_eq!(names.creature(77), "Ghost");
        assert_eq!(names.skill(48).as_deref(), Some("Shield"));
        assert_eq!(names.skill(6).as_deref(), Some("Melee Defense"));
    }
}
