//! The character information panel (layout 0x2100006E, slot 0x10000183):
//! one scrolling text with the game's report on the character: birth,
//! time played and deaths, natural resistances, innate attributes, chess
//! and fishing, masteries and augmentations, and the burden.
//!
//! The report is composed from the authored phrases (string table
//! 0x23000001, `ID_CharacterInfo_*`, each split where its numbers go) and
//! the player's numbers handed to [`CharacterInfoController::update`].

use std::collections::{BTreeMap, HashMap};

use ac_core::items::burden;

use crate::host::PanelController;
use crate::import::ImportedLayout;
use crate::panels::items::{bind_click, emit};
use crate::panels::transcript::wrap_text;
use crate::panels::{PanelOutput, panel_id};
use crate::root::{ElementMessage, UiRoot};
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::{ScrollSource, Scrollbar, Text};

/// The layout hosting the main panels.
pub const HOST_LAYOUT_ID: u32 = 0x2100_006E;
/// The panel's slot in it.
pub const ROOT_ID: u32 = 0x1000_0183;
/// The report text.
pub const MAIN_TEXT_ID: u32 = 0x1000_011D;
/// Its scrollbar.
pub const SCROLLBAR_ID: u32 = 0x1000_011E;
/// The close button.
pub const CLOSE_ID: u32 = 0x1000_00FC;

/// One augmentation line: the int property counting it, its phrase, and
/// whether the count goes into the phrase.
#[derive(Debug, Clone, Copy)]
struct Augmentation {
    property: u32,
    key: &'static str,
    include_value: bool,
}

const fn aug(property: u32, key: &'static str, include_value: bool) -> Augmentation {
    Augmentation {
        property,
        key,
        include_value,
    }
}

const AUGMENTATIONS: [Augmentation; 43] = [
    aug(
        0xDA,
        "ID_CharacterInfo_Augmentation_Attribute_Strength",
        true,
    ),
    aug(
        0xDB,
        "ID_CharacterInfo_Augmentation_Attribute_Endurance",
        true,
    ),
    aug(
        0xDC,
        "ID_CharacterInfo_Augmentation_Attribute_Coordination",
        true,
    ),
    aug(
        0xDD,
        "ID_CharacterInfo_Augmentation_Attribute_Quickness",
        true,
    ),
    aug(0xDE, "ID_CharacterInfo_Augmentation_Attribute_Focus", true),
    aug(0xDF, "ID_CharacterInfo_Augmentation_Attribute_Self", true),
    aug(0xF0, "ID_CharacterInfo_Augmentation_Resist_Slash", true),
    aug(0xF1, "ID_CharacterInfo_Augmentation_Resist_Pierce", true),
    aug(0xF2, "ID_CharacterInfo_Augmentation_Resist_Blunt", true),
    aug(0xF3, "ID_CharacterInfo_Augmentation_Resist_Acid", true),
    aug(0x147, "ID_CharacterInfo_Augmentation_Resist_Nether", true),
    aug(0xF4, "ID_CharacterInfo_Augmentation_Resist_Fire", true),
    aug(0xF5, "ID_CharacterInfo_Augmentation_Resist_Frost", true),
    aug(0xF6, "ID_CharacterInfo_Augmentation_Resist_Lightning", true),
    aug(0xE0, "ID_CharacterInfo_Augmentation_Spec_Salvaging", false),
    aug(
        0xE1,
        "ID_CharacterInfo_Augmentation_Spec_ItemTinkering",
        false,
    ),
    aug(
        0xE2,
        "ID_CharacterInfo_Augmentation_Spec_ArmorTinkering",
        false,
    ),
    aug(
        0xE3,
        "ID_CharacterInfo_Augmentation_Spec_MagicItemTinkering",
        false,
    ),
    aug(
        0xE4,
        "ID_CharacterInfo_Augmentation_Spec_WeaponTinkering",
        false,
    ),
    aug(0x125, "ID_CharacterInfo_Augmentation_Spec_Gearcraft", false),
    aug(0xE5, "ID_CharacterInfo_Augmentation_ExtraPackSlot", false),
    aug(
        0xE6,
        "ID_CharacterInfo_Augmentation_IncreasedCarryingCapacity",
        true,
    ),
    aug(
        0xE7,
        "ID_CharacterInfo_Augmentation_LessDeathItemLoss",
        true,
    ),
    aug(
        0xE8,
        "ID_CharacterInfo_Augmentation_SpellsRemainPastDeath",
        false,
    ),
    aug(0xE9, "ID_CharacterInfo_Augmentation_CriticalDefense", false),
    aug(0xEA, "ID_CharacterInfo_Augmentation_BonusXP", false),
    aug(0xEB, "ID_CharacterInfo_Augmentation_BonusSalvage", true),
    aug(
        0xEC,
        "ID_CharacterInfo_Augmentation_BonusImbueChance",
        false,
    ),
    aug(0xED, "ID_CharacterInfo_Augmentation_FasterRegen", true),
    aug(
        0xEE,
        "ID_CharacterInfo_Augmentation_IncreasedSpellDuration",
        true,
    ),
    aug(
        0x126,
        "ID_CharacterInfo_Augmentation_Infused_CreatureMagic",
        true,
    ),
    aug(
        0x127,
        "ID_CharacterInfo_Augmentation_Infused_ItemMagic",
        true,
    ),
    aug(
        0x128,
        "ID_CharacterInfo_Augmentation_Infused_LifeMagic",
        true,
    ),
    aug(
        0x129,
        "ID_CharacterInfo_Augmentation_Infused_WarMagic",
        true,
    ),
    aug(
        0x148,
        "ID_CharacterInfo_Augmentation_Infused_VoidMagic",
        true,
    ),
    aug(0x12C, "ID_CharacterInfo_Augmentation_SkilledMelee", true),
    aug(0x12D, "ID_CharacterInfo_Augmentation_SkilledMissile", true),
    aug(0x12E, "ID_CharacterInfo_Augmentation_SkilledMagic", true),
    aug(0x135, "ID_CharacterInfo_Augmentation_DamageBonus", true),
    aug(0x136, "ID_CharacterInfo_Augmentation_DamageResist", true),
    aug(
        0x12A,
        "ID_CharacterInfo_Augmentation_CriticalExpertise",
        false,
    ),
    aug(0x12B, "ID_CharacterInfo_Augmentation_CriticalPower", false),
    aug(
        0x146,
        "ID_CharacterInfo_Augmentation_JackOfAllTrades",
        false,
    ),
];

