//! The examination window (layout 0x2100006B, root 0x100005F2): what an
//! assessed item says about itself (the item report), a creature's or
//! character's page, and a spell examined from the spellbook or a spell
//! bar.
//!
//! The item report is built from the server's appraisal answer in the
//! retail order: value and burden, tinkering, set and ratings, weapon and
//! armor, the short spell list, special properties, the use text, level,
//! wield, use and activation limits, item level, caster data, boosts,
//! capacity and lock, mana stone, remaining uses, the craftsman, sale and
//! rarity, the spells in full, the description. A fragment is a
//! line of its paragraph or starts a new one; its style picks a colour
//! from the text's palette (normal, beneficial, detrimental).

use std::fmt;

use ac_core::items::{ClientObject, ClientObjectTable};
use ac_core::spells::SpellMetadata;
use ac_proto::PropertyTables;
use ac_proto::appraise::IdentifyObjectResponse;
use ac_runtime::inventory::{AppraisalView, ItemCommand, ObjectRef};
use ac_runtime::session::SessionCommand;

use crate::draw::{Color, TextureRef};
use crate::geometry::AnchorEdges;
use crate::host::PanelController;
use crate::import::ImportedLayout;
use crate::input::InputAction;
use crate::panels::items::{bind_click, emit};
use crate::panels::{PanelOutput, RowTemplates};
use crate::root::{ElementMessage, UiRoot};
use crate::text::{Line, VJustify, wrap_words};
use crate::tree::{Element, ElementId};
use crate::widgets::catalog_slot::catalog_element;
use crate::widgets::list_box::ScrollablePanel;
use crate::widgets::{CatalogSlot, DatElement, ListBox, ScrollSource, Scrollbar, Text};

#[path = "appraisal_creature.rs"]
mod creature;
#[path = "appraisal_inscription.rs"]
mod inscription;
#[path = "appraisal_sections.rs"]
mod sections;
pub use creature::{
    CreatureRow, CreatureValueStyle, allegiance_display, creature_extra_rows, creature_rows,
    gender_heritage_display, level_display, player_killer_display, society_rank_suffix,
};
pub use inscription::{
    INSCRIBE_HERE, INSCRIPTION_BACKGROUND_ID, INSCRIPTION_SCROLLBAR_ID, INSCRIPTION_TEXT_ID,
    Inscription, InscriptionViewer, SIGNATURE_TEXT_ID,
};
pub use sections::{
    AppraisalPlayerSkill, clothing_coverage, effective_shield_level, enchantment_style,
    equipment_set_name, format_general, format_retail_damage, format_signed_percent,
    item_level_to_total_xp, item_total_xp_to_level, weapon_time_name,
};

/// The window's layout.
pub const LAYOUT_ID: u32 = 0x2100_006B;
/// The window's root.
pub const ROOT_ID: u32 = 0x1000_05F2;
/// The close button.
pub const CLOSE_ID: u32 = 0x1000_05F3;
/// The title.
pub const TITLE_ID: u32 = 0x1000_012D;
/// The item page.
pub const ITEM_PANEL_ID: u32 = 0x1000_012E;
/// The item report.
pub const ITEM_TEXT_ID: u32 = 0x1000_013C;
/// The item report's scrollbar.
pub const ITEM_SCROLLBAR_ID: u32 = 0x1000_013D;
/// The creature (and character) page.
pub const CREATURE_PANEL_ID: u32 = 0x1000_0140;
/// The creature-only header block.
pub const CREATURE_INFO_ID: u32 = 0x1000_014D;
/// The character-only header block.
pub const CHARACTER_INFO_ID: u32 = 0x1000_014F;
/// The spell page.
pub const SPELL_PANEL_ID: u32 = 0x1000_0153;
/// The spell's school.
pub const SPELL_SCHOOL_TEXT_ID: u32 = 0x1000_015E;
/// The spell's icon.
pub const SPELL_ICON_ID: u32 = 0x1000_015F;
/// The spell's mana.
pub const SPELL_MANA_TEXT_ID: u32 = 0x1000_0160;
/// The spell's duration.
pub const SPELL_DURATION_TEXT_ID: u32 = 0x1000_0161;
/// The spell's range.
pub const SPELL_RANGE_TEXT_ID: u32 = 0x1000_0162;
/// The spell's description and components.
pub const SPELL_DISPLAY_TEXT_ID: u32 = 0x1000_0163;
/// The spell's formula row.
pub const SPELL_FORMULA_LIST_ID: u32 = 0x1000_032D;
/// The authored cell each formula component is built from (this layout's
/// element): its own image gives way to the component's icon.
pub const SPELL_COMPONENT_TEMPLATE_ID: u32 = 0x1000_032E;
/// The cell's overlay that marks a component the player does not carry.
pub const SPELL_COMPONENT_MISSING_ID: u32 = 0x1000_0330;
/// The creature page's portrait.
pub const CREATURE_VIEWPORT_ID: u32 = 0x1000_0148;
/// The creature page's nine attribute and vital rows.
pub const CREATURE_STATS_LIST_ID: u32 = 0x1000_0149;
/// The creature page's extra rows.
pub const CREATURE_EXTRA_LIST_ID: u32 = 0x1000_0335;
/// The level number.
pub const CREATURE_LEVEL_VALUE_ID: u32 = 0x1000_014C;
/// A creature's type ("Ghost").
pub const CREATURE_DISPLAY_NAME_ID: u32 = 0x1000_014E;
/// A character's gender and heritage.
pub const CHARACTER_HERITAGE_ID: u32 = 0x1000_0150;
/// A character's displayed title.
pub const CHARACTER_TITLE_TEXT_ID: u32 = 0x1000_0151;
/// A character's player-killer status.
pub const CHARACTER_PLAYER_KILLER_ID: u32 = 0x1000_0152;
/// A character's allegiance.
pub const CHARACTER_ALLEGIANCE_ID: u32 = 0x1000_053A;
/// The creature rows' template (in this layout).
pub const CREATURE_ROW_TEMPLATE_ID: u32 = 0x1000_0166;
/// A creature row's label.
pub const CREATURE_ROW_LABEL_ID: u32 = 0x1000_012A;
/// A creature row's value.
pub const CREATURE_ROW_VALUE_ID: u32 = 0x1000_012B;
/// The creature row template's width and height, pixels (as authored).
pub const CREATURE_ROW_SIZE: (f32, f32) = (292.0, 20.0);
/// The rows' text sits this far right of their chrome, pixels.
pub const CREATURE_ROW_TEXT_INSET: f32 = 8.0;
/// In combat the creature page asks again this often, seconds.
pub const CREATURE_REFRESH_SECONDS: f64 = 0.75;

/// The string property that marks a character's appraisal.
const TEMPLATE_STRING_PROPERTY: u32 = 5;
/// The int property that marks a character's appraisal.
const CHARACTER_MARKER_INT_PROPERTY: u32 = 0x105;
/// The displayed-name string property.
const DISPLAYED_NAME_STRING_PROPERTY: u32 = 0x34;

// ── The item report ─────────────────────────────────────────────────────

/// A fragment's colour: an index into the text's palette.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ReportStyle {
    /// Ordinary text.
    #[default]
    Normal = 0,
    /// Raised by an enchantment.
    Beneficial = 1,
    /// Lowered by an enchantment.
    Detrimental = 2,
}

/// How a fragment joins the one before it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Separator {
    /// The first fragment.
    None,
    /// The next line of the paragraph.
    Line,
    /// A new paragraph (a blank line between).
    Paragraph,
}

/// One piece of the report.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReportFragment {
    /// The text (may hold line breaks; empty for a section boundary).
    pub text: String,
    /// How it joins the fragment before it.
    pub separator: Separator,
    /// Its colour.
    pub style: ReportStyle,
}

/// The item report.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ItemAppraisalReport {
    /// The fragments in order.
    pub fragments: Vec<ReportFragment>,
}

impl ItemAppraisalReport {
    /// Nothing to say.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.fragments.is_empty()
    }
}

impl fmt::Display for ItemAppraisalReport {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        for (i, fragment) in self.fragments.iter().enumerate() {
            if i != 0 {
                f.write_str(if fragment.separator == Separator::Paragraph {
                    "\n\n"
                } else {
                    "\n"
                })?;
            }
            f.write_str(&fragment.text)?;
        }
        Ok(())
    }
}

#[derive(Default)]
struct ReportBuilder {
    fragments: Vec<ReportFragment>,
}

impl ReportBuilder {
    fn line(&mut self, text: impl Into<String>) {
        self.add(text.into(), true, ReportStyle::Normal);
    }

