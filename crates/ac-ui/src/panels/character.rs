//! The character window (layout 0x2100002E): the
//! identity header (name, gender, heritage and title, PK status, level,
//! experience and the level meter, luminance), the attributes list (six
//! attributes and the three vitals) and the skills list (specialized,
//! trained, untrained and unusable groups), the footer that says what the
//! selected row costs, and the raise buttons that spend experience or skill
//! credits.
//!
//! The window shows a [`CharacterSheet`], a plain view model the client
//! builds from the local player's numbers ([`CharacterSheet::build`]).
//! Raises go out as [`PanelOutput::RaiseAttribute`] and its siblings; one
//! raise is in flight at a time, until the next sheet differs from the one
//! it was asked from.

use std::sync::Arc;

use ac_core::items::{PropertyBundle, PublicWeenieFlags};
use ac_core::player::{AttributeKind, LocalPlayerState, VitalKind, skill_names};
use ac_dat::objects::SkillTable;

use crate::draw::{Color, RenderContext};
use crate::event::{UiEvent, kind};
use crate::geometry::AnchorEdges;
use crate::host::PanelController;
use crate::import::ImportedLayout;
use crate::panels::PanelOutput;
use crate::panels::items::{bind_click, emit};
use crate::root::{ElementMessage, UiRoot};
use crate::states;
use crate::text::{Line, TextRun};
use crate::tooltip::{SHARED_POPUP_SKIN_LAYOUT_DID, SHARED_POPUP_SKIN_ROOT_ELEMENT_ID};
use crate::tree::{Element, ElementId, Widget};
use crate::widgets::item_slot::stretch;
use crate::widgets::{Button, ListBox, Meter, ScrollSource, ScrollablePanel, Scrollbar, Text};

/// The character window's layout.
pub const LAYOUT_ID: u32 = 0x2100_002E;
/// Name.
pub const NAME_ID: u32 = 0x1000_0231;
/// Gender, heritage and title.
pub const HERITAGE_ID: u32 = 0x1000_0232;
/// PK status.
pub const PK_STATUS_ID: u32 = 0x1000_0233;
/// "Character Level" caption.
pub const LEVEL_CAPTION_ID: u32 = 0x1000_023A;
/// Level.
pub const LEVEL_ID: u32 = 0x1000_023B;
/// "Total Experience (XP):".
pub const TOTAL_XP_LABEL_ID: u32 = 0x1000_0234;
/// Total experience.
pub const TOTAL_XP_ID: u32 = 0x1000_0235;
/// The level meter.
pub const XP_METER_ID: u32 = 0x1000_0236;
/// "XP for next level:".
pub const XP_NEXT_LABEL_ID: u32 = 0x1000_0237;
/// Experience to the next level.
pub const XP_NEXT_VALUE_ID: u32 = 0x1000_0238;
/// The stat list.
pub const LIST_BOX_ID: u32 = 0x1000_023D;
/// The stat list's scrollbar.
pub const LIST_SCROLLBAR_ID: u32 = 0x1000_023E;
/// The divider above the footer.
pub const LIST_DIVIDER_ID: u32 = 0x1000_023F;
/// "Luminance:".
pub const LUMINANCE_LABEL_ID: u32 = 0x1000_05C5;
/// Luminance.
pub const LUMINANCE_VALUE_ID: u32 = 0x1000_05C6;
/// Footer while nothing is selected.
pub const FOOTER_STATE_A_ID: u32 = 0x1000_0240;
/// Footer while a row is selected.
pub const FOOTER_STATE_B_ID: u32 = 0x1000_0241;
/// The third footer (never shown here).
pub const FOOTER_STATE_C_ID: u32 = 0x1000_0247;
/// The attributes tab.
pub const TAB_ATTRIBUTES_ID: u32 = 0x1000_0228;
/// The skills tab.
pub const TAB_SKILLS_ID: u32 = 0x1000_0229;
/// The titles tab.
pub const TAB_TITLES_ID: u32 = 0x1000_0538;
/// The attributes page (it also shows the skills list).
pub const ATTRIBUTES_PAGE_ID: u32 = 0x1000_022B;
/// The skills page (never shown: the attributes page serves both).
pub const SKILLS_PAGE_ID: u32 = 0x1000_022C;
/// The titles page.
pub const TITLES_PAGE_ID: u32 = 0x1000_0539;
/// Footer title.
pub const FOOTER_TITLE_ID: u32 = 0x1000_024E;
/// Footer line one caption.
pub const FOOTER_LINE1_LABEL: u32 = 0x1000_0242;
/// Footer line one value.
pub const FOOTER_LINE1_VALUE: u32 = 0x1000_0243;
/// Footer line two caption.
pub const FOOTER_LINE2_LABEL: u32 = 0x1000_0244;
/// Footer line two value.
pub const FOOTER_LINE2_VALUE: u32 = 0x1000_0245;
/// Raise by one.
pub const RAISE_ONE_ID: u32 = 0x1000_0246;
/// Raise by ten.
pub const RAISE_TEN_ID: u32 = 0x1000_05EB;
/// The window's close button.
pub const CLOSE_BUTTON_ID: u32 = 0x1000_022A;

/// Parchment-white body text.
pub const BODY: Color = Color::rgba(0.92, 0.90, 0.82, 1.0);
/// A buffed value.
pub const BUFF_GREEN: Color = Color::rgba(0.0, 1.0, 0.0, 1.0);
/// A debuffed value.
pub const DEBUFF_RED: Color = Color::rgba(1.0, 0.0, 0.0, 1.0);
/// A vitae penalty.
pub const VITAE_BLUE: Color = Color::rgba(127.0 / 255.0, 1.0, 1.0, 1.0);

/// Row height, pixels.
pub const ROW_HEIGHT: f32 = 20.0;
const ROW_ICON_SIZE: f32 = 20.0;
const ROW_NAME_X: f32 = 25.0;
const ROW_NAME_W: f32 = 150.0;
const ROW_VALUE_X: f32 = 175.0;
const ROW_VALUE_W: f32 = 100.0;
const ROW_PAD_X: f32 = 4.0;
/// The widest row, pixels.
pub const SKILL_CONTENT_WIDTH: f32 = 282.0;
/// Specialized group header.
pub const SKILL_HEADER_SPECIALIZED_SPRITE: u32 = 0x0600_0F90;
/// Trained group header.
pub const SKILL_HEADER_TRAINED_SPRITE: u32 = 0x0600_0F86;
/// Untrained group header.
pub const SKILL_HEADER_UNTRAINED_SPRITE: u32 = 0x0600_0F98;
/// Unusable group header.
pub const SKILL_HEADER_UNUSABLE_SPRITE: u32 = 0x0600_0F89;
/// A selected row.
pub const ROW_HIGHLIGHT_SPRITE: u32 = 0x0600_0F93;
/// An unselected row (black at 69%).
pub const ROW_NORMAL_SPRITE: u32 = 0x0600_4CC2;
/// "Luminance:".
pub const LUMINANCE_CAPTION: &str = "Luminance:";

/// The attribute rows: name, icon, attribute id.
pub const ATTRIBUTE_ROWS: [(&str, u32, u32); 6] = [
    ("Strength", 0x0600_02C8, 1),
    ("Endurance", 0x0600_02C4, 2),
    ("Coordination", 0x0600_02C9, 4),
    ("Quickness", 0x0600_02C6, 3),
    ("Focus", 0x0600_02C5, 5),
    ("Self", 0x0600_02C7, 6),
];
/// The vital rows: name, icon, the vital's maximum id.
pub const VITAL_ROWS: [(&str, u32, u32); 3] = [
    ("Health", 0x0600_4C3B, 1),
    ("Stamina", 0x0600_4C3C, 3),
    ("Mana", 0x0600_4C3D, 5),
];

fn attribute_description(id: u32) -> Option<&'static str> {
    Some(match id {
        1 => "Measures your character's muscular power.",
        2 => "Measures how healthy your character is.",
        3 => "Measures how fast your character is.",
        4 => "Measures your character's reflexes",
        5 => "Measures your character's mind and senses.",
        6 => "Measures your character's willpower.",
        _ => return None,
    })
}

fn vital_description(id: u32) -> Option<&'static str> {
    Some(match id {
        1 => "(Endurance/2)\nIf you run out of health, you will die!",
        3 => "(Endurance)\nAffects your actions and movement.",
        5 => "(Self)\nAffects how much magic you can cast.",
        _ => return None,
    })
}

// ── The view model ──────────────────────────────────────────────────────

/// How far a skill is advanced.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default)]
pub enum SkillAdvancement {
    /// Not shown.
    #[default]
    Inactive,
    /// Untrained.
    Untrained,
    /// Trained.
    Trained,
    /// Specialized.
    Specialized,
}