/// One luminance aura: its property, its phrase, the Seer's phrase for the
/// ranks past the first five (the four rating auras only), and what one
/// rank counts for.
struct LuminanceAura {
    property: u32,
    base_key: &'static str,
    seer_key: Option<&'static str>,
    points_per_rank: i32,
}

const fn lum(
    property: u32,
    base_key: &'static str,
    seer_key: Option<&'static str>,
    points_per_rank: i32,
) -> LuminanceAura {
    LuminanceAura {
        property,
        base_key,
        seer_key,
        points_per_rank,
    }
}

/// The luminance block's header, written whether or not any aura is owned.
const LUMINANCE_HEADER_KEY: &str = "ID_CharacterInfo_Luminance_Header";
/// The ranks a two-tier aura reads as its base aura; the rest are the
/// Seer's.
const LUMINANCE_BASE_RANK_CAP: i32 = 5;

// Luminance auras, listed under their own header between the masteries and
// the other augmentations. The first four are bought in two tiers; mana use
// and mana gain show five rating points per rank, the specialist aura two
// skill points per rank.
const LUMINANCE_AURAS: [LuminanceAura; 11] = [
    lum(
        0x14D,
        "ID_CharacterInfo_Luminance_Base_Damage",
        Some("ID_CharacterInfo_Luminance_Spec_Damage"),
        1,
    ),
    lum(
        0x14E,
        "ID_CharacterInfo_Luminance_Base_Reduction",
        Some("ID_CharacterInfo_Luminance_Spec_Reduction"),
        1,
    ),
    lum(
        0x14F,
        "ID_CharacterInfo_Luminance_Base_Crit_Damage",
        Some("ID_CharacterInfo_Luminance_Spec_Crit_Damage"),
        1,
    ),
    lum(
        0x150,
        "ID_CharacterInfo_Luminance_Base_Crit_Reduction",
        Some("ID_CharacterInfo_Luminance_Spec_Crit_Reduction"),
        1,
    ),
    lum(
        0x152,
        "ID_CharacterInfo_Luminance_Base_Surge_Chance",
        None,
        1,
    ),
    lum(0x153, "ID_CharacterInfo_Luminance_Base_Mana_Use", None, 5),
    lum(0x154, "ID_CharacterInfo_Luminance_Base_Mana_Gain", None, 5),
    lum(0x156, "ID_CharacterInfo_Luminance_Base_Healing", None, 1),
    lum(
        0x157,
        "ID_CharacterInfo_Luminance_Base_Skilled_Craft",
        None,
        1,
    ),
    lum(
        0x158,
        "ID_CharacterInfo_Luminance_Spec_Skilled_Spec",
        None,
        2,
    ),
    lum(0x16D, "ID_CharacterInfo_Luminance_Base_All_Skills", None, 1),
];