    fn paragraph(&mut self, text: impl Into<String>) {
        self.add(text.into(), false, ReportStyle::Normal);
    }

    fn line_styled(&mut self, text: impl Into<String>, style: ReportStyle) {
        self.add(text.into(), true, style);
    }

    fn paragraph_styled(&mut self, text: impl Into<String>, style: ReportStyle) {
        self.add(text.into(), false, style);
    }

    /// A section boundary (nothing before the first fragment).
    fn blank_line(&mut self) {
        if self.fragments.is_empty() {
            return;
        }
        self.fragments.push(ReportFragment {
            text: String::new(),
            separator: Separator::Line,
            style: ReportStyle::Normal,
        });
    }

    fn add(&mut self, text: String, same_paragraph: bool, style: ReportStyle) {
        if text.trim().is_empty() {
            return;
        }
        let separator = if self.fragments.is_empty() {
            Separator::None
        } else if same_paragraph {
            Separator::Line
        } else {
            Separator::Paragraph
        };
        self.fragments.push(ReportFragment {
            text,
            separator,
            style,
        });
    }
}

/// Names the report reads from the dats (materials, creature types).
pub trait AppraisalNames {
    /// A material's name ("" when unknown).
    fn material(&self, _material: i32) -> String {
        String::new()
    }

    /// A creature type's name ("" when unknown).
    fn creature(&self, _creature: i32) -> String {
        String::new()
    }

    /// A skill's authored name (the skill table's), `None` when the
    /// authored data does not name it: each line answers that its own way.
    fn skill(&self, _skill: i32) -> Option<String> {
        None
    }

    /// A heritage group's name ("" when unknown).
    fn heritage(&self, heritage: i32) -> String {
        crate::panels::character::heritage_name(heritage)
            .unwrap_or_default()
            .to_owned()
    }

    /// The viewer's own skill (the shield line reads it).
    fn player_skill(&self, _skill: u32) -> AppraisalPlayerSkill {
        AppraisalPlayerSkill::default()
    }

    /// A character title's text by its id, `None` when unknown (the
    /// character page then shows the character's template name).
    fn character_title(&self, _title_id: u32) -> Option<String> {
        None
    }

    /// The viewer's own society bits (the society row colours a rival
    /// red and his own green).
    fn local_faction_bits(&self) -> i32 {
        0
    }

    /// The object's name with its material in front (once), as the title
    /// shows it.
    fn appropriate_name(&self, obj: &ClientObject) -> String {
        let base = obj.appropriate_name().into_owned();
        let Some(material) = obj.material_type else {
            return base;
        };
        let material = self.material(material as i32);
        if material.is_empty() {
            return base;
        }
        let remainder = base.replace(&material, "");
        let remainder = remainder.trim();
        if remainder.is_empty() {
            material
        } else {
            format!("{material} {remainder}")
        }
    }
}

/// No dat names.
#[derive(Debug, Clone, Copy, Default)]
pub struct NoNames;

impl AppraisalNames for NoNames {}

/// The client enum of the material names in the master enum map.
pub const MATERIAL_CLIENT_ENUM: u32 = 0x1000_0001;
/// The material names' slot in its map.
pub const MATERIAL_SUB_ENUM: u32 = 1;

/// The material names the dats hold: master map → client enum
/// 0x10000001 → slot 1 → the dual enum map's names, underscores read as
/// spaces. Empty when any link is missing.
#[must_use]
pub fn load_material_names(dats: &ac_dat::DatCollection) -> std::collections::HashMap<u32, String> {
    use ac_dat::objects::DualEnumIdMap;
    let mut names = std::collections::HashMap::new();
    let Some(map) = client_enum_map(dats, MATERIAL_CLIENT_ENUM)
        .and_then(|m| m.client_id(MATERIAL_SUB_ENUM))
        .and_then(|id| dats.get::<DualEnumIdMap>(id).ok().flatten())
    else {
        return names;
    };
    for (id, text) in &map.map.client_enum_to_name {
        names.entry(*id).or_insert_with(|| text.replace('_', " "));
    }
    names
}

/// The enum map the portal's master enum map names for client enumeration
/// `client_enum`, if the dats have it.
#[must_use]
pub fn client_enum_map(
    dats: &ac_dat::DatCollection,
    client_enum: u32,
) -> Option<ac_dat::objects::EnumIdMap> {
    use ac_dat::objects::EnumIdMap;
    let master_id = dats.portal.header().master_map_id.cast_unsigned();
    (master_id != 0)
        .then(|| dats.get::<EnumIdMap>(master_id).ok().flatten())
        .flatten()
        .and_then(|m| m.client_id(client_enum))
        .and_then(|id| dats.get::<EnumIdMap>(id).ok().flatten())
}

/// Names from tables.
#[derive(Debug, Clone, Default)]
pub struct NameTables {
    /// Material id to name.
    pub materials: std::collections::HashMap<u32, String>,
    /// Creature type to name.
    pub creatures: std::collections::HashMap<u32, String>,
    /// Skill id to its authored name.
    pub skills: std::collections::HashMap<u32, String>,
}

impl NameTables {
    /// Every table the dats hold: the materials, the creature types (the
    /// enum mapping chain from 0x2200000E) and the skill table's names.
    #[must_use]
    pub fn load(dats: &ac_dat::DatCollection) -> Self {
        use ac_dat::objects::{EnumMapper, SKILL_TABLE_ID, SkillTable};
        let creatures = ac_core::names::CreatureDisplayNames::load(|id| {
            dats.get::<EnumMapper>(id)
                .ok()
                .flatten()
                .map(|m| ac_core::names::EnumMapping {
                    base_enum_map: m.base_enum_map,
                    id_to_string: m.id_to_string,
                })
        });
        let mut skills = std::collections::HashMap::new();
        if let Ok(Some(table)) = dats.get::<SkillTable>(SKILL_TABLE_ID) {
            for (id, skill) in table.skills {
                if !skill.name.trim().is_empty() {
                    skills.entry(id).or_insert(skill.name);
                }
            }
        }
        Self {
            materials: load_material_names(dats),
            creatures: creatures.names().clone(),
            skills,
        }
    }
}

impl AppraisalNames for NameTables {
    fn skill(&self, skill: i32) -> Option<String> {
        u32::try_from(skill)
            .ok()
            .filter(|s| *s > 0)
            .and_then(|s| self.skills.get(&s).cloned())
    }

    fn material(&self, material: i32) -> String {
        u32::try_from(material)
            .ok()
            .filter(|m| *m > 0)
            .and_then(|m| self.materials.get(&m).cloned())
            .unwrap_or_default()
    }

    fn creature(&self, creature: i32) -> String {
        u32::try_from(creature)
            .ok()
            .and_then(|c| self.creatures.get(&c).cloned())
            .unwrap_or_default()
    }
}

fn get_int(p: &PropertyTables, id: u32) -> i32 {
    p.ints.get(&id).copied().unwrap_or(0)
}

fn get_bool(p: &PropertyTables, id: u32) -> bool {
    p.bools.get(&id).copied().unwrap_or(false)
}

fn get_string(p: &PropertyTables, id: u32) -> &str {
    p.strings.get(&id).map_or("", String::as_str)
}

/// `value` with thousands separators ("12,500").
#[must_use]
pub fn group_thousands(value: i64) -> String {
    let digits = value.unsigned_abs().to_string();
    let mut out = String::new();
    for (i, c) in digits.chars().enumerate() {
        if i != 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(c);
    }
    if value < 0 { format!("-{out}") } else { out }
}

/// The workmanship word of a band 0..=10 ("" for none).
#[must_use]
pub fn workmanship_adjective(workmanship: i32) -> &'static str {
    match workmanship {
        i32::MIN..=0 => "",
        1 => "Poorly crafted",
        2 => "Well-crafted",
        3 => "Finely crafted",
        4 => "Exquisitely crafted",
        5 => "Magnificent",
        6 => "Nearly flawless",
        7 => "Flawless",
        8 => "Utterly flawless",
        9 => "Incomparable",
        _ => "Priceless",
    }
}

/// The damage types a mask names, in the game's order, joined with "/".
#[must_use]
pub fn damage_type_name(mask: u32) -> String {
    const NAMES: [(u32, &str); 9] = [
        (0x0000_0001, "Slashing"),
        (0x0000_0002, "Piercing"),
        (0x0000_0004, "Bludgeoning"),
        (0x0000_0008, "Cold"),
        (0x0000_0010, "Fire"),
        (0x0000_0020, "Acid"),
        (0x0000_0040, "Electrical"),
        (0x0000_0400, "Nether"),
        (0x1000_0000, "Prismatic"),
    ];
    NAMES
        .iter()
        .filter(|(bit, _)| mask & bit != 0)
        .map(|(_, n)| *n)
        .collect::<Vec<_>>()
        .join("/")
}

