//! The item report's weapon, armor, requirement and item-detail sections:
//! set and ratings, the weapon and shield lines, the defense and armor
//! modifiers, level, wield, use and activation limits, the item level,
//! caster data, boosts and healing, mana stones and remaining uses.

use ac_core::items::{ClientObject, EquipMask, PublicWeenieFlags};
use ac_proto::PropertyTables;
use ac_proto::appraise::{EnchantmentHighlight, IdentifyObjectResponse};

use super::{AppraisalNames, ReportBuilder, ReportStyle, damage_type_name, group_thousands};

/// One of the viewer's own skills, as an appraisal reads it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct AppraisalPlayerSkill {
    /// The buffed skill level.
    pub level: i32,
    /// 1 untrained, 2 trained, 3 specialized; 0 when the skill is unknown.
    pub advancement_class: u32,
}

/// The Shield skill.
const SHIELD_SKILL_ID: u32 = 48;

/// The wield requirement property triples: requirement kind, the stat it
/// names, the difficulty.
const WIELD_REQUIREMENTS: [(u32, u32, u32); 4] = [
    (158, 159, 160),
    (270, 271, 272),
    (273, 274, 275),
    (276, 277, 278),
];

fn get_int(p: &PropertyTables, id: u32) -> i32 {
    p.ints.get(&id).copied().unwrap_or(0)
}

fn get_bool(p: &PropertyTables, id: u32) -> bool {
    p.bools.get(&id).copied().unwrap_or(false)
}

fn get_string(p: &PropertyTables, id: u32) -> &str {
    p.strings.get(&id).map_or("", String::as_str)
}

/// How much of a shield's level the viewer can use: their buffed Shield
/// skill, halved unless they are specialized in it, and never more than
/// the shield's own level. A viewer without the skill gets 0.
#[must_use]
pub fn effective_shield_level(shield_level: i32, skill: AppraisalPlayerSkill) -> i32 {
    const SPECIALIZED: u32 = 3;
    let mut level = skill.level;
    if skill.advancement_class < SPECIALIZED {
        level /= 2;
    }
    level.min(shield_level)
}

/// A line's colour from an enchantment highlight: normal when the stat is
/// not enchanted, else beneficial or detrimental by its colour bit.
#[must_use]
pub fn enchantment_style(encoded: Option<EnchantmentHighlight>, low_bit: u32) -> ReportStyle {
    let Some(halves) = encoded else {
        return ReportStyle::Normal;
    };
    let bitfield = u32::from(halves.highlight) | (u32::from(halves.color) << 16);
    if bitfield & low_bit == 0 {
        ReportStyle::Normal
    } else if bitfield & (low_bit << 16) != 0 {
        ReportStyle::Beneficial
    } else {
        ReportStyle::Detrimental
    }
}

/// `value` rounded half away from zero after the 15 significant digits a
/// custom numeric format reads from a double.
fn round_custom(value: f64) -> i64 {
    let fifteen: f64 = format!("{value:.14e}").parse().unwrap_or(value);
    fifteen.round() as i64
}

/// A modifier as a signed whole percent ("+0%;-0%;0%"): "+12%", "-5%",
/// and "0%" for anything that rounds to zero.
#[must_use]
pub fn format_signed_percent(modifier: f64) -> String {
    let percent = round_custom(modifier * 100.0);
    match percent {
        p if p > 0 => format!("+{p}%"),
        p if p < 0 => format!("-{}%", p.unsigned_abs()),
        _ => "0%".to_owned(),
    }
}