impl SkillAdvancement {
    /// From the skill record's status (1 untrained, 2 trained, 3
    /// specialized).
    #[must_use]
    pub fn from_status(status: u32) -> Self {
        match status {
            1 => Self::Untrained,
            2 => Self::Trained,
            3 => Self::Specialized,
            _ => Self::Inactive,
        }
    }

    /// Trained or specialized.
    #[must_use]
    pub fn is_trained(self) -> bool {
        self >= Self::Trained
    }
}

/// One skill on the sheet.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CharacterSkill {
    /// Skill id.
    pub id: u32,
    /// Name.
    pub name: String,
    /// Icon.
    pub icon: u32,
    /// Advancement.
    pub advancement: SkillAdvancement,
    /// Level without spells.
    pub base_level: i32,
    /// Level with spells and vitae.
    pub current_level: i32,
    /// Usable while untrained.
    pub usable_untrained: bool,
    /// Credits to train.
    pub trained_cost: i32,
    /// Credits to specialize.
    pub specialized_cost: i32,
    /// Experience to raise by one (0: at the top).
    pub raise_cost: u64,
    /// Experience to raise by ten.
    pub raise10_cost: u64,
    /// What vitae takes off (zero or negative).
    pub vitae_modifier: i32,
    /// Hover text (the formula and the description).
    pub tooltip: Option<String>,
}

/// The character's numbers as the window shows them.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CharacterSheet {
    /// Name.
    pub name: String,
    /// Level (`None`: "???").
    pub level: Option<i32>,
    /// "Female", "Male".
    pub gender: Option<String>,
    /// "Aluvian", ...
    pub heritage: Option<String>,
    /// "the Adventurer", ...
    pub title: Option<String>,
    /// The PK status line.
    pub pk_status: Option<String>,
    /// Total experience.
    pub total_xp: u64,
    /// Experience to the next level.
    pub xp_to_next_level: u64,
    /// What stands for the number at the top of the level table.
    pub xp_to_next_level_text: Option<String>,
    /// The level meter's fill, 0..=1.
    pub xp_fraction: f32,
    /// Luminance available.
    pub available_luminance: u64,
    /// Luminance at most.
    pub maximum_luminance: u64,
    /// Current health, stamina, mana.
    pub vital_current: [i32; 3],
    /// Maximum health, stamina, mana (with spells and vitae).
    pub vital_max: [i32; 3],
    /// Maximum without spells.
    pub vital_base_max: [i32; 3],
    /// What vitae takes off each maximum.
    pub vital_vitae: [i32; 3],
    /// Attributes with spells: strength, endurance, coordination,
    /// quickness, focus, self (the rows' order).
    pub attributes: [i32; 6],
    /// Attributes without spells, same order.
    pub attribute_base: [i32; 6],
    /// Attributes as created, same order.
    pub attribute_innate: [i32; 6],
    /// Unspent skill credits.
    pub skill_credits: i32,
    /// Unassigned experience.
    pub unassigned_xp: u64,
    /// Experience to raise each attribute row by one (the six, then health,
    /// stamina, mana; 0 at the top).
    pub attribute_raise_costs: [u64; 9],
    /// By ten.
    pub attribute_raise10_costs: [u64; 9],
    /// The skills.
    pub skills: Vec<CharacterSkill>,
}

/// The dat's experience curves (ExperienceTable 0x0E000018): cumulative
/// experience per level and per rank.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ExperienceCurves {
    /// Total experience at each level.
    pub levels: Vec<u64>,
    /// Experience spent at each attribute rank.
    pub attributes: Vec<u32>,
    /// At each vital rank.
    pub vitals: Vec<u32>,
    /// At each trained skill rank.
    pub trained_skills: Vec<u32>,
    /// At each specialized skill rank.
    pub specialized_skills: Vec<u32>,
}

impl From<&ac_dat::objects::ExperienceTable> for ExperienceCurves {
    fn from(table: &ac_dat::objects::ExperienceTable) -> Self {
        Self {
            levels: table.levels.clone(),
            attributes: table.attributes.clone(),
            vitals: table.vitals.clone(),
            trained_skills: table.trained_skills.clone(),
            specialized_skills: table.specialized_skills.clone(),
        }
    }
}

/// Experience to raise `amount` ranks from `ranks` with `spent` already
/// spent: the curve at the target rank (at most the last) less what was
/// spent; 0 at the top or past the curve.
#[must_use]
pub fn raise_cost_from_curve(curve: &[u32], ranks: u32, spent: u32, amount: u32) -> u64 {
    if curve.is_empty() || amount == 0 {
        return 0;
    }
    let max_index = curve.len() as u64 - 1;
    if max_index <= u64::from(ranks) {
        return 0;
    }
    let target = (u64::from(ranks) + u64::from(amount)).min(max_index);
    u64::from(curve[target as usize]).saturating_sub(u64::from(spent))
}

/// Experience to the next level, the meter's fill, and whether there is no
/// next level (the top of the table, or nothing left to earn).
#[must_use]
pub fn level_progress(levels: &[u64], level: i32, total_xp: u64) -> (u64, f32, bool) {
    // An empty curve is no table at all (the meter shows nothing).
    if levels.is_empty() {
        return (0, 0.0, false);
    }
    let total = i64::try_from(total_xp).unwrap_or(i64::MAX);
    ac_core::player::LevelProgress::measure(Some(levels), level, total)
        .map_or((0, 0.0, false), |p| {
            (p.to_next.cast_unsigned(), p.fraction, p.no_next_level)
        })
}

/// "Male", "Female" for the gender property.
#[must_use]
pub fn gender_name(gender: i32) -> Option<&'static str> {
    match gender {
        1 => Some("Male"),
        2 => Some("Female"),
        _ => None,
    }
}

/// The heritage group's name.
#[must_use]
pub fn heritage_name(heritage: i32) -> Option<&'static str> {
    Some(match heritage {
        1 => "Aluvian",
        2 => "Gharu'ndim",
        3 => "Sho",
        4 => "Viamontian",
        5 => "Umbraen",
        6 => "Gearknight",
        7 => "Tumerok",
        8 => "Lugian",
        9 => "Empyrean",
        10 => "Penumbraen",
        11 => "Undead",
        12 | 13 => "Olthoi",
        _ => return None,
    })
}

/// The header's second line: gender, heritage and title, joined by
/// spaces, the title verbatim.
#[must_use]
pub fn stat_header_line(sheet: &CharacterSheet) -> String {
    [&sheet.gender, &sheet.heritage, &sheet.title]
        .into_iter()
        .filter_map(|p| p.as_deref().map(str::trim).filter(|p| !p.is_empty()))
        .collect::<Vec<_>>()
        .join(" ")
}

/// A count with thousands separators ("1,234,567").
#[must_use]
pub fn format_xp(value: u64) -> String {
    let digits = value.to_string();
    let mut out = String::with_capacity(digits.len() + digits.len() / 3);
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(c);
    }
    out
}

/// Looks up a UI string by its string-table key.
pub type UiStrings<'a> = &'a dyn Fn(&str) -> Option<String>;

/// The skills' ids and groups in list order.
pub type SkillLayout = Vec<(u32, SkillAdvancement, bool)>;

/// What the client reads a sheet from.
#[derive(Clone, Copy)]
pub struct SheetSource<'a> {
    /// The local player's numbers.
    pub player: &'a LocalPlayerState,
    /// The player object's properties (level, experience, credits, ...).
    pub properties: &'a PropertyBundle,
    /// The player object's public description bits (PK status).
    pub pwd_bitfield: u32,
    /// The character's name (the allegiance rank title is put before
    /// it from the properties).
    pub name: &'a str,
    /// The displayed title.
    pub title: Option<&'a str>,
    /// The dat's experience curves.
    pub experience: Option<&'a ExperienceCurves>,
    /// The dat's skill table.
    pub skill_table: Option<&'a SkillTable>,
    /// Looks up a UI string by its string-table key
    /// (`ID_StatManagement_Header_PKStatus_*`, the infinity text).
    pub ui_string: Option<UiStrings<'a>>,
}

impl std::fmt::Debug for SheetSource<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SheetSource")
            .field("name", &self.name)
            .finish_non_exhaustive()
    }
}

const ROW_ATTRIBUTES: [AttributeKind; 6] = [
    AttributeKind::Strength,
    AttributeKind::Endurance,
    AttributeKind::Coordination,
    AttributeKind::Quickness,
    AttributeKind::Focus,
    AttributeKind::Self_,
];

fn sat(v: u32) -> i32 {
    i32::try_from(v).unwrap_or(i32::MAX)
}

