//! The creature and character page of the examination window: the nine
//! attribute and vital rows, the extra rows (society, allegiance, armor
//! levels, ratings, the character's own lines) and the header's identity
//! lines.

use ac_core::items::PublicWeenieFlags;
use ac_core::text::format_duration;
use ac_proto::PropertyTables;
use ac_proto::appraise::{ArmorLevels, CreatureProfile};

/// How a row's value is coloured (the value text's palette entry).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum CreatureValueStyle {
    /// The text's own colour.
    #[default]
    Normal,
    /// Raised by an enchantment (palette entry 1, green).
    Positive,
    /// Lowered by an enchantment (palette entry 2, red).
    Negative,
    /// The assessment failed (palette entry 3, yellow).
    Incomplete,
}

impl CreatureValueStyle {
    /// The value text's palette entry, `None` for its own colour.
    #[must_use]
    pub fn palette_index(self) -> Option<usize> {
        match self {
            Self::Normal => None,
            Self::Positive => Some(1),
            Self::Negative => Some(2),
            Self::Incomplete => Some(3),
        }
    }
}

/// One row: a label and a value.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreatureRow {
    /// The label ("Strength", "Monarch:", ...).
    pub label: String,
    /// The value.
    pub value: String,
    /// How the value is coloured.
    pub style: CreatureValueStyle,
}

impl CreatureRow {
    fn new(label: &str, value: impl Into<String>, style: CreatureValueStyle) -> Self {
        Self {
            label: label.to_owned(),
            value: value.into(),
            style,
        }
    }

    fn plain(label: &str, value: impl Into<String>) -> Self {
        Self::new(label, value, CreatureValueStyle::Normal)
    }

    fn blank() -> Self {
        Self::plain("", "")
    }
}

const UNKNOWN: &str = "???";

const DAMAGE_RATING: u32 = 0x133;
const DAMAGE_RESIST_RATING: u32 = 0x134;
const CRIT_RATING: u32 = 0x139;
const CRIT_DAMAGE_RATING: u32 = 0x13A;
const CRIT_RESIST_RATING: u32 = 0x13B;
const CRIT_DAMAGE_RESIST_RATING: u32 = 0x13C;
const DOT_RESIST_RATING: u32 = 0x15E;
const LIFE_RESIST_RATING: u32 = 0x15F;

const FACTION1_BITS: u32 = 281;
const SOCIETY_RANK_CELESTIAL_HAND: u32 = 287;
const SOCIETY_RANK_ELDRYTCH_WEB: u32 = 288;
const SOCIETY_RANK_RADIANT_BLOOD: u32 = 289;
const CELESTIAL_HAND_BIT: i32 = 0x1;
const ELDRYTCH_WEB_BIT: i32 = 0x2;
const RADIANT_BLOOD_BIT: i32 = 0x4;
const SOCIETY_BITS: i32 = CELESTIAL_HAND_BIT | ELDRYTCH_WEB_BIT | RADIANT_BLOOD_BIT;

/// The allegiance rank (int).
pub const ALLEGIANCE_RANK: u32 = 30;
const ALLEGIANCE_FOLLOWERS: u32 = 35;
const MONARCHS_TITLE: u32 = 21;
const PATRONS_TITLE: u32 = 35;
/// The allegiance's name (string).
pub const ALLEGIANCE_NAME: u32 = 47;

const FELLOWSHIP: u32 = 10;
const DATE_OF_BIRTH: u32 = 43;
const AGE: u32 = 125;
const CHESS_RANK: u32 = 181;
const FAKE_FISHING_SKILL: u32 = 192;
const NUM_DEATHS: u32 = 43;
const NUM_CHARACTER_TITLES: u32 = 262;

/// The level (int).
pub const LEVEL: u32 = 25;
/// The creature type (int).
pub const CREATURE_TYPE: u32 = 2;
/// The gender (int).
pub const GENDER: u32 = 0x71;
/// The heritage group (int).
pub const HERITAGE_GROUP: u32 = 0xBC;

/// An armor level at or above this is unenchantable (shown starred, less
/// this).
pub const UNENCHANTABLE_ARMOR_LEVEL: i32 = 9999;

fn int(p: &PropertyTables, id: u32) -> i32 {
    p.ints.get(&id).copied().unwrap_or(0)
}