/// `value` with `precision` significant digits, the general numeric form:
/// trailing zeros dropped, exponent form ("1.23E+05") when the exponent
/// is -5 or below, or at least `precision`. The last digit rounds half away
/// from zero on the exact value.
#[must_use]
pub fn format_general(value: f64, precision: usize) -> String {
    if value == 0.0 || !value.is_finite() {
        return if value.is_nan() {
            "NaN".to_owned()
        } else if value.is_infinite() {
            if value > 0.0 { "∞" } else { "-∞" }.to_owned()
        } else {
            "0".to_owned()
        };
    }
    let precision = precision.max(1);
    // The exact value, far past the digits kept.
    let exact = format!("{:.40e}", value.abs());
    let (mantissa, exponent) = exact.split_once('e').unwrap_or((&exact, "0"));
    let mut exponent: i32 = exponent.parse().unwrap_or(0);
    let mut digits: Vec<u8> = mantissa
        .bytes()
        .filter(u8::is_ascii_digit)
        .map(|d| d - b'0')
        .collect();
    let round_up = digits.get(precision).is_some_and(|d| *d >= 5);
    digits.truncate(precision);
    if round_up {
        let mut i = digits.len();
        loop {
            if i == 0 {
                digits.insert(0, 1);
                digits.truncate(precision);
                exponent += 1;
                break;
            }
            i -= 1;
            if digits[i] == 9 {
                digits[i] = 0;
            } else {
                digits[i] += 1;
                break;
            }
        }
    }
    while digits.len() > 1 && digits.last() == Some(&0) {
        digits.pop();
    }
    let text: String = digits.iter().map(|d| char::from(b'0' + d)).collect();
    let sign = if value < 0.0 { "-" } else { "" };
    if exponent <= -5 || exponent >= precision as i32 {
        let (first, rest) = text.split_at(1);
        let point = if rest.is_empty() {
            String::new()
        } else {
            format!(".{rest}")
        };
        let exponent_sign = if exponent < 0 { '-' } else { '+' };
        return format!(
            "{sign}{first}{point}E{exponent_sign}{:02}",
            exponent.unsigned_abs()
        );
    }
    if exponent < 0 {
        let zeros = "0".repeat((-exponent - 1) as usize);
        return format!("{sign}0.{zeros}{text}");
    }
    let whole = exponent as usize + 1;
    if text.len() <= whole {
        format!("{sign}{text}{}", "0".repeat(whole - text.len()))
    } else {
        let (int, frac) = text.split_at(whole);
        format!("{sign}{int}.{frac}")
    }
}

/// A damage value as the report prints it: four significant digits above
/// 10, three otherwise.
#[must_use]
pub fn format_retail_damage(damage: f64) -> String {
    format_general(damage, if damage > 10.0 { 4 } else { 3 })
}

fn format_modifier(modifier: f64) -> String {
    format_signed_percent(modifier - 1.0)
}

/// The body parts a worn item's coverage bits name.
#[must_use]
pub fn clothing_coverage(priority: u32) -> String {
    let mut coverage = Vec::new();
    for (bits, name) in [
        (0x4000, "Head"),
        (0x0008 | 0x0400, "Chest"),
        (0x0010 | 0x0800, "Abdomen"),
        (0x0020 | 0x1000, "Upper Arms"),
        (0x0040 | 0x2000, "Lower Arms"),
        (0x0080, "Hands"),
        (0x0002 | 0x0100, "Upper Legs"),
        (0x0004 | 0x0200, "Lower Legs"),
        (0x1_0000, "Feet"),
    ] {
        if priority & bits != 0 {
            coverage.push(name);
        }
    }
    coverage.join(", ")
}

