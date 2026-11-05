//! The client's own built-in skill-name list.
//!
//! It is the list an appraisal's weapon-skill line and the salvage message
//! read, not the authored skill data a skill window reads, and the two
//! disagree: this list keeps the original names of three skills the
//! authored data later renamed or dropped ("Person Appraisal" and
//! "Creature Appraisal" against "Assess Person" and "Assess Creature";
//! "Armor Repair", which the authored data no longer has), and it never
//! learned Shield. Both readers leave out a skill it cannot name, so do not
//! "complete" it: a name added here would appear in text the game leaves
//! blank.

/// The built-in name for a skill, if the list has one.
#[must_use]
pub fn try_get_name(skill_id: i32) -> Option<&'static str> {
    let name = match skill_id {
        1 => "Axe",
        2 => "Bow",
        3 => "Crossbow",
        4 => "Dagger",
        5 => "Mace",
        6 => "Melee Defense",
        7 => "Missile Defense",
        8 => "Sling",
        9 => "Spear",
        10 => "Staff",
        11 => "Sword",
        12 => "Thrown Weapon",
        13 => "Unarmed Combat",
        14 => "Arcane Lore",
        15 => "Magic Defense",
        16 => "Mana Conversion",
        17 => "Spellcraft",
        18 => "Item Tinkering",
        19 => "Person Appraisal",
        20 => "Deception",
        21 => "Healing",
        22 => "Jump",
        23 => "Lockpick",
        24 => "Run",
        25 => "Awareness",
        26 => "Armor Repair",
        27 => "Creature Appraisal",
        28 => "Weapon Tinkering",
        29 => "Armor Tinkering",
        30 => "Magic Item Tinkering",
        31 => "Creature Enchantment",
        32 => "Item Enchantment",
        33 => "Life Magic",
        34 => "War Magic",
        35 => "Leadership",
        36 => "Loyalty",
        37 => "Fletching",
        38 => "Alchemy",
        39 => "Cooking",
        40 => "Salvaging",
        41 => "Two Handed Combat",
        42 => "Gearcraft",
        43 => "Void Magic",
        44 => "Heavy Weapons",
        45 => "Light Weapons",
        46 => "Finesse Weapons",
        47 => "Missile Weapons",
        // 48 (Shield) is absent on purpose: the built-in list has no entry.
        49 => "Dual Wield",
        50 => "Recklessness",
        51 => "Sneak Attack",
        52 => "Dirty Fighting",
        53 => "Challenge",
        54 => "Summoning",
        _ => return None,
    };
    Some(name)
}

/// A label for a skill: the built-in name, or "Skill N" for one nothing
/// names. The numbered form is ours, for a host running without the
/// authored skill data; the game itself shows no such label.
#[must_use]
pub fn describe(skill_id: i32) -> String {
    try_get_name(skill_id).map_or_else(|| format!("Skill {skill_id}"), str::to_owned)
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden values from OpenAC
    // `tests/AcDream.Core.Tests/Player/RetailSkillNamesTests.cs`.

    #[test]
    fn names_every_skill_it_knew_and_not_shield() {
        for skill in (1..=54).filter(|&s| s != 48) {
            let name = try_get_name(skill).expect("named");
            assert!(!name.trim().is_empty());
        }
        assert_eq!(try_get_name(48), None);
    }

    #[test]
    fn names_nothing_outside_its_range() {
        for skill in [0, -1, 55, 200] {
            assert_eq!(try_get_name(skill), None);
        }
    }

    #[test]
    fn keeps_the_original_names() {
        assert_eq!(try_get_name(19), Some("Person Appraisal"));
        assert_eq!(try_get_name(26), Some("Armor Repair"));
        assert_eq!(try_get_name(27), Some("Creature Appraisal"));
    }

    #[test]
    fn describe_numbers_a_skill_nothing_names() {
        assert_eq!(describe(47), "Missile Weapons");
        assert_eq!(describe(48), "Skill 48");
        assert_eq!(describe(200), "Skill 200");
    }
}