impl CharacterSheet {
    /// The sheet of the local player.
    #[must_use]
    pub fn build(src: &SheetSource<'_>) -> Self {
        let p = src.player;
        let props = src.properties;
        let level = props.ints.get(&0x19).copied();
        let total_xp = props.int64s.get(&1).copied().unwrap_or(0).max(0) as u64;
        let unassigned_xp = props.int64s.get(&2).copied().unwrap_or(0).max(0) as u64;
        let (xp_to_next_level, xp_fraction, no_next) =
            src.experience.map_or((0, 0.0, false), |x| {
                level_progress(&x.levels, level.unwrap_or(0), total_xp)
            });
        let ui = |key: &str| src.ui_string.and_then(|f| f(key));
        let flags = PublicWeenieFlags(src.pwd_bitfield);
        let pk_key = if flags.contains(PublicWeenieFlags::PLAYER_KILLER) {
            "ID_StatManagement_Header_PKStatus_PK"
        } else if flags.contains(PublicWeenieFlags::PLAYER_KILLER_LITE) {
            "ID_StatManagement_Header_PKStatus_PKL"
        } else {
            "ID_StatManagement_Header_PKStatus_NPK"
        };
        let vitals = VitalKind::ALL;
        let attr_current = |k| p.get_attribute(k).map_or(0, |a| sat(a.current()));
        let attr_raise = |amount| {
            let mut costs = [0; 9];
            if let Some(x) = src.experience {
                for (i, k) in ROW_ATTRIBUTES.iter().enumerate() {
                    if let Some(a) = p.get_attribute(*k) {
                        costs[i] = raise_cost_from_curve(&x.attributes, a.ranks, a.xp, amount);
                    }
                }
                for (i, k) in vitals.iter().enumerate() {
                    if let Some(v) = p.get(*k) {
                        costs[6 + i] = raise_cost_from_curve(&x.vitals, v.ranks, v.xp, amount);
                    }
                }
            }
            costs
        };
        let mut skills = Vec::new();
        for s in p.skills().values() {
            let advancement = SkillAdvancement::from_status(s.status);
            if advancement == SkillAdvancement::Inactive {
                continue;
            }
            let base = src
                .skill_table
                .and_then(|t| t.skills.iter().find(|(id, _)| *id == s.skill_id))
                .map(|(_, b)| b);
            let name = base
                .map(|b| b.name.clone())
                .filter(|n| !n.trim().is_empty())
                .unwrap_or_else(|| skill_names::describe(s.skill_id as i32));
            let curve = src.experience.map(|x| {
                if advancement == SkillAdvancement::Specialized {
                    &x.specialized_skills
                } else {
                    &x.trained_skills
                }
            });
            let cost =
                |amount| curve.map_or(0, |c| raise_cost_from_curve(c, s.ranks, s.xp, amount));
            let values = p.get_skill_value(s.skill_id, None);
            let tooltip = base.and_then(|b| {
                ac_core::player::build_tooltip(&ac_core::player::SkillBase {
                    name: b.name.clone(),
                    description: b.description.clone(),
                    min_level: b.min_level,
                    trained_cost: b.trained_cost,
                    specialized_cost: b.specialized_cost,
                    formula: ac_core::player::SkillFormula {
                        additive_bonus: b.formula.additive_bonus,
                        attribute1_multiplier: b.formula.attribute1_multiplier,
                        attribute2_multiplier: b.formula.attribute2_multiplier,
                        divisor: b.formula.divisor,
                        attribute1: b.formula.attribute1,
                        attribute2: b.formula.attribute2,
                    },
                })
            });
            let level = sat(s.current_level());
            skills.push(CharacterSkill {
                id: s.skill_id,
                name,
                icon: base.map_or(0, |b| b.icon),
                advancement,
                base_level: values.map_or(level, |v| v.unenchanted_level),
                current_level: values.map_or(level, |v| v.effective_level),
                usable_untrained: !matches!(s.skill_id, 18 | 37 | 38 | 39 | 40),
                trained_cost: base.map_or(0, |b| b.trained_cost),
                specialized_cost: base.map_or(0, |b| b.specialized_cost),
                raise_cost: cost(1),
                raise10_cost: cost(10),
                vitae_modifier: values.map_or(0, |v| v.vitae_modifier),
                tooltip,
            });
        }
        skills.sort_by_key(|s| s.id);
        let to_i32 = |v: Option<u32>| v.map_or(0, sat);
        Self {
            // The allegiance rank title goes before the name (OpenAC
            // `CharacterSheetProvider.BuildSheet`, `AllegianceRankTitleTable`).
            name: super::social::rank_titles::compose_full_name(
                props.get_int(0x1E, 0),
                props.get_int(0xBC, 0),
                props.get_int(0x71, 0),
                src.name,
            ),
            level,
            gender: gender_name(props.get_int(0x71, 0)).map(str::to_owned),
            heritage: heritage_name(props.get_int(0xBC, 0)).map(str::to_owned),
            title: src.title.map(str::to_owned),
            pk_status: ui(pk_key),
            total_xp,
            xp_to_next_level,
            xp_to_next_level_text: no_next.then(|| {
                ui("ID_StatManagement_Header_XPToLevelMeterInfinity")
                    .unwrap_or_else(|| "Infinity!".to_owned())
            }),
            xp_fraction,
            available_luminance: props.get_int64(6, 0).max(0) as u64,
            maximum_luminance: props.get_int64(7, 0).max(0) as u64,
            vital_current: vitals.map(|k| to_i32(p.get(k).map(|v| v.current))),
            vital_max: vitals.map(|k| to_i32(p.get_max_approx(k))),
            vital_base_max: vitals.map(|k| to_i32(p.get_base_max_approx(k))),
            vital_vitae: vitals.map(|k| p.get_vital_vitae_modifier(k)),
            attributes: ROW_ATTRIBUTES.map(|k| p.get_effective_attribute(k).unwrap_or(0)),
            attribute_base: ROW_ATTRIBUTES.map(attr_current),
            attribute_innate: ROW_ATTRIBUTES
                .map(|k| p.get_attribute(k).map_or(0, |a| sat(a.start))),
            skill_credits: props.get_int(0x18, 0),
            unassigned_xp,
            attribute_raise_costs: attr_raise(1),
            attribute_raise10_costs: attr_raise(10),
            skills,
        }
    }

    /// The skill `id`.
    #[must_use]
    pub fn skill(&self, id: u32) -> Option<&CharacterSkill> {
        self.skills.iter().find(|s| s.id == id)
    }

    /// The luminance pair shows (level 200 and up with a maximum).
    #[must_use]
    pub fn luminance_visible(&self) -> bool {
        self.level.is_some_and(|l| l >= 200) && self.maximum_luminance != 0
    }
}

/// The skills of a group, by name: specialized, trained, or untrained
/// (usable or not).
#[must_use]
pub fn ordered_skills(
    sheet: &CharacterSheet,
    advancement: SkillAdvancement,
    usable_untrained: Option<bool>,
) -> Vec<&CharacterSkill> {
    let mut v: Vec<&CharacterSkill> = sheet
        .skills
        .iter()
        .filter(|s| s.advancement == advancement)
        .filter(|s| usable_untrained.is_none_or(|u| s.usable_untrained == u))
        .collect();
    v.sort_by(|a, b| a.name.cmp(&b.name));
    v
}

/// The four skill groups as the list shows them: (title, header image,
/// skills).
#[must_use]
pub fn skill_groups(sheet: &CharacterSheet) -> [(&'static str, u32, Vec<&CharacterSkill>); 4] {
    [
        (
            "Specialized Skills",
            SKILL_HEADER_SPECIALIZED_SPRITE,
            ordered_skills(sheet, SkillAdvancement::Specialized, None),
        ),
        (
            "Trained Skills",
            SKILL_HEADER_TRAINED_SPRITE,
            ordered_skills(sheet, SkillAdvancement::Trained, None),
        ),
        (
            "Untrained Skills",
            SKILL_HEADER_UNTRAINED_SPRITE,
            ordered_skills(sheet, SkillAdvancement::Untrained, Some(true)),
        ),
        (
            "Unusable Skills",
            SKILL_HEADER_UNUSABLE_SPRITE,
            ordered_skills(sheet, SkillAdvancement::Untrained, Some(false)),
        ),
    ]
}

/// The skill ids and groups in list order (what decides whether the list
/// must be rebuilt).
#[must_use]
pub fn skill_layout(sheet: &CharacterSheet) -> SkillLayout {
    skill_groups(sheet)
        .into_iter()
        .flat_map(|(_, _, skills)| {
            skills
                .into_iter()
                .map(|s| (s.id, s.advancement, s.usable_untrained))
        })
        .collect()
}

/// Row `index`'s name (attributes then vitals; "" past them).
#[must_use]
pub fn row_name(index: usize) -> &'static str {
    ATTRIBUTE_ROWS
        .get(index)
        .map(|r| r.0)
        .or_else(|| VITAL_ROWS.get(index.wrapping_sub(6)).map(|r| r.0))
        .unwrap_or("")
}