/// The equipment set's name, if the report names it.
#[must_use]
pub fn equipment_set_name(set_id: i32) -> Option<String> {
    if (94..=129).contains(&set_id) {
        let tier = match (set_id - 94) / 12 {
            0 => "Minor",
            1 => "Major",
            _ => "Blackfire",
        };
        let member = (set_id - 94) % 12;
        let effect = match member % 4 {
            0 => "Stinging",
            1 => "Sparking",
            2 => "Smoldering",
            _ => "Shivering",
        };
        let soul = match member / 4 {
            0 => "Shrouded Soul",
            1 => "Darkened Mind",
            _ => "Clouded Spirit",
        };
        return Some(format!("{tier} {effect} {soul}"));
    }
    Some(
        match set_id {
            4 => "Carraida's Benediction",
            5 => "Noble Relic",
            6 => "Ancient Relic",
            7 => "Alduressa Relic",
            8 => "Shou-jen",
            9 => "Empyrean Rings",
            10 => "Arm, Mind, Heart",
            11 => "Coat of Perfect Light",
            12 => "Leggings of Perfect Light",
            13 => "Soldier's",
            14 => "Adept's",
            15 => "Archer's",
            16 => "Defender's",
            17 => "Tinker's",
            18 => "Crafter's",
            19 => "Hearty",
            20 => "Dexterous",
            21 => "Wise",
            22 => "Swift",
            23 => "Hardened",
            24 => "Reinforced",
            25 => "Interlocking",
            26 => "Flame Proof",
            27 => "Acid Proof",
            28 => "Cold Proof",
            29 => "Lightning Proof",
            30 => "Dedication",
            31 => "Gladiatorial Clothing",
            32 => "Ceremonial Clothing",
            33 => "Protective Clothing",
            35 => "Sigil of Defense",
            36 => "Sigil of Destruction",
            37 => "Sigil of Fury",
            38 => "Sigil of Growth",
            39 => "Sigil of Vigor",
            40 => "Heroic Protector",
            41 => "Heroic Destroyer",
            49 => "Weave of Alchemy",
            50 => "Weave of Arcane Lore",
            51 => "Weave of Armor Tinkering",
            52 => "Weave of Assess Person",
            53 | 67 | 74 | 75 | 79 => "Weave of Light Weapons",
            54 | 57 | 77 => "Weave of Missile Weapons",
            55 => "Weave of Cooking",
            56 => "Weave of Creature Enchantment",
            58 => "Weave of Finesse Weapons",
            59 => "Weave of Deception",
            60 => "Weave of Fletching",
            61 => "Weave of Healing",
            62 => "Weave of Item Enchantment",
            63 => "Weave of Item Tinkering",
            64 => "Weave of Leadership",
            65 => "Weave of Life Magic",
            66 => "Weave of Loyalty",
            68 => "Weave of Magic Defense",
            69 => "Weave of Magic Item Tinkering",
            70 => "Weave of Mana Conversion",
            71 => "Weave of Melee Defense",
            72 => "Weave of Missile Defense",
            73 => "Weave of Salvaging",
            76 => "Weave of Heavy Weapons",
            78 => "Weave of Two Handed Combat",
            80 => "Weave of Void Magic",
            81 => "Weave of War Magic",
            82 => "Weave of Weapon Tinkering",
            83 => "Weave of Assess Creature",
            84 => "Weave of Dirty Fighting",
            85 => "Weave of Dual Wield",
            86 => "Weave of Recklessness",
            87 => "Weave of Shield",
            88 => "Weave of Sneak Attack",
            89 => "Shou-jen Shozoku",
            90 => "Weave of Summoning",
            91 => "Shrouded Soul",
            92 => "Darkened Mind",
            93 => "Clouded Spirit",
            130 => "Shimmering Shadows",
            _ => return None,
        }
        .to_owned(),
    )
}

/// The examination window's range for a missile-slot weapon, in yards: the
/// flat ground distance of a 45-degree throw, launch speed (metres per
/// second) squared over gravity (9.8 m/s²), converted to yards and capped
/// at 85. Under ten yards it rounds up; from ten on it rounds down to a
/// multiple of five.
#[must_use]
pub fn missile_range_yards(max_velocity: f64) -> i32 {
    const INVERSE_GRAVITY: f64 = 0.102_040_816_326_530_6;
    const YARDS_PER_METER: f64 = 1.094;
    const RANGE_CAP_YARDS: f64 = 85.0;
    let range = (max_velocity.powi(2) * INVERSE_GRAVITY * YARDS_PER_METER).min(RANGE_CAP_YARDS);
    if range < 10.0 {
        return range.ceil() as i32;
    }
    let whole = range as i32;
    whole - whole % 5
}

/// A weapon's speed band.
#[must_use]
pub fn weapon_time_name(weapon_time: i32) -> &'static str {
    match weapon_time {
        i32::MIN..=10 => "Very Fast",
        11..=30 => "Fast",
        31..=49 => "Average",
        50..=79 => "Slow",
        _ => "Very Slow",
    }
}

fn weapon_subtype(kind: i32) -> &'static str {
    match kind {
        1 => " (Unarmed Weapon)",
        2 => " (Sword)",
        3 => " (Axe)",
        4 => " (Mace)",
        5 => " (Spear)",
        6 => " (Dagger)",
        7 => " (Staff)",
        8 => " (Bow)",
        9 => " (Crossbow)",
        10 => " (Thrown)",
        _ => "",
    }
}

/// A primary attribute's name.
#[must_use]
pub fn primary_attribute_name(attribute: i32) -> String {
    match attribute {
        1 => "Strength".to_owned(),
        2 => "Endurance".to_owned(),
        3 => "Quickness".to_owned(),
        4 => "Coordination".to_owned(),
        5 => "Focus".to_owned(),
        6 => "Self".to_owned(),
        _ => format!("Attribute {attribute}"),
    }
}

/// A vital's name.
#[must_use]
pub fn secondary_attribute_name(attribute: i32) -> String {
    match attribute {
        1 => "Max Health".to_owned(),
        2 => "Health".to_owned(),
        3 => "Max Stamina".to_owned(),
        4 => "Stamina".to_owned(),
        5 => "Max Mana".to_owned(),
        6 => "Mana".to_owned(),
        _ => format!("Vital {attribute}"),
    }
}