/// The nine rows in the window's order: strength, endurance,
/// coordination, quickness, focus, self, health (with its percentage),
/// stamina, mana. An unknown value is "???"; a failed assessment shows
/// only the health percentage, every row in the incomplete colour; an
/// enchanted value is green (raised) or red (lowered).
#[must_use]
pub fn creature_rows(profile: &CreatureProfile, success: bool) -> Vec<CreatureRow> {
    let a = profile.attributes;
    let primary = |label: &str, value: Option<u32>, bit: u32| {
        let text = match value {
            Some(v) if v > 0 => v.to_string(),
            _ => UNKNOWN.to_owned(),
        };
        CreatureRow::new(label, text, style(bit, profile, success))
    };
    let secondary =
        |label: &str, current: Option<u32>, maximum: Option<u32>, show_percent: bool, bit: u32| {
            let mut text = UNKNOWN.to_owned();
            if let (Some(current), Some(maximum)) = (current, maximum)
                && current > 0
                && maximum > 0
            {
                let percent = rounded_percent(current, maximum);
                if success {
                    text = if show_percent {
                        format!("{current}/{maximum} ({percent} %)")
                    } else {
                        format!("{current}/{maximum}")
                    };
                } else if show_percent {
                    text = format!("{percent} %");
                }
            }
            CreatureRow::new(label, text, style(bit, profile, success))
        };
    vec![
        primary("Strength", a.map(|a| a.strength), 0),
        primary("Endurance", a.map(|a| a.endurance), 1),
        primary("Coordination", a.map(|a| a.coordination), 3),
        primary("Quickness", a.map(|a| a.quickness), 2),
        primary("Focus", a.map(|a| a.focus), 4),
        primary("Self", a.map(|a| a.self_), 5),
        secondary(
            "Health",
            Some(profile.health),
            Some(profile.health_max),
            true,
            6,
        ),
        secondary(
            "Stamina",
            a.map(|a| a.stamina),
            a.map(|a| a.stamina_max),
            false,
            7,
        ),
        secondary("Mana", a.map(|a| a.mana), a.map(|a| a.mana_max), false, 8),
    ]
}

fn rounded_percent(numerator: u32, denominator: u32) -> i64 {
    if denominator == 0 {
        return 0;
    }
    let (n, d) = (i64::from(numerator), i64::from(denominator));
    ((100 * n + d / 2) / d).min(i64::from(i32::MAX))
}

fn style(bit: u32, profile: &CreatureProfile, success: bool) -> CreatureValueStyle {
    if !success {
        return CreatureValueStyle::Incomplete;
    }
    let (highlight, color) = profile.attribute_highlights.unwrap_or((0, 0));
    if highlight & (1 << bit) == 0 {
        CreatureValueStyle::Normal
    } else if color & (1 << bit) != 0 {
        CreatureValueStyle::Positive
    } else {
        CreatureValueStyle::Negative
    }
}

/// The extra rows under the portrait: for a character his society and
/// allegiance, his armor levels; the ratings; for a character the lines
/// his options show (fellowship, birth, age, chess rank, fishing, deaths,
/// titles) and the "* = Unenchantable" legend last. `local_faction_bits`
/// are the viewer's society bits (his own society green, a rival red).
#[must_use]
pub fn creature_extra_rows(
    p: &PropertyTables,
    armor: Option<&ArmorLevels>,
    character: bool,
    local_faction_bits: i32,
) -> Vec<CreatureRow> {
    let mut rows = Vec::new();
    if character {
        society_row(&mut rows, p, local_faction_bits);
        if int(p, ALLEGIANCE_RANK) >= 1 {
            allegiance_cascade(&mut rows, p);
        }
    }
    if character && let Some(l) = armor.filter(|l| has_any_armor_level(l)) {
        rows.push(CreatureRow::blank());
        rows.push(armor_level_row(
            "Head/Chest/Groin",
            l.head,
            l.chest,
            l.abdomen,
        ));
        rows.push(armor_level_row(
            "Bicep/Wrist/Hand",
            l.upper_arm,
            l.lower_arm,
            l.hand,
        ));
        rows.push(armor_level_row(
            "Thigh/Shin/Foot",
            l.upper_leg,
            l.lower_leg,
            l.foot,
        ));
    }

    let damage = int(p, DAMAGE_RATING);
    let damage_resist = int(p, DAMAGE_RESIST_RATING);
    let crit = int(p, CRIT_RATING);
    let crit_damage = int(p, CRIT_DAMAGE_RATING);
    let crit_resist = int(p, CRIT_RESIST_RATING);
    let crit_damage_resist = int(p, CRIT_DAMAGE_RESIST_RATING);
    let dot_resist = int(p, DOT_RESIST_RATING);
    let life_resist = int(p, LIFE_RESIST_RATING);
    let show_rating = damage > 0 || crit > 0 || crit_damage > 0;
    let show_resist = damage_resist > 0 || crit_resist > 0 || crit_damage_resist > 0;
    let show_dot_life = dot_resist > 0 || life_resist > 0;
    if show_rating || show_resist || show_dot_life {
        rows.push(CreatureRow::blank());
        if show_rating {
            rows.push(CreatureRow::plain(
                "Dmg/CritDmg",
                format!("Rating: {damage}/{crit_damage}"),
            ));
        }
        if show_resist {
            rows.push(CreatureRow::plain(
                "Dmg/CritDmg",
                format!("Resist: {damage_resist}/{crit_damage_resist}"),
            ));
        }
        if show_dot_life {
            rows.push(CreatureRow::plain(
                "DoT/Life:",
                format!("Resist: {dot_resist}/{life_resist}"),
            ));
        }
        rows.push(CreatureRow::blank());
    }

    if character {
        configurable_extras(&mut rows, p);
        rows.push(CreatureRow::plain("* = Unenchantable", ""));
    }
    rows
}

