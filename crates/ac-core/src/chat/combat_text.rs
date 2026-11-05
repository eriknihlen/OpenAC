//! Combat lines in the chat window: the verb a hit gets from how hard it
//! was, the damage-type and body-part words, and the four notification
//! sentences (you hit, you were hit, your attack was evaded, you evaded).

use super::LogTextType;

/// How the chat window tints a combat line.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum CombatLineKind {
    /// The player's own hit, or an evade.
    Info,
    /// A hit taken.
    Warning,
    /// An attack that failed.
    Error,
}

/// The hit-verb thresholds, as a fraction of the target's health the hit
/// took (0.0 to 1.0). A value exactly on a threshold takes the weaker verb.
pub const LIGHT_THRESHOLD: f64 = 0.1;
/// See [`LIGHT_THRESHOLD`].
pub const MEDIUM_THRESHOLD: f64 = 0.25;
/// See [`LIGHT_THRESHOLD`].
pub const HEAVY_THRESHOLD: f64 = 0.5;

/// Damage-type bits.
pub mod damage_type {
    /// Slashing.
    pub const SLASH: u32 = 0x1;
    /// Piercing.
    pub const PIERCE: u32 = 0x2;
    /// Bludgeoning.
    pub const BLUDGEON: u32 = 0x4;
    /// Cold.
    pub const COLD: u32 = 0x8;
    /// Fire.
    pub const FIRE: u32 = 0x10;
    /// Acid.
    pub const ACID: u32 = 0x20;
    /// Electrical.
    pub const ELECTRIC: u32 = 0x40;
    /// Health drain.
    pub const HEALTH: u32 = 0x80;
    /// Stamina drain.
    pub const STAMINA: u32 = 0x100;
    /// Mana drain.
    pub const MANA: u32 = 0x200;
    /// Nether.
    pub const NETHER: u32 = 0x400;
    /// Prismatic ("base").
    pub const BASE: u32 = 0x1000_0000;
}

/// Attack-condition bits shown in combat lines. Other bits (overpower, for
/// one) add no text.
pub mod attack_conditions {
    /// The defender's critical-protection augmentation turned a critical
    /// hit into a normal one.
    pub const CRITICAL_PROTECTION_AUGMENTATION: u64 = 0x1;
    /// Recklessness.
    pub const RECKLESSNESS: u64 = 0x2;
    /// Sneak attack.
    pub const SNEAK_ATTACK: u64 = 0x4;
}

/// The verb for a hit: first person ("you slash") and third person ("it
/// slashes").
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HitVerbs {
    /// First-person form.
    pub first: &'static str,
    /// Third-person form.
    pub third: &'static str,
}

/// Picks the verbs for a hit of `damage_type` that took `percent` of the
/// target's health. The damage type is matched on its exact value, never
/// bit by bit: a combined mask, stamina, mana and prismatic all get
/// "hit"/"hits" with no threshold test.
///
/// Returns `(typed, verbs)`: `typed` is true when the damage type had a
/// verb ladder of its own. A negative (or not-a-number) `percent` gives
/// empty verbs and `false`.
#[must_use]
pub fn hit_verbs(damage_type: u32, percent: f64) -> (bool, HitVerbs) {
    if percent.is_nan() || percent < 0.0 {
        return (
            false,
            HitVerbs {
                first: "",
                third: "",
            },
        );
    }
    let ladder: [(&'static str, &'static str); 4] = match damage_type {
        damage_type::SLASH => [
            ("scratch", "scratches"),
            ("cut", "cuts"),
            ("slash", "slashes"),
            ("mangle", "mangles"),
        ],
        damage_type::PIERCE => [
            ("nick", "nicks"),
            ("stab", "stabs"),
            ("impale", "impales"),
            ("gore", "gores"),
        ],
        damage_type::BLUDGEON => [
            ("graze", "grazes"),
            ("bash", "bashes"),
            ("smash", "smashes"),
            ("crush", "crushes"),
        ],
        damage_type::COLD => [
            ("numb", "numbs"),
            ("chill", "chills"),
            ("frost", "frosts"),
            ("freeze", "freezes"),
        ],
        damage_type::FIRE => [
            ("singe", "singes"),
            ("scorch", "scorches"),
            ("burn", "burns"),
            ("incinerate", "incinerates"),
        ],
        damage_type::ACID => [
            ("blister", "blisters"),
            ("sear", "sears"),
            ("corrode", "corrodes"),
            ("dissolve", "dissolves"),
        ],
        damage_type::ELECTRIC => [
            ("spark", "sparks"),
            ("shock", "shocks"),
            ("jolt", "jolts"),
            ("blast", "blasts"),
        ],
        damage_type::HEALTH => [
            ("drain", "drains"),
            ("exhaust", "exhausts"),
            ("siphon", "siphons"),
            ("deplete", "depletes"),
        ],
        damage_type::NETHER => [
            ("scar", "scars"),
            ("twist", "twists"),
            ("wither", "withers"),
            ("eradicate", "eradicates"),
        ],
        _ => {
            return (
                false,
                HitVerbs {
                    first: "hit",
                    third: "hits",
                },
            );
        }
    };
    let band = if percent <= LIGHT_THRESHOLD {
        0
    } else if percent <= MEDIUM_THRESHOLD {
        1
    } else if percent <= HEAVY_THRESHOLD {
        2
    } else {
        3
    };
    let (first, third) = ladder[band];
    (true, HitVerbs { first, third })
}