/// A use requirement names a skill the authored data cannot name
/// "Unknown Skill".
fn usage_skill_name(skill: i32, names: &dyn AppraisalNames) -> String {
    names
        .skill(skill)
        .unwrap_or_else(|| "Unknown Skill".to_owned())
}

/// The total experience item level `level` needs.
#[must_use]
pub fn item_level_to_total_xp(level: i32, base: i64, maximum: i32, style: i32) -> i64 {
    let capped = i64::from(level.clamp(0, maximum.max(0)));
    match style {
        1 => base * capped,
        2 => {
            let (mut total, mut cost) = (0i64, base);
            for _ in 0..capped {
                total += cost;
                cost *= 2;
            }
            total
        }
        3 => base * capped * (capped + 1) / 2,
        _ => 0,
    }
}

/// The item level `total` experience reaches.
#[must_use]
pub fn item_total_xp_to_level(total: i64, base: i64, maximum: i32, style: i32) -> i32 {
    if total <= 0 || base <= 0 || maximum <= 0 {
        return 0;
    }
    if style == 1 {
        return (total / base).min(i64::from(maximum)) as i32;
    }
    let (mut remaining, mut next) = (total, base);
    let mut level = 0;
    while level < maximum && remaining >= next {
        remaining -= next;
        level += 1;
        match style {
            2 => next *= 2,
            3 => next = base * (i64::from(level) + 1),
            _ => return 0,
        }
    }
    level
}

pub(super) fn set_and_ratings(r: &mut ReportBuilder, p: &PropertyTables) {
    let set_name = p.ints.get(&265).and_then(|id| equipment_set_name(*id));
    let set_shown = set_name.is_some();
    if let Some(name) = set_name {
        r.line(format!("Set: {name}"));
    }
    let ratings: Vec<String> = [
        (370, "Dam"),
        (371, "Dam Resist"),
        (372, "Crit"),
        (374, "Crit Dam"),
        (373, "Crit Resist"),
        (375, "Crit Dam Resist"),
        (376, "Heal Boost"),
        (377, "Nether Resist"),
        (378, "Life Resist"),
    ]
    .iter()
    .filter_map(|(id, label)| {
        p.ints
            .get(id)
            .filter(|v| **v > 0)
            .map(|v| format!("{label} {v}"))
    })
    .collect();
    let mut ratings_shown = !ratings.is_empty();
    if ratings_shown {
        r.line(format!("Ratings: {}", ratings.join(", ")));
    }
    let vitality = get_int(p, 379);
    if vitality > 0 {
        r.line(format!("This item adds {vitality} Vitality."));
        ratings_shown = true;
    }
    if ratings_shown {
        r.blank_line();
    }
    if set_shown || ratings_shown {
        r.blank_line();
    }
}