fn society_row(rows: &mut Vec<CreatureRow>, p: &PropertyTables, local_faction_bits: i32) {
    let Some(&target) = p.ints.get(&FACTION1_BITS) else {
        return;
    };
    let (name, rank, bit) = if target & CELESTIAL_HAND_BIT != 0 {
        (
            "Celestial Hand",
            int(p, SOCIETY_RANK_CELESTIAL_HAND),
            CELESTIAL_HAND_BIT,
        )
    } else if target & ELDRYTCH_WEB_BIT != 0 {
        (
            "Eldrytch Web",
            int(p, SOCIETY_RANK_ELDRYTCH_WEB),
            ELDRYTCH_WEB_BIT,
        )
    } else if target & RADIANT_BLOOD_BIT != 0 {
        (
            "Radiant Blood",
            int(p, SOCIETY_RANK_RADIANT_BLOOD),
            RADIANT_BLOOD_BIT,
        )
    } else {
        rows.push(CreatureRow::plain("Society:", UNKNOWN));
        return;
    };
    rows.push(CreatureRow::new(
        "Society:",
        format!("{name}{}", society_rank_suffix(rank)),
        society_color(bit, local_faction_bits),
    ));
}

/// The society rank's band, as the row appends it.
#[must_use]
pub fn society_rank_suffix(rank: i32) -> &'static str {
    match rank {
        1..=100 => " ~ Initiate",
        101..=300 => " ~ Adept",
        301..=600 => " ~ Knight",
        601..=1000 => " ~ Lord",
        1001..=1500 => " ~ Master",
        _ => "",
    }
}

fn society_color(target_bit: i32, local_faction_bits: i32) -> CreatureValueStyle {
    if local_faction_bits & target_bit != 0 {
        CreatureValueStyle::Positive
    } else if local_faction_bits & (SOCIETY_BITS & !target_bit) != 0 {
        CreatureValueStyle::Negative
    } else {
        CreatureValueStyle::Normal
    }
}

fn allegiance_cascade(rows: &mut Vec<CreatureRow>, p: &PropertyTables) {
    let Some(monarch) = p.strings.get(&MONARCHS_TITLE) else {
        let followers = int(p, ALLEGIANCE_FOLLOWERS).max(0);
        let unit = if followers == 1 {
            "Follower"
        } else {
            "Followers"
        };
        rows.push(CreatureRow::plain(
            "Alleg. Monarch:",
            format!("{followers} {unit}"),
        ));
        return;
    };
    let Some(patron) = p.strings.get(&PATRONS_TITLE) else {
        rows.push(CreatureRow::plain("Monarch:", monarch.as_str()));
        return;
    };
    if monarch == patron {
        rows.push(CreatureRow::plain("Monarch/Patron:", monarch.as_str()));
        return;
    }
    rows.push(CreatureRow::plain("Monarch:", monarch.as_str()));
    rows.push(CreatureRow::plain("Patron:", patron.as_str()));
}