/// Row `index`'s value text.
#[must_use]
pub fn row_value(sheet: &CharacterSheet, index: usize) -> String {
    match index {
        0..=5 => sheet.attributes[index].to_string(),
        6..=8 => format!(
            "{}/{}",
            sheet.vital_current[index - 6],
            sheet.vital_max[index - 6]
        ),
        _ => String::new(),
    }
}

/// An attribute row's spell delta (effective less base; 0 for vitals).
#[must_use]
pub fn attribute_delta(sheet: &CharacterSheet, index: usize) -> i32 {
    if index < 6 {
        sheet.attributes[index] - sheet.attribute_base[index]
    } else {
        0
    }
}

/// A vital's spell delta (maximum less vitae less the unenchanted
/// maximum).
#[must_use]
pub fn vital_buff_delta(sheet: &CharacterSheet, vital: usize) -> i32 {
    if vital >= 3 {
        return 0;
    }
    sheet.vital_max[vital] - sheet.vital_vitae[vital] - sheet.vital_base_max[vital]
}

/// A skill's spell delta, vitae taken out.
#[must_use]
pub fn skill_buff_delta(skill: &CharacterSkill) -> i32 {
    skill.current_level - skill.vitae_modifier - skill.base_level
}

fn delta_color(delta: i32) -> Color {
    match delta.signum() {
        1 => BUFF_GREEN,
        -1 => DEBUFF_RED,
        _ => Color::WHITE,
    }
}

/// A skill value's colour: green buffed, red debuffed, white otherwise
/// (vitae does not count).
#[must_use]
pub fn skill_value_color(skill: &CharacterSkill) -> Color {
    delta_color(skill_buff_delta(skill))
}

/// An attribute row value's colour.
#[must_use]
pub fn attribute_value_color(sheet: &CharacterSheet, index: usize) -> Color {
    if index < 6 {
        delta_color(attribute_delta(sheet, index))
    } else {
        delta_color(vital_buff_delta(sheet, index - 6))
    }
}

/// Raise cost of attribute row `index` by `amount` (1 or 10).
#[must_use]
pub fn raise_cost(sheet: &CharacterSheet, index: usize, amount: u32) -> u64 {
    let costs = if amount == 10 {
        &sheet.attribute_raise10_costs
    } else {
        &sheet.attribute_raise_costs
    };
    costs.get(index).copied().unwrap_or(0)
}

fn format_buff_delta(delta: i32) -> String {
    match delta {
        0 => String::new(),
        d if d > 0 => format!(" (+{d})"),
        d => format!(" ({d})"),
    }
}

fn format_vitae_delta(vitae: i32) -> String {
    if vitae < 0 {
        format!(" ({vitae})")
    } else {
        String::new()
    }
}

/// The window's tabs.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum CharacterTab {
    /// Attributes and vitals.
    #[default]
    Attributes,
    /// Skills.
    Skills,
    /// Titles.
    Titles,
}

/// What is selected on a tab: an attribute row, or a skill.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Selection {
    /// The attribute row (0..9).
    pub attribute: Option<usize>,
    /// The skill id.
    pub skill: Option<u32>,
}

/// The footer title's runs: the selected row and its deltas (palette
/// colours 1 buff, 2 debuff, 3 vitae), or the prompt.
#[must_use]
pub fn footer_title_runs(
    tab: CharacterTab,
    sheet: &CharacterSheet,
    sel: Selection,
    palette: &[Color],
) -> Vec<TextRun> {
    let color = |i: usize| {
        palette.get(i).copied().unwrap_or(match i {
            1 => BUFF_GREEN,
            2 => DEBUFF_RED,
            3 => VITAE_BLUE,
            _ => Color::WHITE,
        })
    };
    if tab == CharacterTab::Skills {
        let Some(skill) = sel.skill.and_then(|id| sheet.skill(id)) else {
            return vec![TextRun::new("Select a Skill to Improve", BODY)];
        };
        if !skill.advancement.is_trained() {
            return vec![TextRun::new(skill.name.clone(), color(0))];
        }
        let mut runs = vec![TextRun::new(
            format!("{}: {}", skill.name, skill.current_level),
            color(0),
        )];
        if skill.vitae_modifier < 0 {
            runs.push(TextRun::new(
                format_vitae_delta(skill.vitae_modifier),
                color(3),
            ));
        }
        let d = skill_buff_delta(skill);
        if d != 0 {
            runs.push(TextRun::new(
                format_buff_delta(d),
                color(if d > 0 { 1 } else { 2 }),
            ));
        }
        return runs;
    }
    let Some(i) = sel.attribute else {
        return vec![TextRun::new("Select an Attribute to Improve", BODY)];
    };
    let mut runs = vec![TextRun::new(
        format!("{}: {}", row_name(i), row_value(sheet, i)),
        color(0),
    )];
    // A vital's vitae penalty comes before its spell delta, as a skill's
    // does.
    let vitae = i
        .checked_sub(6)
        .and_then(|vital| sheet.vital_vitae.get(vital))
        .copied()
        .unwrap_or(0);
    if vitae < 0 {
        runs.push(TextRun::new(format_vitae_delta(vitae), color(3)));
    }
    let d = if i < 6 {
        attribute_delta(sheet, i)
    } else {
        vital_buff_delta(sheet, i - 6)
    };
    if d != 0 {
        runs.push(TextRun::new(
            format_buff_delta(d),
            color(if d > 0 { 1 } else { 2 }),
        ));
    }
    runs
}

/// The footer title as one string.
#[must_use]
pub fn footer_title(tab: CharacterTab, sheet: &CharacterSheet, sel: Selection) -> String {
    footer_title_runs(tab, sheet, sel, &[])
        .into_iter()
        .map(|r| r.text)
        .collect()
}

/// The footer's two lines: (line 1 caption, value, line 2 caption, value).
#[must_use]
pub fn footer_lines(
    tab: CharacterTab,
    sheet: &CharacterSheet,
    sel: Selection,
) -> (&'static str, String, &'static str, String) {
    let cost_text = |c: u64| {
        if c > 0 {
            format_xp(c)
        } else {
            "Infinity!".to_owned()
        }
    };
    if tab == CharacterTab::Skills {
        return match sel.skill.and_then(|id| sheet.skill(id)) {
            None => (
                "Skill Credits Available:",
                sheet.skill_credits.to_string(),
                "Unassigned Experience:",
                format_xp(sheet.unassigned_xp),
            ),
            Some(s) if s.advancement.is_trained() => (
                "Experience To Raise:",
                cost_text(s.raise_cost),
                "Unassigned Experience:",
                format_xp(sheet.unassigned_xp),
            ),
            Some(s) => (
                "Skill Credits To Raise:",
                cost_text(u64::try_from(s.trained_cost).unwrap_or(0)),
                "Skill Credits Available:",
                sheet.skill_credits.to_string(),
            ),
        };
    }
    match sel.attribute {
        None => (
            "Skill Credits Available:",
            sheet.skill_credits.to_string(),
            "Unassigned Experience:",
            format_xp(sheet.unassigned_xp),
        ),
        Some(i) => (
            "Experience To Raise:",
            cost_text(raise_cost(sheet, i, 1)),
            "Unassigned Experience:",
            format_xp(sheet.unassigned_xp),
        ),
    }
}

/// A raise the window asks for.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RaiseRequest {
    /// An attribute (id 1..=6).
    Attribute {
        /// Attribute id.
        id: u32,
        /// Experience.
        cost: u64,
    },
    /// A vital's maximum (1, 3, 5).
    Vital {
        /// Vital id.
        id: u32,
        /// Experience.
        cost: u64,
    },
    /// A trained skill.
    Skill {
        /// Skill id.
        id: u32,
        /// Experience.
        cost: u64,
    },
    /// Train an untrained skill with credits.
    Train {
        /// Skill id.
        id: u32,
        /// Credits.
        credits: u32,
    },
}

impl RaiseRequest {
    fn output(self) -> PanelOutput {
        match self {
            Self::Attribute { id, cost } => PanelOutput::RaiseAttribute {
                attribute: id,
                xp: cost,
            },
            Self::Vital { id, cost } => PanelOutput::RaiseVital {
                vital: id,
                xp: cost,
            },
            Self::Skill { id, cost } => PanelOutput::RaiseSkill {
                skill: id,
                xp: cost,
            },
            Self::Train { id, credits } => PanelOutput::TrainSkill { skill: id, credits },
        }
    }
}