pub(super) fn weapon_and_armor(
    r: &mut ReportBuilder,
    obj: &ClientObject,
    a: &IdentifyObjectResponse,
    names: &dyn AppraisalNames,
) {
    let p = &a.properties;
    let hook = a.hook_profile.filter(|_| obj.is_hook());
    let valid_locations = hook.map_or(obj.valid_locations.0, |h| h.valid_locations);
    let ammo_type = hook.map_or(u32::from(obj.ammo_type.unwrap_or(0)), |h| h.ammo_type);
    const WEAPON_AND_SHIELD_LOCATIONS: u32 = 0x03F0_0000;
    let weapon_or_shield = valid_locations & WEAPON_AND_SHIELD_LOCATIONS != 0;

    if weapon_or_shield && valid_locations & EquipMask::SHIELD.0 != 0 {
        match p.ints.get(&28) {
            Some(&shield_level) => {
                r.line_styled(
                    format!("Base Shield Level: {shield_level}"),
                    enchantment_style(a.armor_enchantments, 0x0001),
                );
                let effective =
                    effective_shield_level(shield_level, names.player_skill(SHIELD_SKILL_ID));
                r.line(format!(
                    "Effective Shield Level : {effective} (with Shield skill)"
                ));
            }
            None => r.line("Shield Level: Unknown"),
        }
    }

    if weapon_or_shield && let Some(weapon) = a.weapon_profile {
        // The weapon line is named from the client's built-in list, and
        // the line is left out for a skill that list does not name.
        if let Some(skill) = ac_core::player::skill_names::try_get_name(weapon.weapon_skill as i32)
        {
            r.line(format!("Skill: {skill}{}", weapon_subtype(get_int(p, 353))));
        }
        let launcher = valid_locations & EquipMask::MISSILE_WEAPON.0 != 0 && ammo_type != 0;
        let label = if launcher { "Damage Bonus" } else { "Damage" };
        let damage = if weapon.damage == u32::MAX {
            "Unknown".to_owned()
        } else {
            let maximum = f64::from(weapon.damage);
            let minimum = (1.0 - weapon.damage_variance) * maximum;
            let mut text = if maximum - minimum > 0.0002 {
                format!("{} - {}", format_retail_damage(minimum), weapon.damage)
            } else {
                weapon.damage.to_string()
            };
            if !launcher {
                // Only a mask with no type at all is "unknown".
                if weapon.damage_type == 0 {
                    text.push_str(", unknown type");
                } else {
                    text.push_str(&format!(", {}", damage_type_name(weapon.damage_type)));
                }
            }
            text
        };
        let mut style = enchantment_style(a.weapon_enchantments, 0x0008);
        if style == ReportStyle::Normal {
            style = enchantment_style(a.weapon_enchantments, 0x0010);
        }
        r.line_styled(format!("{label}: {damage}"), style);

        let elemental = get_int(p, 204);
        if elemental > 0 {
            r.line(format!(
                "Elemental Damage Bonus: {elemental}, {}.",
                damage_type_name(weapon.damage_type)
            ));
        }
        if launcher {
            r.line_styled(
                if a.success {
                    format!("Damage Modifier: {}.", format_modifier(weapon.damage_mod))
                } else {
                    "Damage Modifier: Unknown".to_owned()
                },
                enchantment_style(a.weapon_enchantments, 0x0020),
            );
        }
        let timed =
            EquipMask::MELEE_WEAPON.0 | EquipMask::MISSILE_WEAPON.0 | EquipMask::TWO_HANDED.0;
        if valid_locations & timed != 0 {
            // Range belongs to anything that fills the missile slot, thrown
            // weapons included; it needs no ammunition type the way the
            // launcher rows above do.
            let missile_slot = valid_locations & EquipMask::MISSILE_WEAPON.0 != 0;
            if weapon.weapon_time == u32::MAX {
                r.line("Speed:  Unknown");
                if missile_slot {
                    r.line("Range:  Unknown");
                }
            } else {
                r.line_styled(
                    format!(
                        "Speed: {} ({})",
                        weapon_time_name(weapon.weapon_time as i32),
                        weapon.weapon_time
                    ),
                    enchantment_style(a.weapon_enchantments, 0x0004),
                );
                if missile_slot {
                    let range = missile_range_yards(weapon.max_velocity);
                    let estimated = if weapon.max_velocity_estimated != 0 {
                        " (based on STRENGTH 100)"
                    } else {
                        ""
                    };
                    r.line(format!("Range: {range} yds.{estimated}"));
                }
            }
        }
        if !launcher && (weapon.weapon_offense - 1.0).abs() > 0.000_001 {
            r.line_styled(
                format!(
                    "Bonus to Attack Skill: {}.",
                    format_signed_percent(weapon.weapon_offense - 1.0)
                ),
                enchantment_style(a.weapon_enchantments, 0x0001),
            );
        }
    }

    ammunition_description(r, valid_locations, ammo_type);

    if !weapon_or_shield && valid_locations & 0x0800_7FFF != 0 {
        let coverage = clothing_coverage(obj.priority);
        if !coverage.is_empty() {
            r.line(format!("Covers {coverage}"));
        }
    }
}

fn ammunition_description(r: &mut ReportBuilder, valid_locations: u32, ammo_type: u32) {
    if ammo_type == 0 {
        return;
    }
    // The crystal and chorizite kinds count as their plain kind.
    let base = match ammo_type {
        0x008 | 0x040 => 0x001,
        0x010 | 0x080 => 0x002,
        0x020 | 0x100 => 0x004,
        other => other,
    };
    let launcher = valid_locations & EquipMask::MISSILE_WEAPON.0 != 0;
    let text = match (launcher, base) {
        (true, 0x001) => "Uses arrows as ammunition.",
        (true, 0x002) => "Uses quarrels as ammunition.",
        (true, 0x004) => "Uses atlatl darts as ammunition.",
        (false, 0x001) => "Used as ammunition by bows.",
        (false, 0x002) => "Used as ammunition by crossbows.",
        (false, 0x004) => "Used as ammunition by atlatls.",
        _ => return,
    };
    r.line(text);
}