fn configurable_extras(rows: &mut Vec<CreatureRow>, p: &PropertyTables) {
    if let Some(fellowship) = p.strings.get(&FELLOWSHIP) {
        rows.push(CreatureRow::plain("Fellowship:", fellowship.as_str()));
    }
    if let Some(arrived) = p.strings.get(&DATE_OF_BIRTH) {
        rows.push(CreatureRow::plain("Arrived in Dereth:", arrived.as_str()));
    }
    if let Some(&age) = p.ints.get(&AGE) {
        rows.push(CreatureRow::plain(
            "Time in Dereth:",
            format_duration(f64::from(age)),
        ));
    }
    if let Some(&rank) = p.ints.get(&CHESS_RANK) {
        rows.push(CreatureRow::plain("Chess Rank:", rank.to_string()));
    }
    if let Some(&fishing) = p.ints.get(&FAKE_FISHING_SKILL) {
        rows.push(CreatureRow::plain("Fishing Skill:", fishing.to_string()));
    }
    if let Some(&deaths) = p.ints.get(&NUM_DEATHS) {
        let text = if deaths <= 0 {
            "Has never died".to_owned()
        } else {
            deaths.to_string()
        };
        rows.push(CreatureRow::plain("Deaths:", text));
    }
    if let Some(&titles) = p.ints.get(&NUM_CHARACTER_TITLES) {
        rows.push(CreatureRow::plain("Titles Earned:", titles.to_string()));
    }
}

fn has_any_armor_level(l: &ArmorLevels) -> bool {
    [
        l.head,
        l.chest,
        l.abdomen,
        l.upper_arm,
        l.lower_arm,
        l.hand,
        l.upper_leg,
        l.lower_leg,
        l.foot,
    ]
    .into_iter()
    .any(|v| v > 0)
}

fn armor_level_row(label: &str, a: i32, b: i32, c: i32) -> CreatureRow {
    CreatureRow::plain(
        label,
        format!(
            "AL: {}/{}/{}",
            armor_level_part(a),
            armor_level_part(b),
            armor_level_part(c)
        ),
    )
}

fn armor_level_part(value: i32) -> String {
    if value >= UNENCHANTABLE_ARMOR_LEVEL {
        format!("*{}", value - UNENCHANTABLE_ARMOR_LEVEL)
    } else {
        value.to_string()
    }
}

// ── The header ──────────────────────────────────────────────────────────

/// The character's gender and heritage ("Female Aluvian"); a heritage of
/// 0 names the creature type instead (`creature_fallback`).
#[must_use]
pub fn gender_heritage_display(
    gender: i32,
    heritage_group: i32,
    creature_fallback: &str,
) -> String {
    let heritage = if heritage_group == 0 {
        Some(creature_fallback)
    } else {
        crate::panels::character::heritage_name(heritage_group)
    };
    [crate::panels::character::gender_name(gender), heritage]
        .into_iter()
        .flatten()
        .map(str::trim)
        .filter(|p| !p.is_empty())
        .collect::<Vec<_>>()
        .join(" ")
}

/// The player-killer line from the object's public flags.
#[must_use]
pub fn player_killer_display(flags: PublicWeenieFlags) -> &'static str {
    if flags.contains(PublicWeenieFlags::PLAYER_KILLER) {
        "Player Killer"
    } else if flags.contains(PublicWeenieFlags::PLAYER_KILLER_LITE) {
        "Player Killer Lite"
    } else {
        "Non-Player Killer"
    }
}

/// The allegiance's name, only for a member (rank 1 or more).
#[must_use]
pub fn allegiance_display(p: &PropertyTables) -> String {
    if int(p, ALLEGIANCE_RANK) >= 1 {
        p.strings.get(&ALLEGIANCE_NAME).cloned().unwrap_or_default()
    } else {
        String::new()
    }
}