/// The raise a button asks for, when it is affordable.
#[must_use]
pub fn raise_request(
    tab: CharacterTab,
    sheet: &CharacterSheet,
    sel: Selection,
    amount: u32,
) -> Option<RaiseRequest> {
    if tab == CharacterTab::Skills {
        let skill = sel.skill.and_then(|id| sheet.skill(id))?;
        if !skill.advancement.is_trained() {
            let credits = u32::try_from(skill.trained_cost).ok()?;
            return (amount == 1
                && credits > 0
                && i64::from(sheet.skill_credits) >= i64::from(credits))
            .then_some(RaiseRequest::Train {
                id: skill.id,
                credits,
            });
        }
        let cost = if amount == 10 {
            skill.raise10_cost
        } else {
            skill.raise_cost
        };
        return (cost > 0 && sheet.unassigned_xp >= cost)
            .then_some(RaiseRequest::Skill { id: skill.id, cost });
    }
    let i = sel.attribute?;
    let cost = raise_cost(sheet, i, amount);
    if cost == 0 || sheet.unassigned_xp < cost {
        return None;
    }
    if i < 6 {
        Some(RaiseRequest::Attribute {
            id: ATTRIBUTE_ROWS[i].2,
            cost,
        })
    } else {
        VITAL_ROWS
            .get(i - 6)
            .map(|v| RaiseRequest::Vital { id: v.2, cost })
    }
}

/// Whether the raise buttons can be pressed: (one, ten) and whether ten
/// shows at all.
#[must_use]
pub fn raise_buttons(
    tab: CharacterTab,
    sheet: &CharacterSheet,
    sel: Selection,
    awaiting: bool,
) -> Option<(bool, bool, bool)> {
    if tab == CharacterTab::Titles {
        return None;
    }
    if tab == CharacterTab::Skills {
        let skill = sel.skill.and_then(|id| sheet.skill(id))?;
        let trained = skill.advancement.is_trained();
        let one = !awaiting
            && if trained {
                skill.raise_cost > 0 && sheet.unassigned_xp >= skill.raise_cost
            } else {
                skill.trained_cost > 0 && sheet.skill_credits >= skill.trained_cost
            };
        let ten = !awaiting
            && trained
            && skill.raise10_cost > 0
            && sheet.unassigned_xp >= skill.raise10_cost;
        return Some((one, ten, trained));
    }
    let i = sel.attribute?;
    let affordable = |c: u64| !awaiting && c > 0 && sheet.unassigned_xp >= c;
    Some((
        affordable(raise_cost(sheet, i, 1)),
        affordable(raise_cost(sheet, i, 10)),
        true,
    ))
}

// ── A stat row ──────────────────────────────────────────────────────────

/// One row of the stat list: the row art, the icon, and (as text
/// children) the name and the value. A click is reported as
/// [`ElementMessage::Clicked`].
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct StatRow {
    /// The row art (normal or highlighted; 0: none).
    pub background: u32,
    /// The icon (0: none).
    pub icon: u32,
    /// The tooltip.
    pub tooltip: Option<String>,
}

impl Widget for StatRow {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.kind == kind::CLICK {
            ui.send_message(
                me,
                ElementMessage::Clicked {
                    x: e.data1,
                    y: e.data2,
                },
            );
            return true;
        }
        e.kind == kind::MOUSE_DOWN || e.kind == kind::MOUSE_UP
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        stretch(rc, self.background, 0.0, 0.0, w, h);
        stretch(rc, self.icon, 0.0, 0.0, ROW_ICON_SIZE, ROW_ICON_SIZE);
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn tooltip_text(&self, _element: &Element) -> Option<String> {
        self.tooltip.clone().filter(|t| !t.trim().is_empty())
    }
}

fn row_text(
    ui: &mut UiRoot,
    row: ElementId,
    x: f32,
    w: f32,
    font: Option<Arc<crate::UiFont>>,
    right: bool,
) -> ElementId {
    let mut e = Element::at(x, 0.0, w, ROW_HEIGHT);
    Text::configure(&mut e);
    e.click_through = true;
    let t = ui.add(row, e, Text::default());
    if let Some(w) = ui.widget_mut::<Text>(t) {
        w.font = font;
        w.one_line = true;
        w.right_aligned = right;
        w.centered = false;
        w.padding = 0.0;
    }
    t
}

fn set_text(ui: &mut UiRoot, id: Option<ElementId>, s: &str, color: Color) {
    if let Some(t) = id.and_then(|id| ui.widget_mut::<Text>(id)) {
        t.set_lines(if s.is_empty() {
            Vec::new()
        } else {
            vec![Line::new(s, color)]
        });
    }
}

fn authored_color(ui: &UiRoot, id: Option<ElementId>) -> Color {
    id.and_then(|id| ui.widget::<Text>(id))
        .map_or(Color::WHITE, |t| t.default_color)
}

/// Put a button in Normal or Ghosted and settle its face (the state change
/// disables or enables it; its look follows on the refresh, as retail's
/// `UpdateState_` after `SetState`).
pub(crate) fn set_button_face(ui: &mut UiRoot, b: ElementId, state: u32) {
    ui.set_retail_state(b, state);
    ui.with::<Button, _>(b, |w, ui| w.refresh(ui, b));
}