/// A cooldown as "1mo 2d 3h 4m 5s ".
#[must_use]
pub fn format_delta_time(seconds: f64) -> String {
    let mut remaining = (seconds.trunc() as i64).max(0);
    let months = remaining / 2_592_000;
    remaining %= 2_592_000;
    let days = remaining / 86_400;
    remaining %= 86_400;
    let hours = remaining / 3_600;
    remaining %= 3_600;
    let minutes = remaining / 60;
    let secs = remaining % 60;
    let mut out = String::new();
    for (n, unit) in [(months, "mo"), (days, "d"), (hours, "h"), (minutes, "m")] {
        if n != 0 {
            out.push_str(&format!("{n}{unit} "));
        }
    }
    out.push_str(&format!("{secs}s "));
    out
}

/// A remaining lifetime as "1 years, 2 days, ... 5 seconds.".
#[must_use]
pub fn format_expiry(remaining: i32) -> String {
    let mut r = remaining.max(0);
    let mut out = String::new();
    for (size, unit) in [
        (31_536_000, "years"),
        (86_400, "days"),
        (3_600, "hours"),
        (60, "minutes"),
    ] {
        if r > size {
            out.push_str(&format!("{} {unit}, ", r / size));
            r %= size;
        }
    }
    out.push_str(&format!("{r} seconds."));
    out
}

fn lockpick_difficulty(success_percent: i32) -> Option<&'static str> {
    Some(match success_percent {
        i32::MIN..=-1 => return None,
        0 => "impossible",
        1..=4 => "ridiculously difficult",
        5..=14 => "extremely difficult",
        15..=34 => "quite difficult",
        35..=49 => "difficult",
        50..=69 => "challenging",
        70..=84 => "mildly challenging",
        85..=94 => "easy",
        _ => "trivial",
    })
}

fn pluralized_gem_name(material_type: i32, material: &str) -> String {
    match material_type {
        0x26 => "Rubies".to_owned(),
        0x0B | 0x18 | 0x1B | 0x1D | 0x20 | 0x25 | 0x28 | 0x2E | 0x24 | 0x2D => {
            format!("pieces of {material}")
        }
        0x1A | 0x31 => format!("{material}es"),
        0x1C => material.to_owned(),
        _ => format!("{material}s"),
    }
}

fn signed(value: i32) -> String {
    if value > 0 {
        format!("+{value}")
    } else {
        value.to_string()
    }
}

/// The item report for `obj` from its appraisal. `spell` names a spell
/// (and gives its description).
#[must_use]
pub fn item_report(
    obj: &ClientObject,
    appraisal: &IdentifyObjectResponse,
    spell: &dyn Fn(u32) -> Option<SpellMetadata>,
    names: &dyn AppraisalNames,
) -> ItemAppraisalReport {
    use sections as s;
    let p = &appraisal.properties;
    let mut r = ReportBuilder::default();
    value_and_burden(&mut r, p);
    tinkering(&mut r, p);
    s::set_and_ratings(&mut r, p);
    s::weapon_and_armor(&mut r, obj, appraisal, names);
    s::defense_modifiers(&mut r, appraisal);
    s::armor_modifiers(&mut r, appraisal);
    short_magic_info(&mut r, appraisal, spell);
    special_properties(&mut r, p, names);
    usage(&mut r, p);
    s::level_limits(&mut r, p);
    s::wield_requirements(&mut r, p, names);
    s::usage_limits(&mut r, p, names);
    s::item_level(&mut r, p);
    s::activation_requirements(&mut r, p, appraisal.success, names);
    s::caster_data(&mut r, appraisal);
    s::boost_and_healing(&mut r, obj, appraisal);
    capacity_and_lock(&mut r, obj, appraisal);
    s::mana_stone(&mut r, appraisal);
    s::remaining_uses(&mut r, obj, appraisal);
    craftsman(&mut r, p);
    sale_and_rare(&mut r, p);
    magic_info(&mut r, appraisal, spell);
    description(&mut r, p, names);
    ItemAppraisalReport {
        fragments: r.fragments,
    }
}

fn value_and_burden(r: &mut ReportBuilder, p: &PropertyTables) {
    r.line(p.ints.get(&19).map_or_else(
        || "Value: ???".to_owned(),
        |v| format!("Value: {}", group_thousands(i64::from(*v))),
    ));
    r.line(p.ints.get(&5).map_or_else(
        || "Burden: Unknown".to_owned(),
        |v| format!("Burden: {}", group_thousands(i64::from(*v))),
    ));
}

fn tinkering(r: &mut ReportBuilder, p: &PropertyTables) {
    if let Some(&tinkers) = p.ints.get(&171) {
        r.line(format!(
            "This item has been tinkered {tinkers} {}",
            if tinkers == 1 { "time." } else { "times." }
        ));
    }
    let last = get_string(p, 39);
    if !last.trim().is_empty() {
        r.line(format!("Last tinkered by {last}."));
    }
    let imbued = get_string(p, 40);
    if !imbued.trim().is_empty() {
        r.line(format!("Imbued by {imbued}."));
    }
    if let Some(&workmanship) = p.ints.get(&105) {
        match p.ints.get(&170).copied().filter(|n| *n > 0) {
            Some(salvaged) => {
                let average = f64::from(workmanship) / f64::from(salvaged);
                let band = (average.round_ties_even() as i32).clamp(0, 10);
                r.line(format!(
                    "Workmanship: {} ({average:.2})",
                    workmanship_adjective(band)
                ));
                r.paragraph(format!("Salvaged from {salvaged} items."));
            }
            None => r.line(format!(
                "Workmanship: {} ({workmanship})",
                workmanship_adjective(workmanship.clamp(0, 10))
            )),
        }
    }
    r.blank_line();
}

fn spell_name(spell: &dyn Fn(u32) -> Option<SpellMetadata>, id: u32) -> String {
    spell(id)
        .map(|m| m.name)
        .filter(|n| !n.trim().is_empty())
        .unwrap_or_else(|| format!("Spell {id}"))
}

fn short_magic_info(
    r: &mut ReportBuilder,
    a: &IdentifyObjectResponse,
    spell: &dyn Fn(u32) -> Option<SpellMetadata>,
) {
    if a.spell_book.is_empty() {
        return;
    }
    if !a.success {
        r.paragraph("Spells: unknown.");
        return;
    }
    let ordinary: Vec<String> = a
        .spell_book
        .iter()
        .filter(|raw| *raw & 0x8000_0000 == 0)
        .map(|raw| spell_name(spell, *raw))
        .collect();
    if !ordinary.is_empty() {
        r.paragraph(format!("Spells: {}", ordinary.join(", ")));
    }
}