/// The damage-type name, e.g. `"Slashing/Piercing"`: every named bit set,
/// in a fixed order, joined with `/`. Health, stamina and mana have no
/// name; empty when no named bit is set.
#[must_use]
pub fn damage_type_text(damage_type: u32) -> String {
    const NAMES: [(u32, &str); 9] = [
        (damage_type::SLASH, "Slashing"),
        (damage_type::PIERCE, "Piercing"),
        (damage_type::BLUDGEON, "Bludgeoning"),
        (damage_type::COLD, "Cold"),
        (damage_type::FIRE, "Fire"),
        (damage_type::ACID, "Acid"),
        (damage_type::ELECTRIC, "Electrical"),
        (damage_type::NETHER, "Nether"),
        (damage_type::BASE, "Prismatic"),
    ];
    NAMES
        .iter()
        .filter(|&&(bit, _)| damage_type & bit != 0)
        .map(|&(_, name)| name)
        .collect::<Vec<_>>()
        .join("/")
}

/// The body part's enum name, e.g. `"UPPER_ARM"`. Parts 11 and 14 and
/// anything outside -1..=27 are `"Unknown"`.
#[must_use]
pub fn body_part_text(body_part: i32) -> &'static str {
    match body_part {
        -1 => "UNDEFINED",
        0 => "HEAD",
        1 => "CHEST",
        2 => "ABDOMEN",
        3 => "UPPER_ARM",
        4 => "LOWER_ARM",
        5 => "HAND",
        6 => "UPPER_LEG",
        7 => "LOWER_LEG",
        8 => "FOOT",
        9 => "HORN",
        10 => "FRONT_LEG",
        12 => "FRONT_FOOT",
        13 => "REAR_LEG",
        15 => "REAR_FOOT",
        16 => "TORSO",
        17 => "TAIL",
        18 => "ARM",
        19 => "LEG",
        20 => "CLAW",
        21 => "WINGS",
        22 => "BREATH",
        23 => "TENTACLE",
        24 => "UPPER_TENTACLE",
        25 => "LOWER_TENTACLE",
        26 => "CLOAK",
        27 => "NUM",
        _ => "Unknown",
    }
}

/// An enum name made readable: underscores become spaces, letters go lower
/// case (ASCII only, whatever the locale).
#[must_use]
pub fn to_display(raw: &str) -> String {
    raw.replace('_', " ").to_ascii_lowercase()
}

/// [`body_part_text`] made readable, e.g. `"upper arm"`.
#[must_use]
pub fn body_part_display(body_part: i32) -> String {
    to_display(body_part_text(body_part))
}

fn plural(damage: u32) -> &'static str {
    if damage == 1 { "" } else { "s" }
}

/// The `{type }` slot of "points of {type }damage": the lower-cased type
/// name and a space, or nothing when the type has no name.
fn type_text_with_trailing_space(damage_type: u32) -> String {
    let text = damage_type_text(damage_type);
    if text.is_empty() {
        String::new()
    } else {
        format!("{} ", text.to_ascii_lowercase())
    }
}