/// Every augmentation phrase key, the luminance auras' and their header's
/// included.
pub fn augmentation_keys() -> impl Iterator<Item = &'static str> {
    AUGMENTATIONS
        .iter()
        .map(|a| a.key)
        .chain(LUMINANCE_AURAS.iter().map(|a| a.base_key))
        .chain(LUMINANCE_AURAS.iter().filter_map(|a| a.seer_key))
        .chain(std::iter::once(LUMINANCE_HEADER_KEY))
}

/// The report's phrases, each split where its values go.
#[derive(Debug, Clone, PartialEq)]
pub struct CharacterInfoStrings {
    /// "You were born on |."
    pub birth: Vec<String>,
    /// "You have played for |."
    pub played: Vec<String>,
    /// Never died.
    pub deaths_none: String,
    /// Died once.
    pub deaths_one: String,
    /// Died twice.
    pub deaths_two: String,
    /// "You have died | times."
    pub deaths_many: Vec<String>,
    /// Natural and drain resistances, regeneration.
    pub resists: Vec<String>,
    /// The six innate attributes.
    pub innates: Vec<String>,
    /// Chess rank.
    pub chess: Vec<String>,
    /// Fishing skill.
    pub fishing: Vec<String>,
    /// Not overburdened.
    pub load_none: String,
    /// Overburdened by | units ... |%.
    pub load_burdened: Vec<String>,
    /// Mule augmentations.
    pub load_augmentations: Vec<String>,
    /// Melee mastery.
    pub melee_mastery: Vec<String>,
    /// Ranged mastery.
    pub ranged_mastery: Vec<String>,
    /// Summoning mastery.
    pub summoning_mastery: Vec<String>,
    /// The augmentation phrases, by key.
    pub augmentations: HashMap<String, Vec<String>>,
}

fn owned(parts: &[&str]) -> Vec<String> {
    parts.iter().map(|s| (*s).to_owned()).collect()
}

impl CharacterInfoStrings {
    /// The phrases in English (what the report reads without the dats).
    #[must_use]
    pub fn english() -> Self {
        Self {
            birth: owned(&["You were born on ", ".\n"]),
            played: owned(&["You have played for ", ".\n"]),
            deaths_none: "You have never died!\n".to_owned(),
            deaths_one: "You have died only once!\n".to_owned(),
            deaths_two: "You have died twice.\n".to_owned(),
            deaths_many: owned(&["You have died ", " times.\n"]),
            resists: owned(&[
                "Natural Resistances: ",
                "\nDrain Resistances: ",
                "\nRegeneration Bonus: ",
                "\n",
            ]),
            innates: owned(&[
                "Innate Strength: ",
                "\nInnate Endurance: ",
                "\nInnate Coordination: ",
                "\nInnate Quickness: ",
                "\nInnate Focus: ",
                "\nInnate Self: ",
                "\n",
            ]),
            chess: owned(&["Chess Rank: ", "\n"]),
            fishing: owned(&["Fishing Skill: ", "\n"]),
            load_none: "You are not overburdened at this time.\n".to_owned(),
            load_burdened: owned(&[
                "You are currently overburdened by ",
                " burden units. This is reducing your Run, Jump, Melee Defense and Missile Defense skills by ",
                "%.\n",
            ]),
            load_augmentations: owned(&[
                "You have been augmented ",
                " times with the Might of the Seventh Mule. This has increased your carrying capacity by ",
                "%.\n",
            ]),
            melee_mastery: owned(&["Your melee mastery is ", ".\n"]),
            ranged_mastery: owned(&["Your ranged mastery is ", ".\n\n"]),
            summoning_mastery: owned(&["Your summoning mastery is ", ".\n\n"]),
            augmentations: HashMap::new(),
        }
    }