fn special_properties(r: &mut ReportBuilder, p: &PropertyTables, names: &dyn AppraisalNames) {
    r.blank_line();
    let carry = get_int(p, 279);
    if carry > 0 {
        r.blank_line();
        r.line(format!(
            "You can only carry {} of these items.",
            group_thousands(i64::from(carry))
        ));
    }
    if let Some(&cooldown) = p.floats.get(&167)
        && cooldown > 0.0
    {
        r.line(format!(
            "Cooldown When Used: {}",
            format_delta_time(cooldown)
        ));
        if p.ints.contains_key(&280) {
            r.blank_line();
        }
    }
    let cleave = get_int(p, 292);
    if cleave > 1 {
        r.line(format!("Cleave: {cleave} enemies in front arc."));
        r.blank_line();
    }
    let mut special: Vec<String> = Vec::new();
    let slayer = get_int(p, 166);
    if slayer != 0 {
        if slayer == 31 {
            special.push("Bael'Zharon's Hate".to_owned());
        } else {
            let name = names.creature(slayer);
            if !name.trim().is_empty() {
                special.push(format!("{name} slayer"));
            }
        }
    }
    if get_int(p, 47) & 0x79E0 != 0 {
        special.push("Multi-Strike".to_owned());
    }
    let imbued = [179, 303, 304, 305, 306]
        .iter()
        .fold(0u32, |acc, id| acc | get_int(p, *id) as u32);
    const IMBUED: [(u32, &str); 15] = [
        (0x0000_0001, "Critical Strike"),
        (0x0000_0002, "Crippling Blow"),
        (0x0000_0004, "Armor Rending"),
        (0x0000_0008, "Slash Rending"),
        (0x0000_0010, "Pierce Rending"),
        (0x0000_0020, "Bludgeon Rending"),
        (0x0000_0040, "Acid Rending"),
        (0x0000_0080, "Cold Rending"),
        (0x0000_0100, "Lightning Rending"),
        (0x0000_0200, "Fire Rending"),
        (0x0000_0400, "+1 Melee Defense"),
        (0x0000_0800, "+1 Missile Defense"),
        (0x0000_1000, "+1 Magic Defense"),
        (0x0000_4000, "Nether Rending"),
        (0x8000_0000, "Phantasmal"),
    ];
    special.extend(
        IMBUED
            .iter()
            .filter(|(f, _)| imbued & f != 0)
            .map(|(_, t)| (*t).to_owned()),
    );
    if p.floats.contains_key(&159) {
        special.push("Magic Absorbing".to_owned());
    }
    if get_int(p, 36) >= 9_999 {
        special.push("Unenchantable".to_owned());
    }
    if matches!(get_int(p, 114), 1 | 2) {
        special.push("Attuned".to_owned());
    }
    match get_int(p, 33) {
        -2 => special.push("Destroyed on Death".to_owned()),
        -1 => special.push("Dropped on Death".to_owned()),
        1 => special.push("Bonded".to_owned()),
        _ => {}
    }
    if get_bool(p, 91) {
        special.push("Retained".to_owned());
    }
    for (id, text) in [
        (136, "Crushing Blow"),
        (147, "Biting Strike"),
        (155, "Armor Cleaving"),
    ] {
        if p.floats.contains_key(&id) {
            special.push(text.to_owned());
        }
    }
    if p.floats.contains_key(&157)
        && let Some(&kind) = p.ints.get(&263)
    {
        special.push(format!(
            "Resistance Cleaving: {}",
            damage_type_name(kind as u32)
        ));
    }
    if p.data_ids.contains_key(&55) {
        special.push("Cast on Strike".to_owned());
    }
    if get_bool(p, 99) {
        special.push("Ivoryable".to_owned());
    }
    if get_bool(p, 100) {
        special.push("Dyeable".to_owned());
    }
    if !special.is_empty() {
        r.line(format!("Properties: {}", special.join(", ")));
    }
    if imbued != 0 {
        r.line("This item cannot be further imbued.");
    }
    if get_bool(p, 130) {
        r.line("This item is tethered to the left side.");
    }
}

fn usage(r: &mut ReportBuilder, p: &PropertyTables) {
    r.paragraph(get_string(p, 14));
}

fn capacity_and_lock(r: &mut ReportBuilder, obj: &ClientObject, a: &IdentifyObjectResponse) {
    let p = &a.properties;
    let hook = obj.is_hook();
    if !hook || a.hook_profile.is_none() {
        let (items, containers) = (obj.items_capacity, obj.containers_capacity);
        if items > 0 && containers > 0 {
            r.paragraph(format!(
                "Can hold up to {items} items and {containers} containers."
            ));
        } else if items > 0 {
            r.paragraph(format!("Can hold up to {items} items."));
        } else if containers > 0 {
            r.paragraph(format!("Can hold up to {containers} containers."));
        }
        let pages = get_int(p, 175);
        if pages > 0 {
            r.paragraph(format!("{} of {pages} pages full.", get_int(p, 174)));
        }
    }
    if hook {
        return;
    }
    let resistance = p.ints.get(&38).copied();
    let Some(&locked) = p.bools.get(&3) else {
        if let Some(res) = resistance.filter(|r| *r != 0) {
            r.paragraph(format!("Bonus to Lockpick Skill: {}", signed(res)));
        }
        return;
    };
    if !locked {
        r.paragraph("Unlocked");
        return;
    }
    r.paragraph("Locked");
    let Some(res) = resistance else {
        r.paragraph("You can't tell how hard the lock is to pick.");
        return;
    };
    if let Some(difficulty) = p.ints.get(&173).and_then(|c| lockpick_difficulty(*c)) {
        r.paragraph(format!(
            "The lock looks {difficulty} to pick (Resistance {res})."
        ));
    }
}

fn craftsman(r: &mut ReportBuilder, p: &PropertyTables) {
    let craftsman = get_string(p, 25);
    if !craftsman.trim().is_empty() && !get_bool(p, 85) && !get_bool(p, 94) {
        r.line(format!("Created by {craftsman}."));
    }
}

fn sale_and_rare(r: &mut ReportBuilder, p: &PropertyTables) {
    if p.bools.get(&69) == Some(&false) {
        r.line("This item cannot be sold.");
    }
    if get_bool(p, 108) {
        r.paragraph(
            "This rare item has a timer restriction of 3 minutes. You will not be able to \
             use another rare item with a timer within 3 minutes of using this one.",
        );
    }
    let rare = get_int(p, 17);
    if rare > 0 {
        r.paragraph(format!("Rare #{rare}"));
    }
}

fn spell_block(heading: &str, spells: &[(u32, Option<SpellMetadata>)]) -> String {
    let mut text = heading.to_owned();
    for (id, meta) in spells {
        let name = meta
            .as_ref()
            .map(|m| m.name.clone())
            .filter(|n| !n.trim().is_empty())
            .unwrap_or_else(|| format!("Spell {id}"));
        text.push_str(&format!("\n~ {name}: "));
        if let Some(m) = meta
            && !m.description.trim().is_empty()
        {
            text.push_str(&m.description);
        }
    }
    text
}

fn magic_info(
    r: &mut ReportBuilder,
    a: &IdentifyObjectResponse,
    spell: &dyn Fn(u32) -> Option<SpellMetadata>,
) {
    if a.spell_book.is_empty() {
        return;
    }
    if !a.success {
        r.paragraph("Spells: unknown.");
        return;
    }
    let (mut ordinary, mut enchantments) = (Vec::new(), Vec::new());
    for raw in &a.spell_book {
        let id = raw & 0x7FFF_FFFF;
        let entry = (id, spell(id));
        if raw & 0x8000_0000 == 0 {
            ordinary.push(entry);
        } else {
            enchantments.push(entry);
        }
    }
    let p = &a.properties;
    if !ordinary.is_empty() {
        if let Some(spellcraft) = p.ints.get(&106) {
            r.line(format!("Spellcraft: {spellcraft}."));
        }
        if let (Some(current), Some(maximum)) = (p.ints.get(&107), p.ints.get(&108)) {
            r.line(format!("Mana: {current} / {maximum}."));
        }
        match p.floats.get(&5).filter(|rate| rate.abs() > 0.000_001) {
            Some(rate) => {
                // A draining item's rate is negative; the interval is its
                // size, rounded half up.
                let seconds = ((1.0 / rate).abs() + 0.5) as i32;
                r.line(format!("Mana Cost: 1 point per {seconds} seconds."));
            }
            None => {
                if let Some(&cost) = p.ints.get(&117) {
                    let mut text = format!("Mana Cost: {cost}.");
                    if cost > 0 {
                        text.push_str("\n(Can be reduced by the Mana Conversion skill).");
                    }
                    r.paragraph(text);
                }
            }
        }
        r.paragraph(spell_block("Spell Descriptions:", &ordinary));
    }
    if !enchantments.is_empty() {
        r.paragraph(spell_block("Enchantments:", &enchantments));
    }
}

fn description(r: &mut ReportBuilder, p: &PropertyTables, names: &dyn AppraisalNames) {
    if p.ints.contains_key(&267)
        && p.ints.contains_key(&98)
        && let Some(&remaining) = p.ints.get(&268)
    {
        r.line(if remaining < 0 {
            "This item is in the act of disintegrating.".to_owned()
        } else {
            format!("This item expires in {}", format_expiry(remaining))
        });
    }
    let long = get_string(p, 16);
    if long.trim().is_empty() {
        r.paragraph(get_string(p, 15));
    } else {
        let mut text = get_string(p, 52).to_owned();
        if text.trim().is_empty() {
            text = long.to_owned();
        }
        let (mut prefix, mut suffix) = (String::new(), String::new());
        if let Some(&decorations) = p.ints.get(&172) {
            if decorations & 1 != 0
                && let Some(&workmanship) = p.ints.get(&105)
            {
                prefix.push_str(workmanship_adjective(workmanship.clamp(0, 10)));
                prefix.push(' ');
            }
            let material = names.material(get_int(p, 131));
            if !material.trim().is_empty() {
                prefix.push_str(&material);
                prefix.push(' ');
                text = text.replacen(&material, "", 1).trim().to_owned();
            }
            if decorations & 4 != 0
                && let (Some(&count), Some(&gem)) = (p.ints.get(&177), p.ints.get(&178))
            {
                let gem_name = if count == 1 {
                    names.material(gem)
                } else {
                    pluralized_gem_name(gem, &names.material(gem))
                };
                if !gem_name.trim().is_empty() {
                    suffix = format!(", set with {count} {gem_name}");
                }
            }
        }
        r.paragraph(format!("{prefix}{text}{suffix}"));
    }
    let portal = get_int(p, 111);
    if portal != 0 {
        let mut lines = Vec::new();
        for (bit, text) in [
            (2, "Player Killers may not use this portal."),
            (4, "Lite Player Killers may not use this portal."),
            (8, "Non-Player Killers may not use this portal."),
            (0x20, "This portal cannot be recalled nor linked to."),
            (0x10, "This portal cannot be summoned."),
        ] {
            if portal & bit != 0 {
                lines.push(text);
            }
        }
        if !lines.is_empty() {
            r.paragraph(lines.join("\n"));
        }
    }
}