pub(super) fn defense_modifiers(r: &mut ReportBuilder, a: &IdentifyObjectResponse) {
    let p = &a.properties;
    modifier(
        r,
        p,
        29,
        "Bonus to Melee Defense",
        enchantment_style(a.weapon_enchantments, 0x0002),
    );
    modifier(r, p, 149, "Bonus to Missile Defense", ReportStyle::Normal);
    modifier(r, p, 150, "Bonus to Magic Defense", ReportStyle::Normal);
}

fn modifier(r: &mut ReportBuilder, p: &PropertyTables, id: u32, label: &str, style: ReportStyle) {
    let Some(&value) = p.floats.get(&id) else {
        return;
    };
    if (value - 1.0).abs() <= 0.000_001 {
        return;
    }
    r.line_styled(
        format!("{label}: {}.", format_signed_percent(value - 1.0)),
        style,
    );
}

pub(super) fn armor_modifiers(r: &mut ReportBuilder, a: &IdentifyObjectResponse) {
    let Some(armor) = a.armor_profile else {
        return;
    };
    let Some(&level) = a.properties.ints.get(&28).filter(|l| **l > 0) else {
        return;
    };
    let style = |bit| enchantment_style(a.armor_enchantments, bit);
    r.paragraph_styled(format!("Armor Level: {level}"), style(0x0001));
    for (name, value, bit) in [
        ("Slashing", armor.slashing, 0x0002),
        ("Piercing", armor.piercing, 0x0004),
        ("Bludgeoning", armor.bludgeoning, 0x0008),
        ("Fire", armor.fire, 0x0020),
        ("Cold", armor.cold, 0x0010),
        ("Acid", armor.acid, 0x0040),
        ("Electric", armor.lightning, 0x0080),
        ("Nether", armor.nether, 0x0100),
    ] {
        protection(r, name, level, value, style(bit));
    }
}

fn protection(r: &mut ReportBuilder, kind: &str, level: i32, modifier: f32, style: ReportStyle) {
    let quality = if modifier <= 0.0002 {
        "None"
    } else if modifier < 0.4 {
        "Poor"
    } else if modifier < 0.8 {
        "Below Average"
    } else if modifier < 1.2 {
        "Average"
    } else if modifier < 1.6 {
        "Above Average"
    } else if modifier < 2.0 {
        "Excellent"
    } else {
        "Unparalleled"
    };
    let effective = f64::from(level) * f64::from(modifier);
    r.line_styled(
        format!("{kind}: {quality}  ({})", round_custom(effective)),
        style,
    );
}

pub(super) fn level_limits(r: &mut ReportBuilder, p: &PropertyTables) {
    let (minimum, maximum) = (get_int(p, 86), get_int(p, 87));
    if minimum > 0 && maximum > 0 {
        r.paragraph(if minimum == maximum {
            format!("Restricted to characters of Level {minimum}.")
        } else {
            format!("Restricted to characters of Levels {minimum} to {maximum}.")
        });
    } else if minimum > 0 {
        r.paragraph(format!(
            "Restricted to characters of Level {minimum} or greater."
        ));
    } else if maximum > 0 {
        r.paragraph(format!(
            "Restricted to characters of Level {maximum} or below."
        ));
    }
    let destination = get_string(p, 38);
    if !destination.trim().is_empty() {
        r.paragraph(format!("Destination: {destination}"));
    }
}

fn original_owner(p: &PropertyTables) -> &str {
    let owner = get_string(p, 25);
    if owner.trim().is_empty() {
        "the original owner"
    } else {
        owner
    }
}

pub(super) fn wield_requirements(
    r: &mut ReportBuilder,
    p: &PropertyTables,
    names: &dyn AppraisalNames,
) {
    if get_bool(p, 85) {
        r.line(format!("Wield requires {}", original_owner(p)));
    }
    if get_int(p, 26) == 1 {
        r.line("Use requires Throne of Destiny.");
    }
    if let Some(&heritage) = p.ints.get(&324) {
        let name = names.heritage(heritage);
        if !name.is_empty() {
            r.line(format!("Wield requires {name}"));
        }
    }
    for (index, (requirement_id, stat_id, difficulty_id)) in WIELD_REQUIREMENTS.iter().enumerate() {
        let (Some(&requirement), Some(&stat), Some(&difficulty)) = (
            p.ints.get(requirement_id),
            p.ints.get(stat_id),
            p.ints.get(difficulty_id),
        ) else {
            continue;
        };
        let quality = requirement_quality(requirement, stat, difficulty, names);
        match requirement {
            8 => {
                let training = match (difficulty == 3, index == 3) {
                    (true, true) => "Specialized",
                    (true, false) => "specialized",
                    (false, true) => "Trained",
                    (false, false) => "trained",
                };
                r.line(format!("Wield requires {training} {quality}"));
            }
            11 => r.line(format!("Wield requires {quality} type")),
            12 => r.line(format!("Wield requires {quality} race")),
            _ if !quality.is_empty() => {
                r.line(format!("Wield requires {quality} {difficulty}"));
            }
            _ => {}
        }
    }
}