/// The line for a hit the player landed:
/// `"You mangle Mosswart Defiler for 12 points of slashing damage!"`.
/// A critical hit is announced with TWO spaces after it.
#[must_use]
pub fn attacker_line(
    defender_name: &str,
    damage_type: u32,
    percent: f64,
    damage: u32,
    critical: bool,
    attack_conditions: u64,
) -> String {
    let (_, verbs) = hit_verbs(damage_type, percent);
    let mut line = String::with_capacity(96);
    if critical {
        line.push_str("Critical hit!  ");
    }
    if attack_conditions & attack_conditions::SNEAK_ATTACK != 0 {
        line.push_str("Sneak Attack! ");
    }
    if attack_conditions & attack_conditions::RECKLESSNESS != 0 {
        line.push_str("Recklessness! ");
    }
    line.push_str(&format!(
        "You {} {defender_name} for {damage} point{} of {}damage!",
        verbs.first,
        plural(damage),
        type_text_with_trailing_space(damage_type)
    ));
    if attack_conditions & attack_conditions::CRITICAL_PROTECTION_AUGMENTATION != 0 {
        line.push_str(
            " Your target's Critical Protection augmentation allows them to avoid your critical hit!",
        );
    }
    line
}

/// The line for a hit the player took, naming the body part:
/// `"Mosswart Stalker scorches your chest for 7 points of fire damage!"`.
#[must_use]
pub fn defender_line(
    attacker_name: &str,
    damage_type: u32,
    percent: f64,
    damage: u32,
    body_part: i32,
    critical: bool,
    attack_conditions: u64,
) -> String {
    defender_line_for_body_part_text(
        attacker_name,
        damage_type,
        percent,
        damage,
        &body_part_display(body_part),
        critical,
        attack_conditions,
    )
}

/// [`defender_line`] with the body part already worded. An empty part
/// reads "… hits you for …". The defender's own prefixes differ from the
/// attacker's: ONE space after "Critical hit!", and "Reckless!".
#[must_use]
pub fn defender_line_for_body_part_text(
    attacker_name: &str,
    damage_type: u32,
    percent: f64,
    damage: u32,
    body_part_text: &str,
    critical: bool,
    attack_conditions: u64,
) -> String {
    let (_, verbs) = hit_verbs(damage_type, percent);
    let mut line = String::with_capacity(112);
    if critical {
        line.push_str("Critical hit! ");
    }
    if attack_conditions & attack_conditions::SNEAK_ATTACK != 0 {
        line.push_str("Sneak Attack! ");
    }
    if attack_conditions & attack_conditions::RECKLESSNESS != 0 {
        line.push_str("Reckless! ");
    }
    line.push_str(attacker_name);
    line.push(' ');
    line.push_str(verbs.third);
    if body_part_text.is_empty() {
        line.push_str(" you");
    } else {
        line.push_str(" your ");
        line.push_str(body_part_text);
    }
    line.push_str(&format!(
        " for {damage} point{} of {}damage!",
        plural(damage),
        type_text_with_trailing_space(damage_type)
    ));
    if attack_conditions & attack_conditions::CRITICAL_PROTECTION_AUGMENTATION != 0 {
        line.push_str(" Your Critical Protection augmentation allows you to avoid a critical hit!");
    }
    line
}

/// The line when the player's target evaded.
#[must_use]
pub fn evasion_attacker_line(defender_name: &str) -> String {
    format!("{defender_name} evaded your attack.")
}

/// The line when the player evaded an attack.
#[must_use]
pub fn evasion_defender_line(attacker_name: &str) -> String {
    format!("You evaded {attacker_name}!")
}

/// A combat notification from the server that becomes a chat line.
///
/// Kills and finished attacks produce no client text, so they have no
/// variant here.
#[derive(Debug, Clone, PartialEq)]
pub enum CombatNotice {
    /// The player hit something.
    DamageDealt {
        /// Who was hit.
        defender_name: String,
        /// Damage-type bits.
        damage_type: u32,
        /// Points of damage.
        damage: u32,
        /// Fraction of the defender's health the hit took.
        damage_percent: f64,
        /// A critical hit.
        critical: bool,
        /// Attack-condition bits.
        attack_conditions: u64,
    },
    /// The player was hit.
    DamageTaken {
        /// Who hit.
        attacker_name: String,
        /// Damage-type bits.
        damage_type: u32,
        /// Points of damage.
        damage: u32,
        /// Fraction of the player's health the hit took.
        damage_percent: f64,
        /// Where the hit landed (the body-part number).
        hit_quadrant: u32,
        /// A critical hit.
        critical: bool,
        /// Attack-condition bits.
        attack_conditions: u64,
    },
    /// The player's target evaded.
    MissedOutgoing {
        /// Who evaded.
        defender_name: String,
    },
    /// The player evaded.
    EvadedIncoming {
        /// Whose attack was evaded.
        attacker_name: String,
    },
}