    /// The phrases the dats author (`resolve` gives a key's parts), the
    /// English ones where a key is missing.
    #[must_use]
    pub fn from_dat(resolve: &dyn Fn(&str) -> Option<Vec<String>>) -> Self {
        let e = Self::english();
        let get = |key: &str, fallback: Vec<String>| {
            resolve(key).filter(|v| !v.is_empty()).unwrap_or(fallback)
        };
        let one = |key: &str, fallback: String| get(key, vec![fallback]).remove(0);
        let augmentations = augmentation_keys()
            .filter_map(|k| {
                resolve(k)
                    .filter(|v| !v.is_empty())
                    .map(|v| (k.to_owned(), v))
            })
            .collect();
        Self {
            birth: get("ID_CharacterInfo_Birth", e.birth),
            played: get("ID_CharacterInfo_Played", e.played),
            deaths_none: one("ID_CharacterInfo_Deaths_None", e.deaths_none),
            deaths_one: one("ID_CharacterInfo_Deaths_One", e.deaths_one),
            deaths_two: one("ID_CharacterInfo_Deaths_Two", e.deaths_two),
            deaths_many: get("ID_CharacterInfo_Deaths_Many", e.deaths_many),
            resists: get("ID_CharacterInfo_Resists", e.resists),
            innates: get("ID_CharacterInfo_Innates", e.innates),
            chess: get("ID_CharacterInfo_Chess", e.chess),
            fishing: get("ID_CharacterInfo_Fishing", e.fishing),
            load_none: one("ID_CharacterInfo_Load_None", e.load_none),
            load_burdened: get("ID_CharacterInfo_Load_Burdened", e.load_burdened),
            load_augmentations: get("ID_CharacterInfo_Load_Augmentations", e.load_augmentations),
            melee_mastery: get("ID_CharacterInfo_Mastery_Melee", e.melee_mastery),
            ranged_mastery: get("ID_CharacterInfo_Mastery_Ranged", e.ranged_mastery),
            summoning_mastery: get("ID_CharacterInfo_Mastery_Summoning", e.summoning_mastery),
            augmentations,
        }
    }
}

/// What the report reads: the player's int properties and his attributes
/// (Strength .. Self) with spells, without them, and as he started.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CharacterInfoSource {
    /// The player's int properties.
    pub ints: BTreeMap<u32, i32>,
    /// Current attributes with spells, Strength .. Self (the burden).
    pub attributes: [i32; 6],
    /// Attributes without spells (as created plus raised), Strength ..
    /// Self (the resistance and regeneration grades).
    pub base: [i32; 6],
    /// Innate (starting) attributes, Strength .. Self.
    pub innate: [i32; 6],
    /// Formats a birth timestamp (Unix seconds) as a local date.
    pub birth_text: Option<String>,
}

impl CharacterInfoSource {
    fn int(&self, id: u32) -> i32 {
        self.ints.get(&id).copied().unwrap_or(0)
    }
}

/// The chess rank shown when the character has none.
pub const UNRANKED_CHESS_RANK: i32 = 1400;

/// An integer as the report writes every one: with thousands separators
/// ("1,400").
fn number(value: i32) -> String {
    crate::panels::appraisal::group_thousands(i64::from(value))
}

/// The grade of a resistance value against the game's inclusive bounds.
#[must_use]
pub fn resistance_grade(
    value: i32,
    none: i32,
    poor: i32,
    mediocre: i32,
    hardy: i32,
    resilient: i32,
) -> &'static str {
    if value <= none {
        "None"
    } else if value <= poor {
        "Poor"
    } else if value <= mediocre {
        "Mediocre"
    } else if value <= hardy {
        "Hardy"
    } else if value <= resilient {
        "Resilient"
    } else {
        "Indomitable"
    }
}

/// A play time as the game words it: years down to seconds, units left
/// out when zero ("1 month 1 week").
#[must_use]
pub fn format_retail_duration(total_seconds: i32) -> String {
    let mut remaining = i64::from(total_seconds.max(0));
    let units: [(i64, &str, &str); 7] = [
        (31_536_000, "year", "years"),
        (2_592_000, "month", "months"),
        (604_800, "week", "weeks"),
        (86_400, "day", "days"),
        (3_600, "hour", "hours"),
        (60, "minute", "minutes"),
        (1, "second", "seconds"),
    ];
    let mut parts = Vec::new();
    for (seconds, one, many) in units {
        let count = remaining / seconds;
        remaining %= seconds;
        if count != 0 {
            parts.push(format!("{count} {}", if count == 1 { one } else { many }));
        }
    }
    parts.join(" ")
}