/// The report as coloured lines for a text `max_width` pixels wide: a
/// blank line before each new paragraph, a fragment's style picking its
/// colour from `palette` (else `default`), long lines wrapped.
#[must_use]
pub fn shape_report(
    report: &ItemAppraisalReport,
    palette: &[Color],
    default: Color,
    max_width: f32,
    measure: &dyn Fn(&str) -> f32,
) -> Vec<Line> {
    let mut lines = Vec::new();
    for fragment in &report.fragments {
        let color = palette
            .get(fragment.style as usize)
            .copied()
            .unwrap_or(default);
        if !lines.is_empty() && fragment.separator == Separator::Paragraph {
            lines.push(Line::new(String::new(), color));
        }
        let text = fragment.text.replace("\\n", "\n");
        for logical in text.split('\n') {
            if logical.is_empty() {
                lines.push(Line::new(String::new(), color));
                continue;
            }
            for wrapped in wrap_words(logical, measure, max_width.max(1.0)) {
                lines.push(Line::new(wrapped, color));
            }
        }
    }
    lines
}

// ── The spell page ──────────────────────────────────────────────────────

/// One component of an examined spell's formula.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SpellExamineComponent {
    /// The component's name.
    pub name: String,
    /// Its icon.
    pub icon: Option<TextureRef>,
    /// The player carries one.
    pub owned: bool,
}

/// The spell page's mana line.
#[must_use]
pub fn spell_mana_text(spell: &SpellMetadata) -> String {
    let mut mana = if spell.mana_cost > 0 {
        spell.mana_cost.to_string()
    } else {
        "???".to_owned()
    };
    if spell.mana_modifier > 0 {
        mana.push_str(&format!(" + {} per target", spell.mana_modifier));
    }
    format!("Mana: {mana}")
}

/// The spell page's duration line ("" for none).
#[must_use]
pub fn spell_duration_text(spell: &SpellMetadata) -> String {
    let d = spell.duration;
    if d <= 0.0 || d == -1.0 {
        return String::new();
    }
    if d >= 60.0 {
        format!("Duration: {} min.", (d / 60.0) as u32)
    } else {
        format!("Duration: {} sec.", d as u32)
    }
}

/// The spell page's range line for a caster of `magic_skill` ("" for
/// none): the range is capped at 75 metres and shown in yards.
#[must_use]
pub fn spell_range_text(spell: &SpellMetadata, magic_skill: u32) -> String {
    let range =
        (spell.base_range_constant + spell.base_range_modifier * magic_skill as f32).min(75.0);
    if range > 0.0 {
        format!("Range: {:.1} yds.", range / 0.9144)
    } else {
        String::new()
    }
}

/// The spell page's description with its components listed.
#[must_use]
pub fn spell_display_text(spell: &SpellMetadata, components: &[SpellExamineComponent]) -> String {
    let mut text = spell.description.clone();
    if components.is_empty() {
        return text;
    }
    text.push_str("\nCOMPONENTS:");
    for c in components {
        text.push_str("\n     ");
        text.push_str(&c.name);
    }
    text
}

// ── The window ──────────────────────────────────────────────────────────

/// The page the window shows.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum AppraisalPage {
    /// An item.
    #[default]
    Item,
    /// A creature.
    Creature,
    /// A character.
    Character,
    /// A spell.
    Spell,
}

/// The page an appraisal answer opens.
#[must_use]
pub fn page_for(appraisal: &IdentifyObjectResponse) -> AppraisalPage {
    if appraisal.creature_profile.is_none() {
        return AppraisalPage::Item;
    }
    let p = &appraisal.properties;
    if p.strings.contains_key(&TEMPLATE_STRING_PROPERTY)
        || p.ints.contains_key(&CHARACTER_MARKER_INT_PROPERTY)
    {
        AppraisalPage::Character
    } else {
        AppraisalPage::Creature
    }
}

/// The window's title for an appraised object: the displayed name (else
/// the material-decorated name), with the stack count in front.
#[must_use]
pub fn appraisal_title(
    obj: &ClientObject,
    p: &PropertyTables,
    names: &dyn AppraisalNames,
) -> String {
    let mut name = get_string(p, DISPLAYED_NAME_STRING_PROPERTY).to_owned();
    if name.trim().is_empty() {
        name = names.appropriate_name(obj);
    }
    if obj.stack_size > 1 {
        format!("{} {name}", obj.stack_size)
    } else {
        name
    }
}

/// A creature row list in two layers around the portrait: the rows'
/// chrome in the authored list behind the 3-D portrait, their text in a
/// list of its own in front of it, 8 pixels further right. The two scroll
/// together (the front one takes the wheel).
#[derive(Debug, Clone, Copy)]
pub struct CreatureRowList {
    /// The authored list (the chrome layer).
    pub background: ElementId,
    /// The text layer.
    pub foreground: ElementId,
}

impl CreatureRowList {
    /// Split `host` around `viewport` in `panel`: the host goes behind the
    /// portrait at `background_z`, a text layer takes the host's place in
    /// front of it. `stretch_rows`: the rows widen and narrow with the
    /// list by their own horizontal edges, in both layers.
    fn create(
        ui: &mut UiRoot,
        panel: ElementId,
        host: ElementId,
        background_z: i32,
        stretch_rows: bool,
    ) -> Option<Self> {
        ui.widget::<ListBox>(host)?;
        let (left, top, width, height, anchors, z) = ui
            .element(host)
            .map(|e| (e.left, e.top, e.width, e.height, e.anchors, e.z_order))?;
        // Both layers measure the rows' edges from their authored widths
        // (the window binds at its authored size).
        ui.with::<ListBox, _>(host, |b, ui| {
            b.set_rows_follow_width(ui, stretch_rows.then_some(width));
        });
        ui.set_z_order(host, background_z);
        if let Some(e) = ui.element_mut(host) {
            e.click_through = true;
        }
        let mut panel_widget = ScrollablePanel::new();
        panel_widget.line_height = CREATURE_ROW_SIZE.1 as i32;
        panel_widget.set_rows_follow_width(stretch_rows.then_some(CREATURE_ROW_SIZE.0));
        let foreground = ui.add(
            panel,
            Element::at(
                left + CREATURE_ROW_TEXT_INSET,
                top,
                CREATURE_ROW_SIZE.0,
                height,
            )
            .with_anchors(anchors)
            .with_z_order(z),
            panel_widget,
        );
        // The margins are the authored ones (the window is at its authored
        // size while it binds), so a saved size later moves both layers
        // alike.
        let (pw, ph) = ui
            .element(panel)
            .map_or((0.0, 0.0), |e| (e.width, e.height));
        ui.apply_anchor(foreground, pw, ph);
        Some(Self {
            background: host,
            foreground,
        })
    }

    /// Remove every row (`reset_scroll`: back to the top).
    fn flush(&self, ui: &mut UiRoot) {
        ui.with::<ListBox, _>(self.background, |b, ui| b.flush(ui));
        let fg = self.foreground;
        ui.with::<ScrollablePanel, _>(fg, |p, ui| p.clear_content(ui, fg));
    }

    /// Replace the rows, the scroll kept.
    fn rebuild(&self, ui: &mut UiRoot, rows: &[CreatureRow], templates: &mut dyn RowTemplates) {
        let saved = self.scroll_y(ui);
        self.flush(ui);
        let fg = self.foreground;
        for (i, row) in rows.iter().enumerate() {
            let Some(back) = templates.build_row(ui, LAYOUT_ID, CREATURE_ROW_TEMPLATE_ID) else {
                return;
            };
            fill_row(ui, back, None);
            let bg = self.background;
            ui.with::<ListBox, _>(bg, |b, ui| b.add_prebuilt_row(ui, bg, back));
            let Some(front) = templates.build_row(ui, LAYOUT_ID, CREATURE_ROW_TEMPLATE_ID) else {
                return;
            };
            fill_row(ui, front, Some(row));
            let top = i as f32 * CREATURE_ROW_SIZE.1;
            ui.with::<ScrollablePanel, _>(fg, |p, ui| p.add_row(ui, fg, front, top));
        }
        self.set_scroll_y(ui, saved);
    }