impl CombatNotice {
    /// The chat line for this notice: its text, its text class and its
    /// tint. The player's own attacks are `CombatSelf`, attacks on the
    /// player `CombatEnemy`; only a hit taken is tinted as a warning.
    #[must_use]
    pub fn chat_line(&self) -> (String, LogTextType, CombatLineKind) {
        match self {
            Self::DamageDealt {
                defender_name,
                damage_type,
                damage,
                damage_percent,
                critical,
                attack_conditions,
            } => (
                attacker_line(
                    defender_name,
                    *damage_type,
                    *damage_percent,
                    *damage,
                    *critical,
                    *attack_conditions,
                ),
                LogTextType::CombatSelf,
                CombatLineKind::Info,
            ),
            Self::DamageTaken {
                attacker_name,
                damage_type,
                damage,
                damage_percent,
                hit_quadrant,
                critical,
                attack_conditions,
            } => (
                // The wire's location number is the body part, read signed.
                defender_line(
                    attacker_name,
                    *damage_type,
                    *damage_percent,
                    *damage,
                    hit_quadrant.cast_signed(),
                    *critical,
                    *attack_conditions,
                ),
                LogTextType::CombatEnemy,
                CombatLineKind::Warning,
            ),
            Self::MissedOutgoing { defender_name } => (
                evasion_attacker_line(defender_name),
                LogTextType::CombatSelf,
                CombatLineKind::Info,
            ),
            Self::EvadedIncoming { attacker_name } => (
                evasion_defender_line(attacker_name),
                LogTextType::CombatEnemy,
                CombatLineKind::Info,
            ),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden values: OpenAC tests/AcDream.Core.Tests/Chat/CombatChatTranslatorTests.cs
    #[test]
    fn hit_verbs_match_the_verb_table_per_type_and_band() {
        let table: [(u32, [&str; 8]); 14] = [
            (
                0x1,
                [
                    "scratch",
                    "scratches",
                    "cut",
                    "cuts",
                    "slash",
                    "slashes",
                    "mangle",
                    "mangles",
                ],
            ),
            (
                0x2,
                [
                    "nick", "nicks", "stab", "stabs", "impale", "impales", "gore", "gores",
                ],
            ),
            (
                0x4,
                [
                    "graze", "grazes", "bash", "bashes", "smash", "smashes", "crush", "crushes",
                ],
            ),
            (
                0x8,
                [
                    "numb", "numbs", "chill", "chills", "frost", "frosts", "freeze", "freezes",
                ],
            ),
            (
                0x10,
                [
                    "singe",
                    "singes",
                    "scorch",
                    "scorches",
                    "burn",
                    "burns",
                    "incinerate",
                    "incinerates",
                ],
            ),
            (
                0x20,
                [
                    "blister",
                    "blisters",
                    "sear",
                    "sears",
                    "corrode",
                    "corrodes",
                    "dissolve",
                    "dissolves",
                ],
            ),
            (
                0x40,
                [
                    "spark", "sparks", "shock", "shocks", "jolt", "jolts", "blast", "blasts",
                ],
            ),
            (
                0x80,
                [
                    "drain", "drains", "exhaust", "exhausts", "siphon", "siphons", "deplete",
                    "depletes",
                ],
            ),
            (
                0x400,
                [
                    "scar",
                    "scars",
                    "twist",
                    "twists",
                    "wither",
                    "withers",
                    "eradicate",
                    "eradicates",
                ],
            ),
            (
                0x100,
                ["hit", "hits", "hit", "hits", "hit", "hits", "hit", "hits"],
            ),
            (
                0x200,
                ["hit", "hits", "hit", "hits", "hit", "hits", "hit", "hits"],
            ),
            (
                0x1000_0000,
                ["hit", "hits", "hit", "hits", "hit", "hits", "hit", "hits"],
            ),
            (
                0x1 | 0x2,
                ["hit", "hits", "hit", "hits", "hit", "hits", "hit", "hits"],
            ),
            (
                0x0,
                ["hit", "hits", "hit", "hits", "hit", "hits", "hit", "hits"],
            ),
        ];
        let samples = [0.05, 0.20, 0.40, 0.90];
        for (damage_type, expected) in table {
            for (band, sample) in samples.iter().enumerate() {
                let (_, verbs) = hit_verbs(damage_type, *sample);
                assert_eq!(
                    verbs.first,
                    expected[band * 2],
                    "{damage_type:#X} band {band}"
                );
                assert_eq!(
                    verbs.third,
                    expected[band * 2 + 1],
                    "{damage_type:#X} band {band}"
                );
            }
        }
    }

    #[test]
    fn boundary_values_take_the_weaker_verb() {
        let cases = [
            (0.10, "grazes"),
            (0.100_001, "bashes"),
            (0.25, "bashes"),
            (0.250_001, "smashes"),
            (0.50, "smashes"),
            (0.500_001, "crushes"),
        ];
        for (percent, expected) in cases {
            assert_eq!(hit_verbs(damage_type::BLUDGEON, percent).1.third, expected);
        }
    }

    #[test]
    fn plus_s_ladder_boundaries_also_take_the_weaker_verb() {
        let cases = [
            (0.10, "nick"),
            (0.100_001, "stab"),
            (0.25, "stab"),
            (0.250_001, "impale"),
            (0.50, "impale"),
            (0.500_001, "gore"),
        ];
        for (percent, expected) in cases {
            let (_, verbs) = hit_verbs(damage_type::PIERCE, percent);
            assert_eq!(verbs.first, expected);
            assert_eq!(verbs.third, format!("{expected}s"));
        }
    }

    #[test]
    fn lowest_band_is_inclusive_of_zero() {
        assert_eq!(hit_verbs(0x4, 0.0).1.first, "graze");
    }

    #[test]
    fn negative_percent_leaves_verbs_empty() {
        let (typed, verbs) = hit_verbs(0x4, -0.01);
        assert!(!typed);
        assert_eq!(verbs.first, "");
        assert_eq!(verbs.third, "");
    }

    #[test]
    fn damage_type_text_matches_the_game_literals() {
        let cases = [
            (0x1, "Slashing"),
            (0x2, "Piercing"),
            (0x4, "Bludgeoning"),
            (0x8, "Cold"),
            (0x10, "Fire"),
            (0x20, "Acid"),
            (0x40, "Electrical"),
            (0x400, "Nether"),
            (0x1000_0000, "Prismatic"),
        ];
        for (bit, expected) in cases {
            assert_eq!(damage_type_text(bit), expected);
        }
    }

    #[test]
    fn health_stamina_mana_contribute_nothing() {
        for bits in [0x80, 0x100, 0x200, 0x0] {
            assert_eq!(damage_type_text(bits), "");
        }
    }

    #[test]
    fn multiple_bits_join_with_slash_in_fixed_bit_order() {
        assert_eq!(damage_type_text(0x1 | 0x2), "Slashing/Piercing");
        assert_eq!(damage_type_text(0x400 | 0x8 | 0x10), "Cold/Fire/Nether");
        assert_eq!(damage_type_text(0x1 | 0x80), "Slashing");
    }

    #[test]
    fn body_part_text_matches_the_table_and_lower_casing() {
        let cases = [
            (-1, "undefined"),
            (0, "head"),
            (1, "chest"),
            (2, "abdomen"),
            (3, "upper arm"),
            (8, "foot"),
            (9, "horn"),
            (24, "upper tentacle"),
            (27, "num"),
            (11, "unknown"),
            (14, "unknown"),
            (99, "unknown"),
        ];
        for (part, expected) in cases {
            assert_eq!(body_part_display(part), expected);
        }
    }

    fn dealt(
        name: &str,
        damage_type: u32,
        damage: u32,
        percent: f64,
        critical: bool,
        conditions: u64,
    ) -> CombatNotice {
        CombatNotice::DamageDealt {
            defender_name: name.to_owned(),
            damage_type,
            damage,
            damage_percent: percent,
            critical,
            attack_conditions: conditions,
        }
    }

    fn taken(
        name: &str,
        damage_type: u32,
        damage: u32,
        quadrant: u32,
        critical: bool,
        percent: f64,
        conditions: u64,
    ) -> CombatNotice {
        CombatNotice::DamageTaken {
            attacker_name: name.to_owned(),
            damage_type,
            damage,
            damage_percent: percent,
            hit_quadrant: quadrant,
            critical,
            attack_conditions: conditions,
        }
    }

    #[test]
    fn damage_dealt_formats_the_attacker_template_as_info() {
        let (text, kind, tint) = dealt("Mosswart Defiler", 0x01, 12, 0.54, false, 0).chat_line();
        assert_eq!(
            text,
            "You mangle Mosswart Defiler for 12 points of slashing damage!"
        );
        assert_eq!(kind.as_u32(), 0x16);
        assert_eq!(tint, CombatLineKind::Info);
    }

    #[test]
    fn damage_dealt_single_point_uses_singular() {
        let (text, ..) = dealt("Drudge Skulker", 0x04, 1, 0.02, false, 0).chat_line();
        assert_eq!(
            text,
            "You graze Drudge Skulker for 1 point of bludgeoning damage!"
        );
    }

    #[test]
    fn damage_dealt_health_damage_omits_the_type_word() {
        let (text, ..) = dealt("Banderling Raider", 0x80, 5, 0.30, false, 0).chat_line();
        assert_eq!(text, "You siphon Banderling Raider for 5 points of damage!");
    }

    #[test]
    fn damage_dealt_critical_and_all_conditions_prefix_order_and_spacing() {
        let (text, ..) = dealt("Olthoi Soldier", 0x02, 99, 0.20, true, 0x7).chat_line();
        assert_eq!(
            text,
            "Critical hit!  Sneak Attack! Recklessness! \
             You stab Olthoi Soldier for 99 points of piercing damage! \
             Your target's Critical Protection augmentation allows them to avoid your critical hit!"
        );
    }

    #[test]
    fn damage_dealt_overpower_condition_adds_no_text() {
        let (text, ..) = dealt("Tusker", 0x04, 8, 0.05, false, 0x8).chat_line();
        assert_eq!(text, "You graze Tusker for 8 points of bludgeoning damage!");
    }

    #[test]
    fn damage_taken_formats_the_defender_template_as_warning() {
        let (text, kind, tint) = taken("Mosswart Stalker", 0x10, 7, 1, false, 0.12, 0).chat_line();
        assert_eq!(
            text,
            "Mosswart Stalker scorches your chest for 7 points of fire damage!"
        );
        assert_eq!(kind.as_u32(), 0x15);
        assert_eq!(tint, CombatLineKind::Warning);
    }

    #[test]
    fn damage_taken_critical_and_conditions_use_the_defender_literals() {
        let (text, ..) = taken("Olthoi Soldier", 0x02, 99, 0, true, 0.20, 0x7).chat_line();
        assert_eq!(
            text,
            "Critical hit! Sneak Attack! Reckless! \
             Olthoi Soldier stabs your head for 99 points of piercing damage! \
             Your Critical Protection augmentation allows you to avoid a critical hit!"
        );
    }

    #[test]
    fn defender_line_empty_body_part_uses_the_you_branch() {
        assert_eq!(
            defender_line_for_body_part_text("Drudge Slinker", 0x1, 0.20, 4, "", false, 0),
            "Drudge Slinker cuts you for 4 points of slashing damage!"
        );
    }

    #[test]
    fn missed_outgoing_formats_the_evasion_attacker_template_as_info() {
        let notice = CombatNotice::MissedOutgoing {
            defender_name: "Mosswart Sniper".to_owned(),
        };
        let (text, kind, tint) = notice.chat_line();
        assert_eq!(text, "Mosswart Sniper evaded your attack.");
        assert_eq!(kind.as_u32(), 0x16);
        assert_eq!(tint, CombatLineKind::Info);
    }

    #[test]
    fn evaded_incoming_formats_the_evasion_defender_template_as_info() {
        let notice = CombatNotice::EvadedIncoming {
            attacker_name: "Drudge Slinker".to_owned(),
        };
        let (text, kind, tint) = notice.chat_line();
        assert_eq!(text, "You evaded Drudge Slinker!");
        assert_eq!(kind.as_u32(), 0x15);
        assert_eq!(tint, CombatLineKind::Info);
    }

    #[test]
    fn no_combat_line_ever_contains_a_percentage() {
        let notices = [
            dealt("A", 0x01, 12, 0.54, true, 0x7),
            taken("B", 0x10, 7, 3, true, 0.235, 0x7),
            CombatNotice::MissedOutgoing {
                defender_name: "C".to_owned(),
            },
            CombatNotice::EvadedIncoming {
                attacker_name: "D".to_owned(),
            },
        ];
        for notice in notices {
            let (text, ..) = notice.chat_line();
            assert!(!text.contains('%'), "{text}");
            assert!(!text.contains('('), "{text}");
        }
    }
}