fn requirement_quality(
    requirement: i32,
    stat: i32,
    difficulty: i32,
    names: &dyn AppraisalNames,
) -> String {
    let base = if matches!(requirement, 2 | 4 | 6) {
        "base "
    } else {
        ""
    };
    match requirement {
        // A wield requirement appends the authored name, or nothing at all
        // when there is none (leaving just the "base " prefix).
        1 | 2 | 8 => format!("{base}{}", names.skill(stat).unwrap_or_default()),
        3 | 4 => format!("{base}{}", primary_attribute_name(stat)),
        5 | 6 => format!("{base}{}", secondary_attribute_name(stat)),
        7 => "level".to_owned(),
        9 | 10 => match stat {
            0x11F => "Standing with the Celestial Hand",
            0x120 => "Standing with the Eldrytch Web",
            0x121 => "Standing with the Radiant Blood",
            _ => "unknown quality",
        }
        .to_owned(),
        11 => names.creature(difficulty),
        12 => names.heritage(difficulty),
        _ => String::new(),
    }
}

pub(super) fn usage_limits(r: &mut ReportBuilder, p: &PropertyTables, names: &dyn AppraisalNames) {
    let level = get_int(p, 369);
    let skill = get_int(p, 366);
    let difficulty = get_int(p, 367);
    let specialized = get_int(p, 368);
    if level > 0 || (skill > 0 && difficulty > 0) || specialized > 0 {
        r.blank_line();
    }
    if level > 0 {
        r.line(format!("Use requires level {level}."));
    }
    if skill > 0 && difficulty > 0 {
        r.line(format!(
            "Use requires {} of at least {difficulty}.",
            usage_skill_name(skill, names)
        ));
    }
    if specialized > 0 {
        r.line(format!(
            "Use requires specialized {}.",
            usage_skill_name(specialized, names)
        ));
    }
}

pub(super) fn item_level(r: &mut ReportBuilder, p: &PropertyTables) {
    let (Some(&base), Some(&maximum), Some(&style)) =
        (p.int64s.get(&5), p.ints.get(&319), p.ints.get(&320))
    else {
        return;
    };
    if base <= 0 || maximum <= 0 || style <= 0 {
        return;
    }
    let experience = p.int64s.get(&4).copied().unwrap_or(0).max(0);
    let current = item_total_xp_to_level(experience, base, maximum, style);
    let next_level = (current + 1).min(maximum);
    let next = item_level_to_total_xp(next_level, base, maximum, style);
    r.line(format!("Item Level: {next_level} / {maximum}"));
    r.line(format!(
        "Item XP: {} / {}",
        group_thousands(experience),
        group_thousands(next)
    ));
    r.blank_line();
    if get_int(p, 352) == 2 {
        r.line("This cloak has a chance to reduce an incoming attack by 200 damage.");
        r.blank_line();
    }
}

pub(super) fn activation_requirements(
    r: &mut ReportBuilder,
    p: &PropertyTables,
    succeeded: bool,
    names: &dyn AppraisalNames,
) {
    if !succeeded {
        return;
    }
    let mut requirements = Vec::new();
    for (name, id) in [("Arcane Lore", 109), ("Allegiance Rank", 110)] {
        let value = get_int(p, id);
        if value > 0 {
            requirements.push(format!("{name}: {value}"));
        }
    }
    let heritage = get_int(p, 188);
    if heritage != 0 {
        let name = names.heritage(heritage);
        if !name.is_empty() {
            requirements.push(name);
        }
    }
    // Unlike a use requirement, an activation requirement the authored
    // data cannot name is left out.
    let (skill_level, skill) = (get_int(p, 115), get_int(p, 176));
    if skill_level > 0
        && skill > 0
        && let Some(name) = names.skill(skill)
    {
        requirements.push(format!("{name}: {skill_level}"));
    }
    let (attribute_level, attribute) = (get_int(p, 258), get_int(p, 257));
    if attribute_level > 0 && attribute > 0 {
        requirements.push(format!(
            "{}: {attribute_level}",
            primary_attribute_name(attribute)
        ));
    }
    let (vital_level, vital) = (get_int(p, 260), get_int(p, 259));
    if vital_level > 0 && vital > 0 {
        requirements.push(format!(
            "{}: {vital_level}",
            secondary_attribute_name(vital)
        ));
    }
    if !requirements.is_empty() {
        r.line(format!("Activation requires {}", requirements.join(", ")));
    }
    if get_bool(p, 94) {
        r.line(format!(
            "This item can only be activated by {}.",
            original_owner(p)
        ));
    }
}