/// Days since 1970-01-01 to a (year, month, day).
fn civil_from_days(days: i64) -> (i64, i64, i64) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let day_of_era = z.rem_euclid(146_097);
    let year_of_era =
        (day_of_era - day_of_era / 1460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_index = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * month_index + 2) / 5 + 1;
    let month = if month_index < 10 {
        month_index + 3
    } else {
        month_index - 9
    };
    let year = year_of_era + era * 400 + i64::from(month <= 2);
    (year, month, day)
}

/// A birth timestamp as a local date and time ("8/21/2026 12:00:00 PM",
/// the general date format).
#[must_use]
pub fn format_birth_date(unix_seconds: i32, utc_offset_seconds: i32) -> String {
    let local = i64::from(unix_seconds) + i64::from(utc_offset_seconds);
    let (year, month, day) = civil_from_days(local.div_euclid(86_400));
    let second_of_day = local.rem_euclid(86_400);
    let hour = second_of_day / 3600;
    let (h12, meridiem) = match hour {
        0 => (12, "AM"),
        1..=11 => (hour, "AM"),
        12 => (12, "PM"),
        _ => (hour - 12, "PM"),
    };
    format!(
        "{month}/{day}/{year} {h12}:{:02}:{:02} {meridiem}",
        second_of_day / 60 % 60,
        second_of_day % 60
    )
}

/// One value inserted between each two phrase parts (a value with no part
/// after it is dropped); "{time[1]|times}" in a part reads "time" for one
/// and "times" otherwise.
/// The luminance block: its header (always), then one line per aura owned,
/// a two-tier aura's ranks past five on a second, Seer's, line.
fn append_luminance_auras(
    body: &mut String,
    source: &CharacterInfoSource,
    strings: &CharacterInfoStrings,
) {
    if let Some(first) = strings
        .augmentations
        .get(LUMINANCE_HEADER_KEY)
        .and_then(|h| h.first())
    {
        body.push_str(first);
    }
    let line = |body: &mut String, key: &str, value: i32| {
        if let Some(tokens) = strings.augmentations.get(key) {
            append_tokens(body, tokens, &[number(value)], &[i64::from(value)]);
        }
    };
    for a in &LUMINANCE_AURAS {
        let mut ranks = source.int(a.property);
        if ranks <= 0 {
            continue;
        }
        let mut seer_ranks = 0;
        if a.seer_key.is_some() && ranks > LUMINANCE_BASE_RANK_CAP {
            seer_ranks = ranks - LUMINANCE_BASE_RANK_CAP;
            ranks = LUMINANCE_BASE_RANK_CAP;
        }
        line(body, a.base_key, ranks * a.points_per_rank);
        if let Some(seer) = a.seer_key.filter(|_| seer_ranks > 0) {
            line(body, seer, seer_ranks * a.points_per_rank);
        }
    }
}

fn append_tokens(body: &mut String, tokens: &[String], values: &[String], counts: &[i64]) {
    for (i, token) in tokens.iter().enumerate() {
        let plural = if counts.is_empty() {
            0
        } else {
            counts[i.min(counts.len() - 1)]
        };
        body.push_str(&resolve_plural(token, plural));
        if i + 1 < tokens.len()
            && let Some(v) = values.get(i)
        {
            body.push_str(v);
        }
    }
}

fn resolve_plural(text: &str, value: i64) -> String {
    text.replace("{time[1]|times}", if value == 1 { "time" } else { "times" })
}

fn section_break(body: &mut String) {
    if !body.is_empty() && !body.ends_with('\n') {
        body.push('\n');
    }
    body.push('\n');
}

fn melee_mastery_name(v: i32) -> &'static str {
    match v {
        1 => "Unarmed Weapons",
        2 => "Swords",
        3 => "Axes",
        4 => "Maces",
        5 => "Spears",
        6 => "Daggers",
        7 => "Staves",
        11 => "Two Handed Weapons",
        _ => "Unknown",
    }
}