pub(crate) fn collect_by_dat_id(ui: &UiRoot, root: ElementId, id: u32, out: &mut Vec<ElementId>) {
    if ui.element(root).is_some_and(|e| e.dat_element_id == id) {
        out.push(root);
    }
    for c in ui.children(root) {
        collect_by_dat_id(ui, c, id, out);
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum RowKind {
    Attribute(usize),
    Skill(u32),
}

/// The footer texts of one footer group.
#[derive(Debug, Clone, Copy, Default)]
struct Footer {
    title: Option<ElementId>,
    line1_label: Option<ElementId>,
    line1_value: Option<ElementId>,
    line2_label: Option<ElementId>,
    line2_value: Option<ElementId>,
}

// ── The controller ──────────────────────────────────────────────────────

/// The character window's controller.
#[derive(Debug)]
pub struct CharacterStatController {
    name: Option<ElementId>,
    heritage: Option<ElementId>,
    pk_status: Option<ElementId>,
    level: Option<ElementId>,
    total_xp: Option<ElementId>,
    xp_meter: Option<ElementId>,
    xp_next_value: Option<ElementId>,
    luminance_label: Option<ElementId>,
    luminance_value: Option<ElementId>,
    list: Option<ElementId>,
    viewport: Option<ElementId>,
    scrollbar: Option<ElementId>,
    raise_one: Vec<ElementId>,
    raise_ten: Vec<ElementId>,
    footer_a: Vec<ElementId>,
    footer_b: Vec<ElementId>,
    footer_c: Vec<ElementId>,
    footers: Vec<Footer>,
    tabs: [Option<ElementId>; 3],
    content_page: Option<ElementId>,
    titles_page: Option<ElementId>,
    close_button: Option<ElementId>,
    row_font: Option<Arc<crate::UiFont>>,
    rows: Vec<(ElementId, RowKind, Option<ElementId>)>,
    built_layout: Option<(CharacterTab, SkillLayout)>,
    tab: CharacterTab,
    selection: Selection,
    sheet: CharacterSheet,
    awaiting_raise: bool,
    titles: Option<crate::panels::character_titles::CharacterTitlesController>,
}

impl CharacterStatController {
    /// Bind to an imported character window. `font` is the default dat
    /// font; `row_font` the stat rows' (the larger one; `font` without it).
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        font: Option<Arc<crate::UiFont>>,
        row_font: Option<Arc<crate::UiFont>>,
    ) -> Self {
        let root = layout.root;
        let direct_child = |ui: &UiRoot, id: u32| {
            ui.children(root)
                .into_iter()
                .find(|c| ui.element(*c).is_some_and(|e| e.dat_element_id == id))
        };
        let content_page = direct_child(ui, ATTRIBUTES_PAGE_ID);
        let titles_page = direct_child(ui, TITLES_PAGE_ID);
        // Ids repeat on the hidden pages: look in the attributes page first.
        let find = |ui: &UiRoot, id: u32| {
            content_page
                .and_then(|p| ui.find_descendant(p, id))
                .or_else(|| layout.find(id))
        };
        let text = |ui: &UiRoot, id: u32| find(ui, id).filter(|t| ui.widget::<Text>(*t).is_some());
        let name = text(ui, NAME_ID);
        let heritage = text(ui, HERITAGE_ID);
        let pk_status = text(ui, PK_STATUS_ID);
        let level = text(ui, LEVEL_ID);
        let total_xp = text(ui, TOTAL_XP_ID);
        let xp_meter = find(ui, XP_METER_ID).filter(|m| ui.widget::<Meter>(*m).is_some());
        let xp_next_label = text(ui, XP_NEXT_LABEL_ID);
        let xp_next_value = text(ui, XP_NEXT_VALUE_ID);
        let luminance_label = text(ui, LUMINANCE_LABEL_ID);
        let luminance_value = text(ui, LUMINANCE_VALUE_ID);
        let level_caption = text(ui, LEVEL_CAPTION_ID);
        let total_xp_label = text(ui, TOTAL_XP_LABEL_ID);
        for t in [
            total_xp_label,
            xp_next_label,
            total_xp,
            xp_next_value,
            level_caption,
        ]
        .into_iter()
        .flatten()
        {
            if let Some(e) = ui.element_mut(t) {
                e.click_through = true;
            }
        }
        if let Some(w) = total_xp.and_then(|t| ui.widget_mut::<Text>(t)) {
            w.right_aligned = true;
        }
        if let Some(t) = level_caption
            && let Some(w) = ui.widget_mut::<Text>(t)
        {
            w.set_lines(vec![Line::new("Character", BODY), Line::new("Level", BODY)]);
        }
        set_text(ui, total_xp_label, "Total Experience (XP):", BODY);
        if let Some(t) = xp_next_label
            && let Some(w) = ui.widget_mut::<Text>(t)
        {
            if font.is_some() {
                w.font = font.clone();
            }
            w.centered = false;
            w.right_aligned = false;
            w.padding = 0.0;
            w.set_lines(vec![Line::new("XP for next level:", BODY)]);
        }
        if let Some(t) = xp_next_value
            && let Some(w) = ui.widget_mut::<Text>(t)
        {
            if font.is_some() {
                w.font = font.clone();
            }
            w.right_aligned = true;
            w.one_line = true;
            w.padding = 0.0;
        }
        let mut raise_one = Vec::new();
        let mut raise_ten = Vec::new();
        collect_by_dat_id(ui, root, RAISE_ONE_ID, &mut raise_one);
        collect_by_dat_id(ui, root, RAISE_TEN_ID, &mut raise_ten);
        raise_one.retain(|b| ui.widget::<Button>(*b).is_some());
        raise_ten.retain(|b| ui.widget::<Button>(*b).is_some());
        for b in raise_one.iter().chain(&raise_ten) {
            if let Some(w) = ui.widget_mut::<Button>(*b) {
                w.listeners.click = true;
            }
            ui.set_visible(*b, false);
        }
        let scope = content_page.unwrap_or(root);
        let mut footer_a = Vec::new();
        let mut footer_b = Vec::new();
        let mut footer_c = Vec::new();
        collect_by_dat_id(ui, scope, FOOTER_STATE_A_ID, &mut footer_a);
        collect_by_dat_id(ui, scope, FOOTER_STATE_B_ID, &mut footer_b);
        collect_by_dat_id(ui, scope, FOOTER_STATE_C_ID, &mut footer_c);
        let mut footers = Vec::new();
        for g in footer_a.iter().chain(&footer_b) {
            let by_id = |ui: &UiRoot, id| {
                ui.find_descendant(*g, id)
                    .filter(|t| ui.widget::<Text>(*t).is_some())
            };
            // The idle footer's texts may carry no ids: find them where
            // they are authored.
            let by_pos = |ui: &UiRoot, top: f32, left: f32| {
                ui.children(*g).into_iter().find(|c| {
                    ui.widget::<Text>(*c).is_some()
                        && ui.element(*c).is_some_and(|e| {
                            (e.top - top).abs() < 1.0 && (e.left - left).abs() < 1.0
                        })
                })
            };
            let pick = |ui: &UiRoot, id, top, left| by_id(ui, id).or_else(|| by_pos(ui, top, left));
            let f = Footer {
                title: pick(ui, FOOTER_TITLE_ID, 0.0, 0.0),
                line1_label: pick(ui, FOOTER_LINE1_LABEL, 20.0, 5.0),
                line1_value: pick(ui, FOOTER_LINE1_VALUE, 20.0, 200.0),
                line2_label: pick(ui, FOOTER_LINE2_LABEL, 37.0, 5.0),
                line2_value: pick(ui, FOOTER_LINE2_VALUE, 37.0, 200.0),
            };
            if let Some(t) = f.title
                && let Some(w) = ui.widget_mut::<Text>(t)
            {
                w.background_sprite = 0;
                w.vertical_justify = crate::text::VJustify::Top;
                w.honor_vertical_justification = true;
                w.one_line = true;
            }
            for t in [
                f.title,
                f.line1_label,
                f.line1_value,
                f.line2_label,
                f.line2_value,
            ]
            .into_iter()
            .flatten()
            {
                if let Some(e) = ui.element_mut(t) {
                    e.click_through = true;
                }
            }
            footers.push(f);
        }
        if footers.is_empty() {
            // A layout without footer groups: the ids alone.
            let f = Footer {
                title: text(ui, FOOTER_TITLE_ID),
                line1_label: text(ui, FOOTER_LINE1_LABEL),
                line1_value: text(ui, FOOTER_LINE1_VALUE),
                line2_label: text(ui, FOOTER_LINE2_LABEL),
                line2_value: text(ui, FOOTER_LINE2_VALUE),
            };
            footers.push(f);
        }
        let list = find(ui, LIST_BOX_ID);
        if let Some(l) = list
            && ui.element(l).is_some_and(|e| e.layout_policy.is_none())
        {
            ui.set_anchors(
                l,
                AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::BOTTOM,
            );
        }
        let mut stretch_ids = Vec::new();
        collect_by_dat_id(ui, root, LIST_SCROLLBAR_ID, &mut stretch_ids);
        for s in &stretch_ids {
            if ui.element(*s).is_some_and(|e| e.layout_policy.is_none()) {
                ui.set_anchors(
                    *s,
                    AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::BOTTOM,
                );
            }
        }
        for id in [
            LIST_DIVIDER_ID,
            FOOTER_STATE_A_ID,
            FOOTER_STATE_B_ID,
            FOOTER_STATE_C_ID,
        ] {
            let mut v = Vec::new();
            collect_by_dat_id(ui, root, id, &mut v);
            for e in v {
                if ui.element(e).is_some_and(|el| el.layout_policy.is_none()) {
                    ui.set_anchors(e, AnchorEdges::LEFT | AnchorEdges::BOTTOM);
                }
            }
        }
        let scrollbar =
            find(ui, LIST_SCROLLBAR_ID).filter(|s| ui.widget::<Scrollbar>(*s).is_some());
        let tabs = [TAB_ATTRIBUTES_ID, TAB_SKILLS_ID, TAB_TITLES_ID].map(|id| {
            let t = layout.find(id);
            if let Some(t) = t {
                bind_click(ui, t);
            }
            t
        });
        let close_button = layout.find(CLOSE_BUTTON_ID).filter(|b| bind_click(ui, *b));
        // Only the attributes page shows (it serves the skills list too).
        for page in ui.children(root) {
            let id = ui.element(page).map_or(0, |e| e.dat_element_id);
            if matches!(id, ATTRIBUTES_PAGE_ID | SKILLS_PAGE_ID | TITLES_PAGE_ID) {
                ui.set_visible(page, id == ATTRIBUTES_PAGE_ID);
            }
        }
        let mut c = Self {
            name,
            heritage,
            pk_status,
            level,
            total_xp,
            xp_meter,
            xp_next_value,
            luminance_label,
            luminance_value,
            list,
            viewport: None,
            scrollbar,
            raise_one,
            raise_ten,
            footer_a,
            footer_b,
            footer_c,
            footers,
            tabs,
            content_page,
            titles_page,
            close_button,
            row_font: row_font.or(font),
            rows: Vec::new(),
            built_layout: None,
            tab: CharacterTab::Attributes,
            selection: Selection::default(),
            sheet: CharacterSheet::default(),
            awaiting_raise: false,
            titles: titles_page.and_then(|p| {
                crate::panels::character_titles::CharacterTitlesController::bind(ui, p)
            }),
        };
        c.update_tab_states(ui);
        c.set_footer_selected(ui, false);
        c.refresh(ui);
        c
    }

    /// The titles page.
    pub fn titles_mut(
        &mut self,
    ) -> Option<&mut crate::panels::character_titles::CharacterTitlesController> {
        self.titles.as_mut()
    }

    /// The tab showing.
    #[must_use]
    pub fn current_tab(&self) -> CharacterTab {
        self.tab
    }

    /// The selection.
    #[must_use]
    pub fn selection(&self) -> Selection {
        self.selection
    }

    /// The sheet shown.
    #[must_use]
    pub fn sheet(&self) -> &CharacterSheet {
        &self.sheet
    }

    /// A raise is in flight.
    #[must_use]
    pub fn awaiting_raise(&self) -> bool {
        self.awaiting_raise
    }

    /// The rows of the list, in order: (row element, attribute index or
    /// skill id).
    #[must_use]
    pub fn rows(&self) -> Vec<(ElementId, Option<usize>, Option<u32>)> {
        self.rows
            .iter()
            .map(|(e, k, _)| match k {
                RowKind::Attribute(i) => (*e, Some(*i), None),
                RowKind::Skill(id) => (*e, None, Some(*id)),
            })
            .collect()
    }

    /// The value text element of row `row`.
    #[must_use]
    pub fn row_value_element(&self, row: ElementId) -> Option<ElementId> {
        self.rows
            .iter()
            .find(|(e, _, _)| *e == row)
            .and_then(|r| r.2)
    }

    /// The raise buttons: (by one, by ten).
    #[must_use]
    pub fn raise_buttons(&self) -> (&[ElementId], &[ElementId]) {
        (&self.raise_one, &self.raise_ten)
    }

    /// The footer groups' title texts.
    #[must_use]
    pub fn footer_titles(&self) -> Vec<Option<ElementId>> {
        self.footers.iter().map(|f| f.title).collect()
    }

    /// The footer groups' line texts: (line 1 caption, value, line 2
    /// caption, value) of each group.
    #[must_use]
    pub fn footer_lines(&self) -> Vec<[Option<ElementId>; 4]> {
        self.footers
            .iter()
            .map(|f| [f.line1_label, f.line1_value, f.line2_label, f.line2_value])
            .collect()
    }

    /// The raise in flight is over without an answer (the player declined
    /// the training confirmation): the raise buttons work again.
    pub fn release_raise(&mut self, ui: &mut UiRoot) {
        self.awaiting_raise = false;
        self.refresh_raise_buttons(ui);
    }

    /// Show `sheet`. A sheet that differs from the one shown releases a
    /// raise in flight (its answer arrived).
    pub fn apply(&mut self, ui: &mut UiRoot, sheet: &CharacterSheet) {
        if *sheet != self.sheet {
            self.awaiting_raise = false;
            self.sheet = sheet.clone();
        }
        // A selected skill that left the sheet is no longer selected.
        if let Some(id) = self.selection.skill
            && self.sheet.skill(id).is_none()
        {
            self.selection.skill = None;
        }
        self.refresh(ui);
    }

    /// Switch to `tab` (the selection clears).
    pub fn show_tab(&mut self, ui: &mut UiRoot, tab: CharacterTab) {
        if self.tab == tab {
            return;
        }
        self.tab = tab;
        self.selection = Selection::default();
        let titles = tab == CharacterTab::Titles;
        if let Some(p) = self.titles_page {
            ui.set_visible(p, titles);
        }
        if let Some(p) = self.content_page {
            ui.set_visible(p, !titles);
        }
        self.update_tab_states(ui);
        self.refresh(ui);
    }

    fn update_tab_states(&self, ui: &mut UiRoot) {
        for (t, tab) in self.tabs.iter().zip([
            CharacterTab::Attributes,
            CharacterTab::Skills,
            CharacterTab::Titles,
        ]) {
            if let Some(t) = *t {
                crate::widgets::tab_panel::set_open(ui, t, self.tab == tab);
            }
        }
    }

    fn set_footer_selected(&self, ui: &mut UiRoot, selected: bool) {
        for g in &self.footer_a {
            ui.set_visible(*g, !selected);
        }
        for g in &self.footer_b {
            ui.set_visible(*g, selected);
        }
        for g in &self.footer_c {
            ui.set_visible(*g, false);
        }
    }

    fn has_selection(&self) -> bool {
        match self.tab {
            CharacterTab::Attributes => self.selection.attribute.is_some(),
            CharacterTab::Skills => self
                .selection
                .skill
                .is_some_and(|id| self.sheet.skill(id).is_some()),
            CharacterTab::Titles => false,
        }
    }

    /// Rebuild what changed and refresh every text from the sheet.
    pub fn refresh(&mut self, ui: &mut UiRoot) {
        let s = &self.sheet;
        let header = [
            (self.name, s.name.clone()),
            (self.heritage, stat_header_line(s)),
            (self.pk_status, s.pk_status.clone().unwrap_or_default()),
            (
                self.level,
                s.level.map_or_else(|| "???".to_owned(), |l| l.to_string()),
            ),
        ];
        for (id, text) in header {
            let color = authored_color(ui, id);
            set_text(ui, id, &text, color);
        }
        set_text(ui, self.total_xp, &format_xp(s.total_xp), BODY);
        if let Some(m) = self.xp_meter
            && let Some(w) = ui.widget_mut::<Meter>(m)
        {
            w.fill = Some(s.xp_fraction);
        }
        let next = s
            .xp_to_next_level_text
            .clone()
            .unwrap_or_else(|| format_xp(s.xp_to_next_level));
        set_text(ui, self.xp_next_value, &next, BODY);
        let lum = s.luminance_visible();
        let label_color = authored_color(ui, self.luminance_label);
        set_text(
            ui,
            self.luminance_label,
            if lum { LUMINANCE_CAPTION } else { "" },
            label_color,
        );
        let value_color = authored_color(ui, self.luminance_value);
        let lum_text = if lum {
            format!(
                "{} / {}",
                format_xp(s.available_luminance),
                format_xp(s.maximum_luminance)
            )
        } else {
            String::new()
        };
        set_text(ui, self.luminance_value, &lum_text, value_color);
        if self.tab != CharacterTab::Titles {
            self.rebuild_list_if_needed(ui);
            self.refresh_rows(ui);
        }
        self.refresh_footer(ui);
        self.refresh_raise_buttons(ui);
    }

    fn list_viewport(&mut self, ui: &mut UiRoot) -> Option<ElementId> {
        let list = self.list?;
        if let Some(v) = self.viewport.filter(|v| ui.exists(*v)) {
            return Some(v);
        }
        let v = if ui.widget::<ListBox>(list).is_some() {
            ui.with::<ListBox, _>(list, |l, ui| l.viewport(ui, list))?
        } else {
            let (w, h) = ui.element(list).map_or((0.0, 0.0), |e| (e.width, e.height));
            let mut p = ScrollablePanel::new();
            p.line_height = ROW_HEIGHT as i32;
            let v = ui.add(
                list,
                Element::at(0.0, 0.0, w, h).with_anchors(AnchorEdges::ALL),
                p,
            );
            // Margins now: the first layout pass skips a hidden window,
            // whose list may have grown before it is first shown.
            ui.capture_current_anchor_baseline(v);
            v
        };
        if let Some(p) = ui.widget_mut::<ScrollablePanel>(v) {
            p.line_height = ROW_HEIGHT as i32;
        }
        if let Some(b) = self.scrollbar
            && let Some(w) = ui.widget_mut::<Scrollbar>(b)
        {
            w.model = ScrollSource::Element(v);
        }
        self.viewport = Some(v);
        Some(v)
    }

    fn content_width(&self, ui: &UiRoot) -> f32 {
        let list_w = self
            .list
            .and_then(|l| ui.element(l))
            .map_or(0.0, |e| e.width);
        if self.tab == CharacterTab::Skills
            && let (Some(bar), Some(list)) = (self.scrollbar, self.list)
            && ui.parent(bar) == ui.parent(list)
        {
            let gutter =
                ui.element(bar).map_or(0.0, |e| e.left) - ui.element(list).map_or(0.0, |e| e.left);
            if gutter > 32.0 {
                return gutter;
            }
        }
        if list_w > 0.0 {
            list_w.min(SKILL_CONTENT_WIDTH)
        } else {
            SKILL_CONTENT_WIDTH
        }
    }

    fn rebuild_list_if_needed(&mut self, ui: &mut UiRoot) {
        let wanted = (
            self.tab,
            if self.tab == CharacterTab::Skills {
                skill_layout(&self.sheet)
            } else {
                Vec::new()
            },
        );
        if self.built_layout.as_ref() == Some(&wanted) {
            return;
        }
        let Some(v) = self.list_viewport(ui) else {
            return;
        };
        let keep_scroll = self.built_layout.as_ref().is_some_and(|b| b.0 == self.tab);
        let saved = ui
            .widget::<ScrollablePanel>(v)
            .map_or(0, |p| p.scroll.scroll_y());
        ui.with::<ScrollablePanel, _>(v, |p, ui| p.clear_content(ui, v));
        self.rows.clear();
        let width = self.content_width(ui);
        let mut y = 0.0;
        if self.tab == CharacterTab::Skills {
            for (title, sprite, skills) in skill_groups(&self.sheet.clone()) {
                let header = ui.insert(
                    Element {
                        click_through: true,
                        ..Element::at(0.0, 0.0, width, ROW_HEIGHT)
                    },
                    StatRow {
                        background: sprite,
                        icon: 0,
                        tooltip: None,
                    },
                );
                let label = row_text(
                    ui,
                    header,
                    ROW_PAD_X,
                    (width - ROW_PAD_X * 2.0).max(1.0),
                    self.row_font.clone(),
                    false,
                );
                set_text(ui, Some(label), title, Color::WHITE);
                ui.with::<ScrollablePanel, _>(v, |p, ui| p.add_row(ui, v, header, y));
                y += ROW_HEIGHT;
                for s in skills {
                    let (row, value) = self.add_row(
                        ui,
                        v,
                        y,
                        width,
                        s.icon,
                        &s.name,
                        s.tooltip.clone(),
                        Color::WHITE,
                    );
                    self.rows.push((row, RowKind::Skill(s.id), Some(value)));
                    y += ROW_HEIGHT;
                }
            }
        } else {
            for i in 0..9 {
                let (name, icon, id) = if i < 6 {
                    ATTRIBUTE_ROWS[i]
                } else {
                    VITAL_ROWS[i - 6]
                };
                let tooltip = if i < 6 {
                    attribute_description(id)
                } else {
                    vital_description(id)
                };
                let (row, value) = self.add_row(
                    ui,
                    v,
                    y,
                    width,
                    icon,
                    name,
                    tooltip.map(str::to_owned),
                    BODY,
                );
                self.rows.push((row, RowKind::Attribute(i), Some(value)));
                y += ROW_HEIGHT;
            }
        }
        if let Some(p) = ui.widget_mut::<ScrollablePanel>(v) {
            p.scroll.set_scroll_y(if keep_scroll { saved } else { 0 });
        }
        ui.with::<ScrollablePanel, _>(v, |p, ui| {
            p.layout_rows(ui, v);
            if keep_scroll {
                p.scroll.set_scroll_y(saved);
                p.layout_rows(ui, v);
            }
        });
        if let Some(b) = self.scrollbar {
            ui.set_visible(b, true);
        }
        self.built_layout = Some(wanted);
    }

    #[allow(clippy::too_many_arguments)]
    fn add_row(
        &self,
        ui: &mut UiRoot,
        viewport: ElementId,
        top: f32,
        width: f32,
        icon: u32,
        name: &str,
        tooltip: Option<String>,
        name_color: Color,
    ) -> (ElementId, ElementId) {
        let mut e = Element::at(0.0, 0.0, width, ROW_HEIGHT);
        e.authored.tooltip_root_element_id = SHARED_POPUP_SKIN_ROOT_ELEMENT_ID;
        e.authored.tooltip_layout_did = SHARED_POPUP_SKIN_LAYOUT_DID;
        let row = ui.insert(
            e,
            StatRow {
                background: ROW_NORMAL_SPRITE,
                icon,
                tooltip,
            },
        );
        let n = row_text(
            ui,
            row,
            ROW_NAME_X,
            ROW_NAME_W,
            self.row_font.clone(),
            false,
        );
        set_text(ui, Some(n), name, name_color);
        let value = row_text(
            ui,
            row,
            ROW_VALUE_X,
            ROW_VALUE_W,
            self.row_font.clone(),
            true,
        );
        ui.with::<ScrollablePanel, _>(viewport, |p, ui| p.add_row(ui, viewport, row, top));
        (row, value)
    }

    fn refresh_rows(&mut self, ui: &mut UiRoot) {
        for (row, kind, value) in self.rows.clone() {
            let (text, color, selected) = match kind {
                RowKind::Attribute(i) => (
                    row_value(&self.sheet, i),
                    attribute_value_color(&self.sheet, i),
                    self.selection.attribute == Some(i),
                ),
                RowKind::Skill(id) => match self.sheet.skill(id) {
                    Some(s) => (
                        s.current_level.to_string(),
                        skill_value_color(s),
                        self.selection.skill == Some(id),
                    ),
                    None => (String::new(), Color::WHITE, false),
                },
            };
            set_text(ui, value, &text, color);
            if let Some(r) = ui.widget_mut::<StatRow>(row) {
                r.background = if selected {
                    ROW_HIGHLIGHT_SPRITE
                } else {
                    ROW_NORMAL_SPRITE
                };
            }
        }
    }

    fn refresh_footer(&self, ui: &mut UiRoot) {
        let selected = self.has_selection();
        self.set_footer_selected(ui, selected && self.tab != CharacterTab::Titles);
        let (l1, v1, l2, v2) = footer_lines(self.tab, &self.sheet, self.selection);
        for f in &self.footers {
            if let Some(t) = f.title {
                let palette = ui
                    .widget::<Text>(t)
                    .map(|w| w.font_color_palette.clone())
                    .unwrap_or_default();
                let runs = footer_title_runs(self.tab, &self.sheet, self.selection, &palette);
                if let Some(w) = ui.widget_mut::<Text>(t) {
                    // The selected row reads in white with coloured deltas;
                    // the prompt in the body colour.
                    let color = if selected { Color::WHITE } else { BODY };
                    w.set_lines(vec![Line::new(
                        footer_title(self.tab, &self.sheet, self.selection),
                        color,
                    )]);
                    w.runs = selected.then_some(runs);
                }
            }
            set_text(ui, f.line1_label, l1, BODY);
            set_text(ui, f.line1_value, &v1, BODY);
            set_text(ui, f.line2_label, l2, BODY);
            set_text(ui, f.line2_value, &v2, BODY);
        }
    }

    fn refresh_raise_buttons(&self, ui: &mut UiRoot) {
        let state = if self.has_selection() {
            raise_buttons(self.tab, &self.sheet, self.selection, self.awaiting_raise)
        } else {
            None
        };
        let Some((one, ten, ten_shown)) = state else {
            for b in self.raise_one.iter().chain(&self.raise_ten) {
                ui.set_visible(*b, false);
            }
            return;
        };
        let face = |ok: bool| if ok { states::NORMAL } else { states::GHOSTED };
        for b in &self.raise_one {
            ui.set_visible(*b, true);
            set_button_face(ui, *b, face(one));
        }
        for b in &self.raise_ten {
            ui.set_visible(*b, ten_shown);
            if ten_shown {
                set_button_face(ui, *b, face(ten));
            }
        }
    }

    fn click_row(&mut self, ui: &mut UiRoot, kind: RowKind) {
        match kind {
            RowKind::Attribute(i) => {
                self.selection.attribute = (self.selection.attribute != Some(i)).then_some(i);
            }
            RowKind::Skill(id) => {
                self.selection.skill = (self.selection.skill != Some(id)).then_some(id);
            }
        }
        self.refresh_rows(ui);
        self.refresh_footer(ui);
        self.refresh_raise_buttons(ui);
    }

    fn click_raise(&mut self, ui: &mut UiRoot, amount: u32) {
        if self.awaiting_raise {
            return;
        }
        if let Some(r) = raise_request(self.tab, &self.sheet, self.selection, amount) {
            emit(ui, r.output());
            self.awaiting_raise = true;
            self.refresh_raise_buttons(ui);
        }
    }
}

impl PanelController for CharacterStatController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if let Some(t) = self.titles.as_mut()
            && t.on_message(ui, element, message)
        {
            return true;
        }
        let ElementMessage::Clicked { .. } = message else {
            return self.rows.iter().any(|(r, _, _)| *r == element);
        };
        if let Some(&(_, kind, _)) = self.rows.iter().find(|(r, _, _)| *r == element) {
            self.click_row(ui, kind);
            return true;
        }
        if self.raise_one.contains(&element) {
            self.click_raise(ui, 1);
            return true;
        }
        if self.raise_ten.contains(&element) {
            self.click_raise(ui, 10);
            return true;
        }
        if let Some(i) = self.tabs.iter().position(|t| *t == Some(element)) {
            let tab = [
                CharacterTab::Attributes,
                CharacterTab::Skills,
                CharacterTab::Titles,
            ][i];
            self.show_tab(ui, tab);
            return true;
        }
        if Some(element) == self.close_button {
            emit(
                ui,
                PanelOutput::ClosePanel(crate::panels::panel_id::CHARACTER),
            );
            return true;
        }
        false
    }
}