    fn scroll_y(&self, ui: &UiRoot) -> i32 {
        ui.widget::<ScrollablePanel>(self.foreground)
            .map_or(0, |p| p.scroll.scroll_y())
    }

    /// Both layers at `y` (the background follows the front one).
    fn set_scroll_y(&self, ui: &mut UiRoot, y: i32) {
        if let Some(p) = ui.widget_mut::<ScrollablePanel>(self.foreground) {
            p.scroll.set_scroll_y(y);
        }
        let y = self.scroll_y(ui);
        let viewport = ui
            .widget::<ListBox>(self.background)
            .and_then(ListBox::viewport_id);
        if let Some(v) = viewport
            && let Some(p) = ui.widget_mut::<ScrollablePanel>(v)
        {
            p.scroll.set_scroll_y(y);
        }
    }

    /// The front rows, in order.
    #[must_use]
    pub fn rows(&self, ui: &UiRoot) -> Vec<ElementId> {
        ui.children(self.foreground)
    }
}

/// One row: the chrome only (`None`), or the text only, the value in its
/// style's colour.
fn fill_row(ui: &mut UiRoot, row: ElementId, text: Option<&CreatureRow>) {
    let label = ui.find_descendant(row, CREATURE_ROW_LABEL_ID);
    let value = ui.find_descendant(row, CREATURE_ROW_VALUE_ID);
    match text {
        None => {
            for t in [label, value].into_iter().flatten() {
                if let Some(w) = ui.widget_mut::<Text>(t) {
                    w.set_lines(Vec::new());
                }
            }
        }
        Some(r) => {
            if let Some(d) = ui.widget_mut::<DatElement>(row) {
                d.media_visible = false;
            }
            if let Some(w) = label.and_then(|t| ui.widget_mut::<Text>(t)) {
                let color = w.default_color;
                w.set_lines(vec![Line::new(r.label.clone(), color)]);
            }
            if let Some(w) = value.and_then(|t| ui.widget_mut::<Text>(t)) {
                let color = r
                    .style
                    .palette_index()
                    .and_then(|i| w.font_color_palette.get(i).copied())
                    .unwrap_or(w.default_color);
                w.set_lines(vec![Line::new(r.value.clone(), color)]);
            }
        }
    }
}

/// The examination window's controller.
#[derive(Debug)]
pub struct AppraisalController {
    item_panel: ElementId,
    creature_panel: ElementId,
    creature_stats: Option<CreatureRowList>,
    creature_extra: Option<CreatureRowList>,
    creature_texts: [Option<ElementId>; 6],
    shown: bool,
    in_combat: bool,
    refresh_elapsed: f64,
    spell_panel: ElementId,
    creature_info: Option<ElementId>,
    character_info: Option<ElementId>,
    title: ElementId,
    item_text: ElementId,
    close: Option<ElementId>,
    spell_school: ElementId,
    spell_mana: ElementId,
    spell_duration: ElementId,
    spell_range: ElementId,
    spell_display: ElementId,
    spell_icon: ElementId,
    formula: ElementId,
    formula_origin_x: f32,
    formula_cell_width: f32,
    formula_cells: Vec<ElementId>,
    page: AppraisalPage,
    item_object: u32,
    creature_object: u32,
    character_object: u32,
    spell_id: u32,
    report: ItemAppraisalReport,
    shaped_width: Option<f32>,
    inscription: Inscription,
    player: u32,
    player_name: String,
}

fn text_of(ui: &UiRoot, layout: &ImportedLayout, id: u32) -> Option<ElementId> {
    layout.find(id).filter(|t| ui.widget::<Text>(*t).is_some())
}

fn set_text(ui: &mut UiRoot, t: ElementId, value: &str) {
    if let Some(w) = ui.widget_mut::<Text>(t) {
        let color = w.default_color;
        let lines = if value.is_empty() {
            Vec::new()
        } else {
            value
                .split('\n')
                .map(|l| Line::new(l.to_owned(), color))
                .collect()
        };
        w.set_lines(lines);
    }
}

impl AppraisalController {
    /// Bind to the imported window (`None` when a page or text is
    /// missing).
    pub fn bind(ui: &mut UiRoot, layout: &ImportedLayout) -> Option<Self> {
        let item_panel = layout.find(ITEM_PANEL_ID)?;
        let creature_panel = layout.find(CREATURE_PANEL_ID)?;
        let spell_panel = layout.find(SPELL_PANEL_ID)?;
        let title = text_of(ui, layout, TITLE_ID)?;
        let item_text = text_of(ui, layout, ITEM_TEXT_ID)?;
        let spell_school = text_of(ui, layout, SPELL_SCHOOL_TEXT_ID)?;
        let spell_mana = text_of(ui, layout, SPELL_MANA_TEXT_ID)?;
        let spell_duration = text_of(ui, layout, SPELL_DURATION_TEXT_ID)?;
        let spell_range = text_of(ui, layout, SPELL_RANGE_TEXT_ID)?;
        let spell_display = text_of(ui, layout, SPELL_DISPLAY_TEXT_ID)?;
        let icon_host = layout.find(SPELL_ICON_ID)?;
        let formula = layout.find(SPELL_FORMULA_LIST_ID)?;
        let close = layout.find(CLOSE_ID).filter(|b| bind_click(ui, *b));

        if let Some(w) = ui.widget_mut::<Text>(item_text) {
            w.vertical_justify = VJustify::Top;
            w.preserve_end_on_layout = false;
            w.wheel_scroll_enabled = true;
        }
        if let Some(e) = ui.element_mut(item_text) {
            e.click_through = false;
        }
        if let Some(bar) = layout.find(ITEM_SCROLLBAR_ID)
            && let Some(b) = ui.widget_mut::<Scrollbar>(bar)
        {
            b.model = ScrollSource::Element(item_text);
        }
        for t in [spell_school, spell_mana, spell_duration, spell_range] {
            if let Some(w) = ui.widget_mut::<Text>(t) {
                w.preserve_end_on_layout = false;
            }
        }
        if let Some(w) = ui.widget_mut::<Text>(spell_display) {
            w.preserve_end_on_layout = false;
            w.one_line = false;
            w.vertical_justify = VJustify::Top;
        }
        // The icon host's own art gives way to the spell's icon.
        if let Some(d) = ui.widget_mut::<DatElement>(icon_host) {
            d.media_visible = false;
        }
        let (w, h) = ui
            .element(icon_host)
            .map_or((32.0, 32.0), |e| (e.width, e.height));
        let spell_icon = ui.add(
            icon_host,
            catalog_element(w, h),
            CatalogSlot {
                empty_sprite: 0,
                ..CatalogSlot::default()
            },
        );
        ui.set_anchors(spell_icon, AnchorEdges::ALL);
        let (formula_origin_x, formula_cell_width) = ui
            .element(formula)
            .map_or((0.0, 32.0), |e| (e.left, e.width));
        let inscription = Inscription::bind(ui, layout);
        // OpenAC: the rows' chrome behind the portrait (stats two below
        // it, extras one below), their text in front. The attribute and
        // vital rows stretch with the window; the extra rows keep their
        // size.
        let viewport_z = layout
            .find(CREATURE_VIEWPORT_ID)
            .and_then(|v| ui.element(v))
            .map(|e| e.z_order);
        let (creature_stats, creature_extra) = match viewport_z {
            Some(z) => (
                layout
                    .find(CREATURE_STATS_LIST_ID)
                    .and_then(|h| CreatureRowList::create(ui, creature_panel, h, z - 2, true)),
                layout
                    .find(CREATURE_EXTRA_LIST_ID)
                    .and_then(|h| CreatureRowList::create(ui, creature_panel, h, z - 1, false)),
            ),
            None => (None, None),
        };
        let creature_texts = [
            CREATURE_LEVEL_VALUE_ID,
            CREATURE_DISPLAY_NAME_ID,
            CHARACTER_HERITAGE_ID,
            CHARACTER_TITLE_TEXT_ID,
            CHARACTER_PLAYER_KILLER_ID,
            CHARACTER_ALLEGIANCE_ID,
        ]
        .map(|id| text_of(ui, layout, id));
        for t in creature_texts.into_iter().flatten() {
            if let Some(w) = ui.widget_mut::<Text>(t) {
                w.preserve_end_on_layout = false;
            }
        }
        let mut c = Self {
            item_panel,
            creature_panel,
            creature_stats,
            creature_extra,
            creature_texts,
            shown: false,
            in_combat: false,
            refresh_elapsed: 0.0,
            spell_panel,
            creature_info: layout.find(CREATURE_INFO_ID),
            character_info: layout.find(CHARACTER_INFO_ID),
            title,
            item_text,
            close,
            spell_school,
            spell_mana,
            spell_duration,
            spell_range,
            spell_display,
            spell_icon,
            formula,
            formula_origin_x,
            formula_cell_width,
            formula_cells: Vec::new(),
            page: AppraisalPage::Item,
            item_object: 0,
            creature_object: 0,
            character_object: 0,
            spell_id: 0,
            report: ItemAppraisalReport::default(),
            shaped_width: None,
            inscription,
            player: 0,
            player_name: String::new(),
        };
        c.show_page(ui, AppraisalPage::Item);
        Some(c)
    }