pub(super) fn caster_data(r: &mut ReportBuilder, a: &IdentifyObjectResponse) {
    let p = &a.properties;
    if let Some(&conversion) = p.floats.get(&144) {
        r.paragraph_styled(
            format!(
                "Bonus to Mana Conversion: {}.",
                format_signed_percent(conversion)
            ),
            enchantment_style(a.resist_enchantments, 0x1000),
        );
    }
    if let (Some(&elemental), Some(&kind)) = (p.floats.get(&152), p.ints.get(&45)) {
        r.paragraph_styled(
            format!("Damage bonus for {} spells:", damage_type_name(kind as u32)),
            enchantment_style(a.resist_enchantments, 0x2000),
        );
        r.line(format!(
            " vs. Monsters: {}.",
            format_signed_percent(elemental - 1.0)
        ));
        let players = 1.0 + (elemental - 1.0) * 0.25;
        r.line(format!(
            " vs. Players: {}.",
            format_signed_percent(players - 1.0)
        ));
    }
}

pub(super) fn boost_and_healing(
    r: &mut ReportBuilder,
    obj: &ClientObject,
    a: &IdentifyObjectResponse,
) {
    let p = &a.properties;
    let public = obj.public_weenie_bitfield.map_or(0, |f| f.0);
    let healer = public & PublicWeenieFlags::HEALER.0 != 0
        || obj.is_hook() && a.hook_profile.is_some_and(|h| h.flags & 0x2 != 0);
    let boost = get_int(p, 90);
    let verb = if boost >= 0 { "Restores" } else { "Depletes" };
    let amount = boost.unsigned_abs();
    let text = match get_int(p, 89) {
        2 => Some(format!("{verb} {amount} Health when used.")),
        4 => Some(format!("{verb} {amount} Stamina when consumed.")),
        6 => Some(format!("{verb} {amount} Mana when used.")),
        _ => None,
    };
    if !healer
        && let Some(text) = text
        && p.ints.contains_key(&90)
    {
        r.paragraph(text);
    }
    if !healer {
        return;
    }
    if let Some(&bonus) = p.ints.get(&90) {
        r.paragraph(format!("Bonus to Healing Skill: {bonus}"));
    }
    if let Some(&modifier) = p.floats.get(&100) {
        r.line(format!(
            "Restoration Bonus: {}%",
            round_custom(modifier * 100.0)
        ));
    }
}

pub(super) fn mana_stone(r: &mut ReportBuilder, a: &IdentifyObjectResponse) {
    if !a.spell_book.is_empty() {
        return;
    }
    let p = &a.properties;
    if let Some(&mana) = p.ints.get(&107) {
        r.line(format!("Stored Mana: {mana}"));
    }
    if let Some(&efficiency) = p.floats.get(&87) {
        r.line(format!("Efficiency: {}%", round_custom(efficiency * 100.0)));
    }
    if let Some(&destruction) = p.floats.get(&137) {
        r.line(format!(
            "Chance of Destruction: {}%",
            round_custom(destruction * 100.0)
        ));
    }
}

pub(super) fn remaining_uses(
    r: &mut ReportBuilder,
    obj: &ClientObject,
    a: &IdentifyObjectResponse,
) {
    let p = &a.properties;
    if let Some(&keys) = p.ints.get(&193) {
        r.line(format!(
            "Contains {keys} {}",
            if keys == 1 { "key." } else { "keys." }
        ));
    }
    if get_bool(p, 63) {
        r.line("Number of uses remaining:  Unlimited");
        return;
    }
    if let Some(&uses) = p.ints.get(&92) {
        r.line(format!("Number of uses remaining: {uses}"));
        return;
    }
    let public = obj.public_weenie_bitfield.map_or(0, |f| f.0);
    let tool = public & (PublicWeenieFlags::HEALER.0 | PublicWeenieFlags::LOCKPICK.0) != 0
        || a.hook_profile.is_some_and(|h| h.flags & 0xA != 0);
    if !a.success && tool {
        r.paragraph("Number of uses remaining:  Unknown");
    }
}