fn ranged_mastery_name(v: i32) -> &'static str {
    match v {
        8 => "Bows",
        9 => "Crossbows",
        10 => "Thrown Weapons",
        12 => "Magical Spells",
        _ => "Unknown",
    }
}

fn summoning_mastery_name(v: i32) -> &'static str {
    match v {
        1 => "Primalist",
        2 => "Necromancer",
        3 => "Naturalist",
        _ => "Unknown",
    }
}

/// The report.
#[must_use]
pub fn build_report(source: &CharacterInfoSource, strings: &CharacterInfoStrings) -> String {
    let mut body = String::new();
    let one = |v: String| vec![v];
    if let Some(birth) = source.birth_text.as_ref().filter(|b| !b.is_empty()) {
        append_tokens(&mut body, &strings.birth, &one(birth.clone()), &[]);
    }
    if let Some(&played) = source.ints.get(&0x7D) {
        let text = format_retail_duration(played);
        if !text.is_empty() {
            append_tokens(&mut body, &strings.played, &one(text), &[]);
        }
    }
    let deaths = source.int(0x2B);
    match deaths {
        0 => body.push_str(&strings.deaths_none),
        1 => body.push_str(&strings.deaths_one),
        2 => body.push_str(&strings.deaths_two),
        _ => append_tokens(
            &mut body,
            &strings.deaths_many,
            &one(number(deaths)),
            &[i64::from(deaths)],
        ),
    }
    section_break(&mut body);

    // The grades read Strength and Endurance without spells.
    let [strength, endurance, ..] = source.base;
    let resist = resistance_grade(strength + endurance, 200, 260, 320, 380, 440);
    let regeneration = resistance_grade(strength + 2 * endurance, 200, 346, 470, 580, 690);
    let r = &strings.resists;
    let part = |i: usize| r.get(i).map_or("", String::as_str);
    body.push_str(part(0));
    body.push_str(resist);
    body.push_str(part(1));
    body.push_str(resist);
    body.push_str(part(2));
    body.push_str(regeneration);
    body.push_str(part(3));
    section_break(&mut body);

    let innate: Vec<String> = source.innate.iter().copied().map(number).collect();
    let counts: Vec<i64> = source.innate.iter().map(|v| i64::from(*v)).collect();
    append_tokens(&mut body, &strings.innates, &innate, &counts);
    section_break(&mut body);

    // A character who never played chess is ranked 1400.
    let chess = source
        .ints
        .get(&0xB5)
        .copied()
        .unwrap_or(UNRANKED_CHESS_RANK);
    append_tokens(
        &mut body,
        &strings.chess,
        &one(number(chess)),
        &[i64::from(chess)],
    );
    let fishing = source.int(0xC0);
    append_tokens(
        &mut body,
        &strings.fishing,
        &one(number(fishing)),
        &[i64::from(fishing)],
    );
    section_break(&mut body);

    for (tokens, id, name) in [
        (
            &strings.melee_mastery,
            0x162,
            melee_mastery_name as fn(i32) -> &'static str,
        ),
        (&strings.ranged_mastery, 0x163, ranged_mastery_name),
        (&strings.summoning_mastery, 0x16A, summoning_mastery_name),
    ] {
        let v = source.int(id);
        if v > 0 {
            append_tokens(&mut body, tokens, &one(name(v).to_owned()), &[]);
        }
    }
    append_luminance_auras(&mut body, source, strings);
    for a in &AUGMENTATIONS {
        let v = source.int(a.property);
        let Some(tokens) = (v > 0).then(|| strings.augmentations.get(a.key)).flatten() else {
            continue;
        };
        if a.include_value {
            append_tokens(&mut body, tokens, &one(number(v)), &[i64::from(v)]);
        } else if let Some(first) = tokens.first() {
            body.push_str(&resolve_plural(first, i64::from(v)));
        }
    }
    section_break(&mut body);

    let augmentations = source.int(0xE6);
    // The burden reads Strength with spells.
    let capacity = burden::encumbrance_capacity(source.attributes[0], augmentations);
    let current = source.int(5);
    let load = burden::load_ratio(capacity, current);
    if load < 1.0 {
        body.push_str(&strings.load_none);
    } else {
        let over = current - capacity;
        let penalty = burden::load_penalty_percent(load);
        append_tokens(
            &mut body,
            &strings.load_burdened,
            &[number(over), number(penalty)],
            &[i64::from(over), i64::from(penalty)],
        );
    }
    if augmentations > 0 {
        #[allow(clippy::cast_precision_loss)]
        let percent = augmentations as f32 * 20.0;
        append_tokens(
            &mut body,
            &strings.load_augmentations,
            &[number(augmentations), percent.to_string()],
            &[i64::from(augmentations), percent as i64],
        );
    }
    body.trim_end_matches(['\r', '\n']).to_owned()
}