    /// The page showing.
    #[must_use]
    pub fn page(&self) -> AppraisalPage {
        self.page
    }

    /// The object the page shows (0 for a spell or nothing).
    #[must_use]
    pub fn current_object(&self) -> u32 {
        match self.page {
            AppraisalPage::Item => self.item_object,
            AppraisalPage::Creature => self.creature_object,
            AppraisalPage::Character => self.character_object,
            AppraisalPage::Spell => 0,
        }
    }

    /// The spell the spell page shows.
    #[must_use]
    pub fn spell(&self) -> u32 {
        self.spell_id
    }

    /// The item report showing.
    #[must_use]
    pub fn report(&self) -> &ItemAppraisalReport {
        &self.report
    }

    /// The spell icon cell.
    #[must_use]
    pub fn spell_icon(&self) -> ElementId {
        self.spell_icon
    }

    /// The formula's component cells.
    #[must_use]
    pub fn formula_cells(&self) -> &[ElementId] {
        &self.formula_cells
    }

    fn show_page(&mut self, ui: &mut UiRoot, page: AppraisalPage) {
        self.page = page;
        ui.set_visible(self.item_panel, page == AppraisalPage::Item);
        ui.set_visible(
            self.creature_panel,
            matches!(page, AppraisalPage::Creature | AppraisalPage::Character),
        );
        ui.set_visible(self.spell_panel, page == AppraisalPage::Spell);
        if let Some(e) = self.creature_info {
            ui.set_visible(e, page == AppraisalPage::Creature);
        }
        if let Some(e) = self.character_info {
            ui.set_visible(e, page == AppraisalPage::Character);
        }
    }

    /// Show an appraisal answer: the title, and the item report on the
    /// item page, or the creature or character page (the header lines and
    /// the rows, built from `rows`' templates). False when the object is
    /// not in the table.
    pub fn apply(
        &mut self,
        ui: &mut UiRoot,
        view: &AppraisalView,
        objects: &ClientObjectTable,
        spell: &dyn Fn(u32) -> Option<SpellMetadata>,
        names: &dyn AppraisalNames,
        rows: &mut dyn RowTemplates,
    ) -> bool {
        let a = &view.response;
        let Some(obj) = objects.get(a.guid) else {
            return false;
        };
        set_text(ui, self.title, &appraisal_title(obj, &a.properties, names));
        let page = page_for(a);
        match page {
            AppraisalPage::Item => {
                let newly = self.item_object != a.guid;
                self.item_object = a.guid;
                self.report = item_report(obj, a, spell, names);
                self.shaped_width = None;
                self.shape(ui);
                if newly && let Some(w) = ui.widget_mut::<Text>(self.item_text) {
                    w.scroll.set_scroll_y(0);
                }
                let viewer = InscriptionViewer {
                    player_name: &self.player_name,
                    owned: objects.is_owned_by_object(a.guid, self.player),
                };
                self.inscription.set(ui, obj, a, viewer);
                if newly {
                    self.inscription.scroll_to_top(ui);
                }
            }
            AppraisalPage::Creature => {
                let newly = self.creature_object != a.guid;
                self.creature_object = a.guid;
                self.apply_creature(ui, obj, a, false, newly, names, rows);
            }
            AppraisalPage::Character => {
                let newly = self.character_object != a.guid;
                self.character_object = a.guid;
                self.apply_creature(ui, obj, a, true, newly, names, rows);
            }
            AppraisalPage::Spell => {}
        }
        self.refresh_elapsed = 0.0;
        self.show_page(ui, page);
        true
    }

    /// OpenAC `AppraisalUiController.ApplyCreature`: the level, the
    /// creature's type or the character's identity block and title-bar
    /// name, and both row lists.
    #[allow(clippy::too_many_arguments)]
    fn apply_creature(
        &mut self,
        ui: &mut UiRoot,
        obj: &ClientObject,
        a: &IdentifyObjectResponse,
        character: bool,
        newly: bool,
        names: &dyn AppraisalNames,
        rows: &mut dyn RowTemplates,
    ) {
        self.clear_creature_text(ui);
        let p = &a.properties;
        let [level, display_name, heritage, title, killer, allegiance] = self.creature_texts;
        let mut put = |t: Option<ElementId>, value: &str| {
            if let Some(t) = t {
                set_text(ui, t, value);
            }
        };
        put(level, &level_display(p));
        if character {
            let gender = get_int(p, creature::GENDER);
            let group = get_int(p, creature::HERITAGE_GROUP);
            let fallback = if group == 0 {
                names.creature(get_int(p, creature::CREATURE_TYPE))
            } else {
                String::new()
            };
            put(heritage, &gender_heritage_display(gender, group, &fallback));
            let shown_title = p
                .ints
                .get(&CHARACTER_MARKER_INT_PROPERTY)
                .filter(|id| **id != 0)
                .and_then(|id| names.character_title(id.cast_unsigned()))
                .filter(|t| !t.is_empty())
                .unwrap_or_else(|| get_string(p, TEMPLATE_STRING_PROPERTY).to_owned());
            put(title, &shown_title);
            put(killer, player_killer_display(obj.flags()));
            put(allegiance, &allegiance_display(p));
            let bar = crate::panels::social::rank_titles::compose_full_name(
                get_int(p, creature::ALLEGIANCE_RANK),
                group,
                gender,
                &names.appropriate_name(obj),
            );
            set_text(ui, self.title, &bar);
        } else {
            put(
                display_name,
                &names.creature(get_int(p, creature::CREATURE_TYPE)),
            );
        }
        if let Some(list) = self.creature_stats {
            let stats = a
                .creature_profile
                .as_ref()
                .map(|profile| creature_rows(profile, a.success))
                .unwrap_or_default();
            list.rebuild(ui, &stats, rows);
        }
        if let Some(list) = self.creature_extra {
            let faction = if character {
                names.local_faction_bits()
            } else {
                0
            };
            let extra = creature_extra_rows(p, a.armor_levels.as_ref(), character, faction);
            list.rebuild(ui, &extra, rows);
        }
        if newly {
            for list in [self.creature_stats, self.creature_extra]
                .into_iter()
                .flatten()
            {
                list.set_scroll_y(ui, 0);
            }
        }
    }

    fn clear_creature_text(&self, ui: &mut UiRoot) {
        for t in self.creature_texts.into_iter().flatten() {
            set_text(ui, t, "");
        }
        for list in [self.creature_stats, self.creature_extra]
            .into_iter()
            .flatten()
        {
            list.flush(ui);
        }
    }

    /// The creature page's two row lists (stats, extras), when bound.
    #[must_use]
    pub fn creature_lists(&self) -> (Option<CreatureRowList>, Option<CreatureRowList>) {
        (self.creature_stats, self.creature_extra)
    }

    /// Whether the player is in a combat mode (the creature page then asks
    /// again every 0.75 seconds while it shows).
    pub fn set_in_combat(&mut self, in_combat: bool) {
        self.in_combat = in_combat;
    }

    /// OpenAC `AppraisalUiController.Tick`: while the window shows a
    /// creature or character in combat, true every 0.75 seconds (ask
    /// again about it); anything else starts the count again.
    pub fn refresh_due(&mut self, dt: f64) -> bool {
        if !self.shown
            || !matches!(
                self.page,
                AppraisalPage::Creature | AppraisalPage::Character
            )
            || !self.in_combat
            || self.current_object() == 0
        {
            self.refresh_elapsed = 0.0;
            return false;
        }
        if !dt.is_finite() || dt <= 0.0 {
            return false;
        }
        self.refresh_elapsed += dt;
        if self.refresh_elapsed < CREATURE_REFRESH_SECONDS {
            return false;
        }
        self.refresh_elapsed %= CREATURE_REFRESH_SECONDS;
        true
    }