/// The level line: the number, "???" when unknown.
#[must_use]
pub fn level_display(p: &PropertyTables) -> String {
    match int(p, LEVEL) {
        l if l > 0 => l.to_string(),
        _ => UNKNOWN.to_owned(),
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC
    //! `tests/AcDream.App.Tests/UI/Layout/CreatureAppraisalRowsTests.cs` and
    //! the header cases of `AppraisalUiControllerTests.cs`.

    use super::*;
    use ac_proto::appraise::CreatureAttributes;

    fn profile(health: u32, health_max: u32, highlights: u16, colors: u16) -> CreatureProfile {
        // Flags 0x09: no attribute block (strength..self unknown); OpenAC's
        // fixture has stamina 50/100 and mana 75/100 beside it, which the
        // wire only carries with the attributes, so those rows read "???"
        // here as the failed-assessment case expects anyway.
        CreatureProfile {
            flags: 0x09,
            health,
            health_max,
            attributes: None,
            attribute_highlights: Some((highlights, colors)),
        }
    }

    fn props() -> PropertyTables {
        PropertyTables::default()
    }

    fn labels(rows: &[CreatureRow]) -> Vec<&str> {
        rows.iter().map(|r| r.label.as_str()).collect()
    }

    fn find<'a>(rows: &'a [CreatureRow], label: &str) -> &'a CreatureRow {
        let mut found = rows.iter().filter(|r| r.label == label);
        let row = found.next().unwrap_or_else(|| panic!("no {label} row"));
        assert!(found.next().is_none(), "one {label} row");
        row
    }

    #[test]
    fn failed_assessment_shows_only_health_percent_and_unknown_other_values() {
        let rows = creature_rows(&profile(25, 100, 0, 0), false);
        assert!(
            rows.iter()
                .all(|r| r.style == CreatureValueStyle::Incomplete)
        );
        assert_eq!(rows[6].value, "25 %");
        assert_eq!(rows[0].value, "???");
        assert_eq!(rows[7].value, "???");
        assert_eq!(rows[8].value, "???");
    }

    #[test]
    fn enchantment_bits_select_retail_positive_and_negative_styles() {
        let rows = creature_rows(&profile(100, 100, (1 << 0) | (1 << 6), 1 << 0), true);
        assert_eq!(rows[0].style, CreatureValueStyle::Positive);
        assert_eq!(rows[6].style, CreatureValueStyle::Negative);
        assert_eq!(rows[1].style, CreatureValueStyle::Normal);
    }

    #[test]
    fn the_nine_rows_in_retail_order_with_values() {
        // AppraisalUiControllerTests.CreatureResponse_UsesRetailHeaderAndNineOrderedTemplateRows.
        let profile = CreatureProfile {
            flags: 0x08,
            health: 295,
            health_max: 295,
            attributes: Some(CreatureAttributes {
                strength: 120,
                endurance: 190,
                quickness: 190,
                coordination: 190,
                focus: 330,
                self_: 350,
                stamina: 190,
                mana: 550,
                stamina_max: 190,
                mana_max: 550,
            }),
            attribute_highlights: Some((0, 0)),
        };
        let rows = creature_rows(&profile, true);
        assert_eq!(
            labels(&rows),
            [
                "Strength",
                "Endurance",
                "Coordination",
                "Quickness",
                "Focus",
                "Self",
                "Health",
                "Stamina",
                "Mana"
            ]
        );
        assert_eq!(
            rows.iter().map(|r| r.value.as_str()).collect::<Vec<_>>(),
            [
                "120",
                "190",
                "190",
                "190",
                "330",
                "350",
                "295/295 (100 %)",
                "190/190",
                "550/550"
            ]
        );
    }

    #[test]
    fn extra_ratings_follow_retail_grouping_formatting_and_separators() {
        let mut p = props();
        for (k, v) in [
            (0x133, 35),
            (0x139, 7),
            (0x13A, 4),
            (0x134, 12),
            (0x13B, 3),
            (0x13C, 2),
            (0x15E, 9),
            (0x15F, 6),
        ] {
            p.ints.insert(k, v);
        }
        let rows = creature_extra_rows(&p, None, false, 0);
        assert_eq!(rows.len(), 5);
        assert_eq!((rows[0].label.as_str(), rows[0].value.as_str()), ("", ""));
        assert_eq!(
            (rows[1].label.as_str(), rows[1].value.as_str()),
            ("Dmg/CritDmg", "Rating: 35/4")
        );
        assert_eq!(
            (rows[2].label.as_str(), rows[2].value.as_str()),
            ("Dmg/CritDmg", "Resist: 12/2")
        );
        assert_eq!(
            (rows[3].label.as_str(), rows[3].value.as_str()),
            ("DoT/Life:", "Resist: 9/6")
        );
        assert_eq!((rows[4].label.as_str(), rows[4].value.as_str()), ("", ""));
    }

    #[test]
    fn crit_only_triggers_the_rating_row_while_healing_boost_alone_does_not() {
        let mut crit = props();
        crit.ints.insert(0x139, 8);
        let rows = creature_extra_rows(&crit, None, false, 0);
        assert_eq!(rows.len(), 3);
        assert_eq!(rows[1].value, "Rating: 0/0");
        let mut healing = props();
        healing.ints.insert(0x143, 20);
        assert!(creature_extra_rows(&healing, None, false, 0).is_empty());
    }

    #[test]
    fn each_rating_row_gates_independently() {
        for (key, label, value) in [
            (0x139, "Dmg/CritDmg", "Rating: 0/0"),
            (0x13B, "Dmg/CritDmg", "Resist: 0/0"),
        ] {
            let mut p = props();
            p.ints.insert(key, 1);
            let rows = creature_extra_rows(&p, None, false, 0);
            assert_eq!(rows.len(), 3);
            assert_eq!(
                (rows[1].label.as_str(), rows[1].value.as_str()),
                (label, value)
            );
        }
        let mut p = props();
        p.ints.insert(0x15E, 4);
        let rows = creature_extra_rows(&p, None, false, 0);
        assert_eq!(
            (rows[1].label.as_str(), rows[1].value.as_str()),
            ("DoT/Life:", "Resist: 4/0")
        );
    }

    fn levels(values: [i32; 9]) -> ArmorLevels {
        ArmorLevels {
            head: values[0],
            chest: values[1],
            abdomen: values[2],
            upper_arm: values[3],
            lower_arm: values[4],
            hand: values[5],
            upper_leg: values[6],
            lower_leg: values[7],
            foot: values[8],
        }
    }

    #[test]
    fn armor_level_trio_uses_retail_grouping_labels_and_precedes_the_ratings() {
        let l = levels([100, 110, 120, 130, 140, 150, 160, 170, 180]);
        let mut p = props();
        p.ints.insert(0x133, 35);
        let rows = creature_extra_rows(&p, Some(&l), true, 0);
        let pairs: Vec<(&str, &str)> = rows
            .iter()
            .map(|r| (r.label.as_str(), r.value.as_str()))
            .collect();
        assert_eq!(
            pairs,
            [
                ("", ""),
                ("Head/Chest/Groin", "AL: 100/110/120"),
                ("Bicep/Wrist/Hand", "AL: 130/140/150"),
                ("Thigh/Shin/Foot", "AL: 160/170/180"),
                ("", ""),
                ("Dmg/CritDmg", "Rating: 35/0"),
                ("", ""),
                ("* = Unenchantable", ""),
            ]
        );
    }

    #[test]
    fn armor_level_parts_star_the_unenchantable_sentinel_independently() {
        for (value, expected) in [(9998, "9998"), (9999, "*0"), (10123, "*124")] {
            let rows = creature_extra_rows(
                &props(),
                Some(&levels([value, 0, 0, 0, 0, 0, 0, 0, 0])),
                true,
                0,
            );
            assert_eq!(
                find(&rows, "Head/Chest/Groin").value,
                format!("AL: {expected}/0/0")
            );
        }
        let rows = creature_extra_rows(
            &props(),
            Some(&levels([50, 9999, 20000, 0, 0, 0, 0, 0, 0])),
            true,
            0,
        );
        assert_eq!(find(&rows, "Head/Chest/Groin").value, "AL: 50/*0/*10001");
    }

    #[test]
    fn no_armor_level_above_zero_or_none_given_leaves_the_trio_out() {
        let rows = creature_extra_rows(
            &props(),
            Some(&levels([0, 0, -5, 0, 0, 0, 0, 0, 0])),
            true,
            0,
        );
        assert!(!rows.iter().any(|r| r.label.contains("Groin")));
        assert_eq!(rows.last().unwrap().label, "* = Unenchantable");
        let mut p = props();
        p.ints.insert(0x133, 35);
        let rows = creature_extra_rows(&p, None, true, 0);
        assert!(!rows.iter().any(|r| r.value.starts_with("AL:")));
    }

    #[test]
    fn the_legend_is_last_for_a_character_and_absent_for_a_monster() {
        let mut p = props();
        p.ints.insert(0x133, 35);
        let rows = creature_extra_rows(&p, None, false, 0);
        assert!(!rows.iter().any(|r| r.label == "* = Unenchantable"));
        let rows = creature_extra_rows(&props(), None, true, 0);
        assert_eq!(rows, [CreatureRow::plain("* = Unenchantable", "")]);
    }

    #[test]
    fn society_row_names_the_society_rank_band_and_colours_it_against_the_viewer() {
        assert!(
            !creature_extra_rows(&props(), None, true, 0)
                .iter()
                .any(|r| r.label == "Society:")
        );
        for (bit, rank_key, name) in [
            (0x1, 287, "Celestial Hand"),
            (0x2, 288, "Eldrytch Web"),
            (0x4, 289, "Radiant Blood"),
        ] {
            let mut p = props();
            p.ints.insert(281, bit);
            p.ints.insert(rank_key, 50);
            let rows = creature_extra_rows(&p, None, true, 0);
            assert_eq!(find(&rows, "Society:").value, format!("{name} ~ Initiate"));
        }
        for bits in [0x8, 0x10] {
            let mut p = props();
            p.ints.insert(281, bits);
            let rows = creature_extra_rows(&p, None, true, 0x1);
            let row = find(&rows, "Society:");
            assert_eq!(
                (row.value.as_str(), row.style),
                ("???", CreatureValueStyle::Normal)
            );
        }
        for (rank, suffix) in [
            (0, ""),
            (1, " ~ Initiate"),
            (100, " ~ Initiate"),
            (101, " ~ Adept"),
            (300, " ~ Adept"),
            (301, " ~ Knight"),
            (600, " ~ Knight"),
            (601, " ~ Lord"),
            (1000, " ~ Lord"),
            (1001, " ~ Master"),
            (1500, " ~ Master"),
            (1501, ""),
        ] {
            assert_eq!(society_rank_suffix(rank), suffix, "{rank}");
        }
        use CreatureValueStyle as S;
        for (target, local, expected) in [
            (0x1, 0x1, S::Positive),
            (0x1, 0x2, S::Negative),
            (0x1, 0x4, S::Negative),
            (0x1, 0x0, S::Normal),
            (0x2, 0x2, S::Positive),
            (0x2, 0x1, S::Negative),
            (0x4, 0x4, S::Positive),
            (0x4, 0x2, S::Negative),
            (0x1, 0x3, S::Positive),
        ] {
            let mut p = props();
            p.ints.insert(281, target);
            let rows = creature_extra_rows(&p, None, true, local);
            assert_eq!(find(&rows, "Society:").style, expected, "{target} {local}");
        }
    }

    #[test]
    fn the_allegiance_cascade_follows_the_titles_present() {
        let mut p = props();
        p.ints.insert(30, 0);
        p.strings.insert(21, "Should Not Appear".into());
        let rows = creature_extra_rows(&p, None, true, 0);
        assert!(!rows.iter().any(|r| r.label.contains("Monarch")));
        for (followers, expected) in [
            (0, "0 Followers"),
            (1, "1 Follower"),
            (2, "2 Followers"),
            (-5, "0 Followers"),
        ] {
            let mut p = props();
            p.ints.insert(30, 1);
            p.ints.insert(35, followers);
            let rows = creature_extra_rows(&p, None, true, 0);
            assert_eq!(find(&rows, "Alleg. Monarch:").value, expected);
        }
        let mut p = props();
        p.ints.insert(30, 1);
        p.strings.insert(21, "Baroness Aluvia".into());
        let rows = creature_extra_rows(&p, None, true, 0);
        assert_eq!(find(&rows, "Monarch:").value, "Baroness Aluvia");
        assert!(
            !rows
                .iter()
                .any(|r| r.label == "Patron:" || r.label == "Monarch/Patron:")
        );
        p.strings.insert(35, "Baroness Aluvia".into());
        let rows = creature_extra_rows(&p, None, true, 0);
        assert_eq!(find(&rows, "Monarch/Patron:").value, "Baroness Aluvia");
        p.strings.insert(35, "Patron Title".into());
        let rows = creature_extra_rows(&p, None, true, 0);
        assert_eq!(find(&rows, "Monarch:").value, "Baroness Aluvia");
        assert_eq!(find(&rows, "Patron:").value, "Patron Title");
    }

    #[test]
    fn the_configurable_extras_each_need_their_own_property() {
        let mut p = props();
        p.strings.insert(10, "The Fellows".into());
        p.strings.insert(43, "1/1/2023".into());
        p.ints.insert(125, 90);
        p.ints.insert(181, 7);
        p.ints.insert(192, 42);
        p.ints.insert(262, 3);
        let rows = creature_extra_rows(&p, None, true, 0);
        assert_eq!(find(&rows, "Fellowship:").value, "The Fellows");
        assert_eq!(find(&rows, "Arrived in Dereth:").value, "1/1/2023");
        assert_eq!(find(&rows, "Time in Dereth:").value, "1m 30s");
        assert_eq!(find(&rows, "Chess Rank:").value, "7");
        assert_eq!(find(&rows, "Fishing Skill:").value, "42");
        assert_eq!(find(&rows, "Titles Earned:").value, "3");
        // The string 43 (birth) is not the int 43 (deaths).
        assert!(!rows.iter().any(|r| r.label == "Deaths:"));
        for (deaths, expected) in [
            (0, "Has never died"),
            (-3, "Has never died"),
            (1, "1"),
            (5, "5"),
        ] {
            let mut p = props();
            p.ints.insert(43, deaths);
            let rows = creature_extra_rows(&p, None, true, 0);
            assert_eq!(find(&rows, "Deaths:").value, expected);
        }
    }

    #[test]
    fn a_monster_never_gains_the_character_rows() {
        let mut p = props();
        for (k, v) in [
            (281, 0x1),
            (30, 5),
            (125, 90),
            (181, 7),
            (192, 42),
            (43, 2),
            (262, 3),
        ] {
            p.ints.insert(k, v);
        }
        for (k, v) in [(21, "Monarch Title"), (10, "Fellows"), (43, "1/1/2023")] {
            p.strings.insert(k, v.into());
        }
        let rows = creature_extra_rows(&p, None, false, 0x1);
        assert!(rows.is_empty(), "{rows:?}");
    }

    #[test]
    fn a_complete_character_lists_its_extras_in_the_retail_order() {
        let mut p = props();
        for (k, v) in [(281, 0x1), (287, 50), (30, 5), (0x133, 10), (43, 0)] {
            p.ints.insert(k, v);
        }
        for (k, v) in [(21, "Monarch Title"), (35, "Patron Title"), (10, "Fellows")] {
            p.strings.insert(k, v.into());
        }
        let rows = creature_extra_rows(&p, Some(&levels([1, 0, 0, 0, 0, 0, 0, 0, 0])), true, 0x1);
        assert_eq!(
            labels(&rows),
            [
                "Society:",
                "Monarch:",
                "Patron:",
                "",
                "Head/Chest/Groin",
                "Bicep/Wrist/Hand",
                "Thigh/Shin/Foot",
                "",
                "Dmg/CritDmg",
                "",
                "Fellowship:",
                "Deaths:",
                "* = Unenchantable",
            ]
        );
        assert_eq!(rows[0].style, CreatureValueStyle::Positive);
    }

    #[test]
    fn the_header_identity_lines() {
        // AppraisalUiControllerTests.CharacterResponse_ComposesRetailHeaderIdentityBlock,
        // _HeritageFallsBackToCreatureTypeWhenGroupIsZero,
        // _PlayerKillerLineReflectsLocalObjectPwdBits,
        // AllegianceElement_ClearsUnlessRankIsAtLeastOne.
        assert_eq!(gender_heritage_display(2, 1, ""), "Female Aluvian");
        assert_eq!(gender_heritage_display(1, 0, "Ghost"), "Male Ghost");
        assert_eq!(gender_heritage_display(0, 0, ""), "");
        assert_eq!(
            player_killer_display(PublicWeenieFlags(0x20)),
            "Player Killer"
        );
        assert_eq!(
            player_killer_display(PublicWeenieFlags(0x0200_0000)),
            "Player Killer Lite"
        );
        assert_eq!(
            player_killer_display(PublicWeenieFlags::NONE),
            "Non-Player Killer"
        );
        let mut p = props();
        p.strings.insert(47, "The Empire".into());
        assert_eq!(allegiance_display(&p), "");
        p.ints.insert(30, 0);
        assert_eq!(allegiance_display(&p), "");
        p.ints.insert(30, 5);
        assert_eq!(allegiance_display(&p), "The Empire");
        assert_eq!(level_display(&props()), "???");
        p.ints.insert(25, 80);
        assert_eq!(level_display(&p), "80");
    }
}