/// The character information panel's controller.
#[derive(Debug)]
pub struct CharacterInfoController {
    text: ElementId,
    close: Option<ElementId>,
    shown: Option<(String, f32, u32)>,
    report: String,
    /// How many times the report was laid out (reuse check).
    pub layouts: u32,
}

impl CharacterInfoController {
    /// Bind to the imported slot: its text (one is added when the slot has
    /// none), scrollbar and close button.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        font: Option<std::sync::Arc<crate::UiFont>>,
    ) -> Self {
        let root = layout.root;
        let text = match layout
            .find(MAIN_TEXT_ID)
            .filter(|t| ui.widget::<Text>(*t).is_some())
        {
            Some(t) => {
                if let Some(w) = ui.widget_mut::<Text>(t)
                    && w.font.is_none()
                {
                    w.font = font;
                }
                t
            }
            None => {
                let (w, h) = ui.element(root).map_or((0.0, 0.0), |e| (e.width, e.height));
                let mut e = crate::tree::Element::at(
                    12.0,
                    44.0,
                    (w - 24.0).max(40.0),
                    (h - 56.0).max(40.0),
                );
                e.dat_element_id = MAIN_TEXT_ID;
                e.z_order = 1_000_000;
                Text::configure(&mut e);
                e.click_through = false;
                let mut t = Text::default();
                t.font = font;
                ui.add(root, e, t)
            }
        };
        if let Some(w) = ui.widget_mut::<Text>(text) {
            w.preserve_end_on_layout = false;
        }
        if let Some(bar) = layout.find(SCROLLBAR_ID)
            && let Some(s) = ui.widget_mut::<Scrollbar>(bar)
        {
            s.model = ScrollSource::Element(text);
        }
        let close = layout.find(CLOSE_ID).filter(|b| bind_click(ui, *b));
        Self {
            text,
            close,
            shown: None,
            report: String::new(),
            layouts: 0,
        }
    }

    /// The report text element.
    #[must_use]
    pub fn text(&self) -> ElementId {
        self.text
    }

    /// The report as last composed.
    #[must_use]
    pub fn report(&self) -> &str {
        &self.report
    }

    /// Compose the report from `source` and show it, wrapped to the text's
    /// width (laid out again only when the report, the width or the font
    /// changed).
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        source: &CharacterInfoSource,
        strings: &CharacterInfoStrings,
    ) {
        self.report = build_report(source, strings);
        let Some((width, padding, font, color)) = ui
            .widget::<Text>(self.text)
            .zip(ui.element(self.text))
            .map(|(t, e)| (e.width, t.padding, t.font.clone(), t.default_color))
        else {
            return;
        };
        let key = (
            self.report.clone(),
            width,
            font.as_ref().map_or(0, |f| f.id),
        );
        if self.shown.as_ref() == Some(&key) {
            return;
        }
        self.shown = Some(key);
        self.layouts += 1;
        let max_w = (width - 2.0 * padding).max(1.0);
        let measure = |s: &str| match &font {
            Some(f) => f.measure_width(s),
            None => s.chars().count() as f32 * 8.0,
        };
        let mut lines = Vec::new();
        for paragraph in self.report.split('\n') {
            if paragraph.is_empty() {
                lines.push(Line::new("", color));
                continue;
            }
            for l in wrap_text(paragraph, max_w, &measure) {
                lines.push(Line::new(l, color));
            }
        }
        if let Some(t) = ui.widget_mut::<Text>(self.text) {
            t.set_lines(lines);
        }
    }
}

impl PanelController for CharacterInfoController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if Some(element) == self.close {
            if matches!(message, ElementMessage::Clicked { .. }) {
                emit(ui, PanelOutput::ClosePanel(panel_id::CHARACTER_INFORMATION));
            }
            return true;
        }
        false
    }
}