    /// The selection changed to `selected` (each change once, in order).
    /// While the window shows, the newly selected object is examined, or
    /// (nothing selected) the window closes; a change while it is hidden
    /// does nothing.
    pub fn follow_selection(&mut self, ui: &mut UiRoot, selected: Option<u32>) {
        if !self.shown {
            return;
        }
        match selected {
            Some(id) if id != 0 => emit(
                ui,
                PanelOutput::Session(SessionCommand::Item(ItemCommand::Appraise(
                    ObjectRef::Guid(id),
                ))),
            ),
            _ => {
                emit(ui, PanelOutput::CloseExamination);
                // The window is closing: a later change heard with this
                // one finds it hidden.
                self.shown = false;
            }
        }
    }

    /// The examine key closes the window while it shows (true: the key is
    /// taken); otherwise it is not the window's.
    pub fn handle_input_action(&mut self, ui: &mut UiRoot, action: InputAction) -> bool {
        if !self.shown || action != InputAction::SelectionExamine {
            return false;
        }
        emit(ui, PanelOutput::CloseExamination);
        true
    }

    /// Re-wrap the item report when the text's width changed (a resize).
    pub fn update(&mut self, ui: &mut UiRoot) {
        self.shape(ui);
    }

    fn shape(&mut self, ui: &mut UiRoot) {
        let Some(width) = ui.element(self.item_text).map(|e| e.width) else {
            return;
        };
        if self.shaped_width == Some(width) {
            return;
        }
        self.shaped_width = Some(width);
        let Some(w) = ui.widget_mut::<Text>(self.item_text) else {
            return;
        };
        let font = w.font.clone();
        let measure = move |s: &str| {
            font.as_ref()
                .map_or(s.chars().count() as f32 * 8.0, |f| f.measure_width(s))
        };
        let lines = shape_report(
            &self.report,
            &w.font_color_palette,
            w.default_color,
            (width - 2.0 * w.padding).max(1.0),
            &measure,
        );
        w.set_lines(lines);
    }

    /// Examine a spell locally (no server request): its name, school,
    /// mana, duration, range at `magic_skill`, description and components,
    /// icon and formula.
    pub fn examine_spell(
        &mut self,
        ui: &mut UiRoot,
        spell: &SpellMetadata,
        icon: Option<TextureRef>,
        magic_skill: u32,
        components: &[SpellExamineComponent],
        templates: &mut dyn RowTemplates,
    ) {
        self.spell_id = spell.spell_id;
        set_text(ui, self.title, &spell.name);
        set_text(ui, self.spell_school, &format!("School: {}", spell.school));
        set_text(ui, self.spell_mana, &spell_mana_text(spell));
        set_text(ui, self.spell_duration, &spell_duration_text(spell));
        set_text(ui, self.spell_range, &spell_range_text(spell, magic_skill));
        let display = spell_display_text(spell, components);
        if let Some(w) = ui.widget_mut::<Text>(self.spell_display) {
            let color = w.default_color;
            w.set_content(crate::widgets::TextContent::Wrapped(display));
            w.default_color = color;
            w.scroll.set_scroll_y(0);
        }
        if let Some(s) = ui.widget_mut::<CatalogSlot>(self.spell_icon) {
            s.entry_id = spell.spell_id;
            s.icon = icon;
        }
        self.rebuild_formula(ui, components, templates);
        self.show_page(ui, AppraisalPage::Spell);
    }

    /// The player's items changed: the shown spell's description and
    /// formula again, from `components` as they stand now (the text keeps
    /// its scroll; title, lines and icon stay). Nothing unless the spell
    /// page shows `spell`.
    pub fn refresh_spell_components(
        &mut self,
        ui: &mut UiRoot,
        spell: &SpellMetadata,
        components: &[SpellExamineComponent],
        templates: &mut dyn RowTemplates,
    ) {
        if self.page != AppraisalPage::Spell
            || self.spell_id == 0
            || spell.spell_id != self.spell_id
        {
            return;
        }
        let display = spell_display_text(spell, components);
        if let Some(w) = ui.widget_mut::<Text>(self.spell_display) {
            let color = w.default_color;
            w.set_content(crate::widgets::TextContent::Wrapped(display));
            w.default_color = color;
        }
        self.rebuild_formula(ui, components, templates);
    }

    /// The formula row: one authored cell per component, centred on the
    /// authored row, each showing the component's icon, with the missing
    /// overlay over a component the player does not carry. No cells when
    /// the cell's template cannot be built.
    fn rebuild_formula(
        &mut self,
        ui: &mut UiRoot,
        components: &[SpellExamineComponent],
        templates: &mut dyn RowTemplates,
    ) {
        self.clear_formula(ui);
        if components.is_empty() {
            return;
        }
        let mut cells = Vec::with_capacity(components.len());
        for component in components {
            let Some(cell) = templates.build_row(ui, LAYOUT_ID, SPELL_COMPONENT_TEMPLATE_ID) else {
                for c in cells {
                    ui.destroy(c);
                }
                return;
            };
            if let Some(d) = ui.widget_mut::<DatElement>(cell) {
                d.runtime_image = Some(component.icon);
            }
            if let Some(missing) = ui.find_descendant(cell, SPELL_COMPONENT_MISSING_ID) {
                ui.set_visible(missing, !component.owned);
            }
            cells.push(cell);
        }
        let width = self.formula_cell_width;
        let n = components.len() as f32;
        if let Some(e) = ui.element_mut(self.formula) {
            e.left = self.formula_origin_x - width * 0.5 * (n - 1.0);
            e.width = width * n;
        }
        ui.reset_anchor_capture(self.formula);
        for (i, cell) in cells.into_iter().enumerate() {
            if let Some(e) = ui.element_mut(cell) {
                e.layout_policy = None;
                e.left = i as f32 * width;
                e.top = 0.0;
            }
            ui.set_anchors(cell, AnchorEdges::LEFT | AnchorEdges::TOP);
            ui.add_child(self.formula, cell);
            self.formula_cells.push(cell);
        }
    }

    /// No formula cells; the row back at its authored place and width.
    fn clear_formula(&mut self, ui: &mut UiRoot) {
        for c in self.formula_cells.drain(..) {
            ui.destroy(c);
        }
        if let Some(e) = ui.element_mut(self.formula) {
            e.left = self.formula_origin_x;
            e.width = self.formula_cell_width;
        }
        ui.reset_anchor_capture(self.formula);
    }

    /// Forget everything shown (a new session).
    pub fn reset(&mut self, ui: &mut UiRoot) {
        for t in [
            self.title,
            self.spell_school,
            self.spell_mana,
            self.spell_duration,
            self.spell_range,
            self.spell_display,
        ] {
            set_text(ui, t, "");
        }
        self.report = ItemAppraisalReport::default();
        self.shaped_width = None;
        self.shape(ui);
        self.item_object = 0;
        self.creature_object = 0;
        self.character_object = 0;
        self.spell_id = 0;
        if let Some(s) = ui.widget_mut::<CatalogSlot>(self.spell_icon) {
            s.entry_id = 0;
            s.icon = None;
        }
        self.clear_formula(ui);
        self.inscription.reset(ui);
        self.refresh_elapsed = 0.0;
        self.show_page(ui, AppraisalPage::Item);
        self.clear_creature_text(ui);
    }

    /// Who is looking: the player's object id and name (the inscription
    /// is theirs to write when they hold the item and no one else signed
    /// it).
    pub fn set_player(&mut self, player: u32, name: &str) {
        self.player = player;
        name.clone_into(&mut self.player_name);
    }
}

impl PanelController for AppraisalController {
    fn on_shown(&mut self, _ui: &mut UiRoot) {
        self.shown = true;
    }

    fn on_hidden(&mut self, _ui: &mut UiRoot) {
        self.shown = false;
    }

    // The item report wraps again when its width changed (a resize; nothing
    // otherwise). Both creature row layers scroll together: the front one
    // takes the wheel, the chrome behind follows it.
    fn tick(&mut self, ui: &mut UiRoot, _dt: f64) {
        self.update(ui);
        for list in [self.creature_stats, self.creature_extra]
            .into_iter()
            .flatten()
        {
            let y = list.scroll_y(ui);
            list.set_scroll_y(ui, y);
        }
    }

    fn on_descendant_focus_changed(&mut self, ui: &mut UiRoot, focused: Option<ElementId>) {
        self.inscription.focus_changed(ui, focused);
    }

    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if self.inscription.on_element_message(ui, element, message) {
            return true;
        }
        if Some(element) == self.close {
            if matches!(message, ElementMessage::Clicked { .. }) {
                emit(ui, PanelOutput::CloseExamination);
            }
            return true;
        }
        false
    }
}
