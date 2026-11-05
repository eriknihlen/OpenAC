//! The game tables the gameplay owners read, decoded once per process from
//! the portal dat and shared by every session: the spell table (names,
//! schools, targets, formulas), the component check's maps, the skill
//! table's formulas, the character-creation options and the contract table
//! the quest tracker names contracts from.

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::chargen::ChargenOptions;
use ac_core::items::component_fill::ComponentDescription;
use ac_core::player::{SkillBase, SkillFormula};
use ac_core::quests::{ContractCatalog, ContractEntry};
use ac_core::spells::{
    ComponentType, SpellBaseRecord, SpellComponentInfo, SpellComponentRequirements,
    SpellFieldOutOfRange, SpellFormulaDefinition, SpellTable, project_spell,
};
use ac_dat::DatCollection;

use crate::communication::ChatPoseCatalog;
use ac_dat::objects::{
    CHAR_GEN_ID, CHAT_AUDIENCE, CHAT_POSE_TABLE_ID, CONTRACT_TABLE_ID, CharGen, ChatPoseTable,
    ContractTable, EXPERIENCE_TABLE_ID, EnumIdMap, ExperienceTable, SKILL_TABLE_ID,
    SPELL_COMPONENT_TABLE_ID, SPELL_TABLE_ID, SkillTable, SpellComponentTable,
    SpellTable as DatSpellTable, TABOO_TABLE_ID, TabooTable,
};

/// The map from spell component ids to the weenie classes that carry them
/// (a dual id map, laid out as the enum id maps are).
const COMPONENT_ID_MAP_ID: u32 = 0x2700_0002;
/// The master map's category of maps whose values name weenie classes.
const MAGIC_PACK_ENUM_CATEGORY: u32 = 0x1000_0001;
/// The slot in that category holding each school's focus (magic pack).
const MAGIC_PACK_ENUM_VALUE: u32 = 4;
/// The slot in that category holding the trade notes (denomination to
/// weenie class).
const TRADE_NOTE_ENUM_VALUE: u32 = 0xA;

/// Why the tables could not be read.
#[derive(Debug, thiserror::Error)]
pub enum TablesError {
    /// No dat directory was named and none is installed.
    #[error("no dat directory")]
    NoDatDirectory,
    /// The dat could not be read or an object did not decode.
    #[error(transparent)]
    Dat(#[from] ac_dat::DatError),
    /// The portal dat has no spell table.
    #[error("the portal dat has no spell table (0x{SPELL_TABLE_ID:08X})")]
    MissingSpellTable,
    /// A spell record does not fit the client's fields.
    #[error(transparent)]
    SpellField(#[from] SpellFieldOutOfRange),
    /// The spell table names a spell twice.
    #[error(transparent)]
    DuplicateSpell(#[from] ac_core::spells::DuplicateSpellId),
}

/// The language filter's banned chat patterns, as the dats gave them.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub enum ChatTaboo {
    /// No dats were read: the filter has nothing to censor.
    #[default]
    NotLoaded,
    /// The dats have no taboo table: the filter censors every word.
    Missing,
    /// The chat audience's patterns (empty when the table has none for
    /// chat).
    Patterns(Vec<String>),
}

impl ChatTaboo {
    /// The patterns the chat log censors against: `None` censors every
    /// word, an empty list nothing.
    #[must_use]
    pub fn patterns(&self) -> Option<Vec<String>> {
        match self {
            Self::NotLoaded => Some(Vec::new()),
            Self::Missing => None,
            Self::Patterns(patterns) => Some(patterns.clone()),
        }
    }
}

/// One spell component as the component table describes it, with the
/// weenie class that carries it.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ComponentDetails {
    /// The weenie class carrying it.
    pub weenie_class_id: u32,
    /// Its name.
    pub name: String,
    /// Its category (the component book's sort).
    pub category: u32,
    /// Its icon.
    pub icon: u32,
    /// Its type (`ComponentType`).
    pub component_type: u32,
    /// The gesture a cast makes for it.
    pub gesture: u32,
    /// The gesture's speed.
    pub gesture_speed: f32,
    /// Its burn rate (the component-destruction modifier).
    pub burn_rate: f32,
    /// The word a cast speaks for it.
    pub word: String,
}

/// The decoded tables. Cheap to share: sessions hold it behind an [`Arc`].
#[derive(Debug, Default)]
pub struct GameTables {
    spells: Arc<SpellTable>,
    components: SpellComponentRequirements,
    component_names: HashMap<u32, String>,
    /// Each spell component's category and name, by the weenie class that
    /// carries it (the component book and `/fillcomps` read it).
    components_by_class: HashMap<u32, ComponentDescription>,
    /// Every spell component carried by a weenie class, by its component
    /// id, as the dats describe it (plugins read it).
    component_details: HashMap<u32, ComponentDetails>,
    magic_packs: HashMap<u32, u32>,
    skills: HashMap<u32, SkillBase>,
    /// Each trade note's value in pyreals, by its weenie class.
    trade_notes: Arc<HashMap<u32, i32>>,
    chargen: Arc<ChargenOptions>,
    contracts: Arc<ContractCatalog>,
    /// The language filter's banned patterns for chat.
    chat_taboo: ChatTaboo,
    /// The poses speech can carry.
    chat_poses: Arc<ChatPoseCatalog>,
    /// The display names of creature kinds (a plugin's species name).
    creature_names: Arc<ac_core::names::CreatureDisplayNames>,
    /// The experience table's level curve: the total experience each level
    /// starts at, by level (`None` when the dats hold no table).
    experience_levels: Option<Arc<[u64]>>,
}

impl GameTables {
    /// No tables: spells have no names or formulas, the component check
    /// fails every spell, skill formula terms read 0.
    #[must_use]
    pub fn empty() -> Self {
        Self::default()
    }

    /// Tables built from already-decoded parts (tests, tools).
    #[must_use]
    pub fn from_parts(
        spells: SpellTable,
        components: SpellComponentRequirements,
        skills: HashMap<u32, SkillBase>,
    ) -> Self {
        Self {
            spells: Arc::new(spells),
            components,
            component_names: HashMap::new(),
            components_by_class: HashMap::new(),
            component_details: HashMap::new(),
            magic_packs: HashMap::new(),
            skills,
            trade_notes: Arc::default(),
            chargen: Arc::default(),
            contracts: Arc::default(),
            chat_taboo: ChatTaboo::default(),
            chat_poses: Arc::default(),
            creature_names: Arc::default(),
            experience_levels: None,
        }
    }

    /// The same tables with this level curve (tests, tools).
    #[must_use]
    pub fn with_experience_levels(mut self, levels: Vec<u64>) -> Self {
        self.experience_levels = Some(levels.into());
        self
    }

    /// The experience table's level curve, `None` when the dats hold none.
    #[must_use]
    pub fn experience_levels(&self) -> Option<&[u64]> {
        self.experience_levels.as_deref()
    }

    /// The same tables with these speech poses (tests, tools).
    #[must_use]
    pub fn with_chat_poses(mut self, chat_poses: ChatPoseCatalog) -> Self {
        self.chat_poses = Arc::new(chat_poses);
        self
    }

    /// The poses speech can carry (empty when the dat has no table).
    #[must_use]
    pub fn chat_poses(&self) -> &Arc<ChatPoseCatalog> {
        &self.chat_poses
    }

    /// The same tables with these banned chat patterns (tests, tools).
    #[must_use]
    pub fn with_chat_taboo(mut self, chat_taboo: ChatTaboo) -> Self {
        self.chat_taboo = chat_taboo;
        self
    }

    /// The same tables with these character-creation options (tests, tools).
    #[must_use]
    pub fn with_chargen(mut self, chargen: ChargenOptions) -> Self {
        self.chargen = Arc::new(chargen);
        self
    }

    /// The same tables with this contract catalog (tests, tools).
    #[must_use]
    pub fn with_contracts(mut self, contracts: ContractCatalog) -> Self {
        self.contracts = Arc::new(contracts);
        self
    }

    /// Open the dats in `dir` (else the usual install place) and read the
    /// tables.
    ///
    /// # Errors
    /// No dat directory, or [`GameTables::load`]'s errors.
    pub fn open(dir: Option<&std::path::Path>) -> Result<Self, TablesError> {
        let dir = dir
            .map(std::path::Path::to_path_buf)
            .or_else(ac_dat::default_dat_dir)
            .ok_or(TablesError::NoDatDirectory)?;
        Self::load(&DatCollection::open(dir)?)
    }

    /// Read the spell table, the spell component table and its weenie-class
    /// map, each school's focus, the skill table, the character-generation
    /// table and the contract table.
    ///
    /// # Errors
    /// A dat read or decode failure, a missing spell table, or a spell
    /// record that does not fit.
    pub fn load(dats: &DatCollection) -> Result<Self, TablesError> {
        let component_table: Option<SpellComponentTable> = dats.get(SPELL_COMPONENT_TABLE_ID)?;
        let component_ids: Option<EnumIdMap> = dats.portal.get(COMPONENT_ID_MAP_ID)?;
        let mut wcid_by_scid = HashMap::new();
        let mut component_names = HashMap::new();
        let mut components_by_class = HashMap::new();
        let mut component_details = HashMap::new();
        let mut infos = HashMap::new();
        if let Some(table) = &component_table {
            for (scid, component) in &table.components {
                infos.insert(
                    *scid,
                    SpellComponentInfo {
                        component_type: ComponentType(component.component_type),
                        text: component.text.clone(),
                    },
                );
                component_names.insert(*scid, component.name.clone());
                if let Some(wcid) = component_ids.as_ref().and_then(|m| m.client_id(*scid)) {
                    wcid_by_scid.insert(*scid, wcid);
                    component_details.insert(
                        *scid,
                        ComponentDetails {
                            weenie_class_id: wcid,
                            name: component.name.clone(),
                            category: component.category,
                            icon: component.icon,
                            component_type: component.component_type,
                            gesture: component.gesture,
                            gesture_speed: component.time,
                            burn_rate: component.cdm,
                            word: component.text.clone(),
                        },
                    );
                    components_by_class.insert(
                        wcid,
                        ComponentDescription {
                            category: component.category,
                            name: component.name.clone(),
                        },
                    );
                }
            }
        }

        let spell_table: DatSpellTable = dats
            .get(SPELL_TABLE_ID)?
            .ok_or(TablesError::MissingSpellTable)?;
        let infos = component_table.as_ref().map(|_| &infos);
        let mut metadata = Vec::with_capacity(spell_table.spells.len());
        let mut formulas = HashMap::new();
        for (id, spell) in &spell_table.spells {
            let record = SpellBaseRecord {
                name: spell.name.clone(),
                description: spell.description.clone(),
                components: spell.components().to_vec(),
                school: spell.school.cast_unsigned(),
                icon: spell.icon,
                category: spell.category,
                bitfield: spell.bitfield,
                base_mana: spell.base_mana,
                base_range_constant: spell.base_range_constant,
                base_range_mod: spell.base_range_mod,
                power: spell.power,
                spell_economy_mod: spell.spell_economy_mod,
                formula_version: spell.formula_version,
                component_loss: spell.component_loss,
                meta_spell_type: spell.meta_spell_type,
                duration: spell.duration,
                caster_effect: spell.caster_effect,
                target_effect: spell.target_effect,
                fizzle_effect: spell.fizzle_effect,
                recovery_interval: spell.recovery_interval,
                recovery_amount: spell.recovery_amount,
                display_order: spell.display_order,
                non_component_target_type: spell.non_component_target_type,
                mana_mod: spell.mana_mod,
                degrade_modifier: spell.degrade_modifier,
                degrade_limit: spell.degrade_limit,
                portal_lifetime: spell.portal_lifetime,
            };
            let projected = project_spell(*id, &record, infos)?;
            formulas.insert(
                *id,
                SpellFormulaDefinition {
                    formula_version: projected.formula_version,
                    component_ids: projected.formula_components.clone(),
                    school: projected.school_id.0,
                },
            );
            metadata.push(projected);
        }

        let magic_packs = magic_packs(dats)?;
        let trade_notes = trade_note_denominations(dats)?;
        let skills = dats
            .get::<SkillTable>(SKILL_TABLE_ID)?
            .map(|table| {
                table
                    .skills
                    .into_iter()
                    .map(|(id, skill)| (id, skill_base(skill)))
                    .collect()
            })
            .unwrap_or_default();
        let chargen = dats
            .get::<CharGen>(CHAR_GEN_ID)?
            .map(|table| crate::session::project_chargen(&table, &skills))
            .unwrap_or_default();
        let contracts = dats
            .get::<ContractTable>(CONTRACT_TABLE_ID)?
            .map(|table| contract_catalog(&table))
            .unwrap_or_default();
        let chat_taboo = match dats.get::<TabooTable>(TABOO_TABLE_ID)? {
            None => ChatTaboo::Missing,
            Some(table) => ChatTaboo::Patterns(
                table
                    .patterns(CHAT_AUDIENCE)
                    .map(<[String]>::to_vec)
                    .unwrap_or_default(),
            ),
        };
        let chat_poses = dats
            .get::<ChatPoseTable>(CHAT_POSE_TABLE_ID)?
            .map(|table| ChatPoseCatalog::from_table(&table))
            .unwrap_or_default();
        let experience_levels = dats
            .get::<ExperienceTable>(EXPERIENCE_TABLE_ID)?
            .map(|table| Arc::from(table.levels));

        Ok(Self {
            spells: Arc::new(SpellTable::create(metadata)?),
            components: SpellComponentRequirements::new(
                formulas,
                wcid_by_scid,
                magic_packs.clone(),
            ),
            component_names,
            components_by_class,
            component_details,
            magic_packs,
            skills,
            chargen: Arc::new(chargen),
            trade_notes: Arc::new(trade_notes),
            contracts: Arc::new(contracts),
            chat_taboo,
            chat_poses: Arc::new(chat_poses),
            creature_names: Arc::new(ac_core::names::CreatureDisplayNames::load(|id| {
                dats.get::<ac_dat::objects::EnumMapper>(id)
                    .ok()
                    .flatten()
                    .map(|m| ac_core::names::EnumMapping {
                        base_enum_map: m.base_enum_map,
                        id_to_string: m.id_to_string,
                    })
            })),
            experience_levels,
        })
    }

    /// A spell component's category and name, by the weenie class that
    /// carries it.
    #[must_use]
    pub fn component_description(&self, weenie_class_id: u32) -> Option<ComponentDescription> {
        self.components_by_class.get(&weenie_class_id).cloned()
    }

    /// The spell table.
    #[must_use]
    pub fn spells(&self) -> &Arc<SpellTable> {
        &self.spells
    }

    /// The component check.
    #[must_use]
    pub fn components(&self) -> &SpellComponentRequirements {
        &self.components
    }

    /// A spell component carried by a weenie class, by its component id.
    #[must_use]
    pub fn component_details(&self, component_id: u32) -> Option<&ComponentDetails> {
        self.component_details.get(&component_id)
    }

    /// A spell component's name.
    #[must_use]
    pub fn component_name(&self, component_id: u32) -> Option<&str> {
        self.component_names.get(&component_id).map(String::as_str)
    }

    /// The skill table, by skill id.
    #[must_use]
    pub fn skills(&self) -> &HashMap<u32, SkillBase> {
        &self.skills
    }

    /// The character-creation options (empty when the dat has no table).
    #[must_use]
    pub fn chargen(&self) -> &Arc<ChargenOptions> {
        &self.chargen
    }

    /// The contract catalog (empty when the dat has no table).
    #[must_use]
    pub fn contracts(&self) -> &Arc<ContractCatalog> {
        &self.contracts
    }

    /// The language filter's banned chat patterns.
    #[must_use]
    pub fn chat_taboo(&self) -> &ChatTaboo {
        &self.chat_taboo
    }

    /// The display names of creature kinds.
    #[must_use]
    pub fn creature_names(&self) -> &ac_core::names::CreatureDisplayNames {
        &self.creature_names
    }

    /// Tables with these creature names (tests).
    #[must_use]
    pub fn with_creature_names(mut self, names: ac_core::names::CreatureDisplayNames) -> Self {
        self.creature_names = Arc::new(names);
        self
    }

    /// A school's focus (magic pack) weenie class.
    #[must_use]
    pub fn magic_pack_wcid(&self, school: u32) -> Option<u32> {
        self.magic_packs.get(&school).copied()
    }

    /// Each trade note's value in pyreals, by its weenie class: only these
    /// classes pay as trade notes.
    #[must_use]
    pub fn trade_notes(&self) -> &Arc<HashMap<u32, i32>> {
        &self.trade_notes
    }

    /// The same tables with these trade notes (tests, tools).
    #[must_use]
    pub fn with_trade_notes(mut self, notes: HashMap<u32, i32>) -> Self {
        self.trade_notes = Arc::new(notes);
        self
    }
}

/// The trade notes: the master map names the category's map, whose slot
/// names the dual map from each denomination (pyreals) to its weenie class.
fn trade_note_denominations(dats: &DatCollection) -> Result<HashMap<u32, i32>, TablesError> {
    let master_id = dats.portal.header().master_map_id.cast_unsigned();
    if master_id == 0 {
        return Ok(HashMap::new());
    }
    let resolve = |map_id: u32, key: u32| -> Result<Option<u32>, TablesError> {
        Ok(dats
            .portal
            .get::<EnumIdMap>(map_id)?
            .and_then(|m| m.client_id(key)))
    };
    let Some(category) = resolve(master_id, MAGIC_PACK_ENUM_CATEGORY)? else {
        return Ok(HashMap::new());
    };
    let Some(notes_id) = resolve(category, TRADE_NOTE_ENUM_VALUE)? else {
        return Ok(HashMap::new());
    };
    Ok(dats
        .portal
        .get::<ac_dat::objects::DualEnumIdMap>(notes_id)?
        .map(|m| {
            m.map
                .client_enum_to_id
                .into_iter()
                .filter_map(|(value, wcid)| {
                    let value = i32::try_from(value).ok().filter(|v| *v > 0)?;
                    (wcid != 0).then_some((wcid, value))
                })
                .collect()
        })
        .unwrap_or_default())
}

/// Each school's focus weenie class: the master map names the category's
/// map, whose slot names the focus map.
///
/// # Errors
/// When a map cannot be read.
pub fn magic_packs(dats: &DatCollection) -> Result<HashMap<u32, u32>, TablesError> {
    let master_id = dats.portal.header().master_map_id.cast_unsigned();
    if master_id == 0 {
        return Ok(HashMap::new());
    }
    let resolve = |map_id: u32, key: u32| -> Result<Option<u32>, TablesError> {
        Ok(dats
            .portal
            .get::<EnumIdMap>(map_id)?
            .and_then(|m| m.client_id(key)))
    };
    let Some(category) = resolve(master_id, MAGIC_PACK_ENUM_CATEGORY)? else {
        return Ok(HashMap::new());
    };
    let Some(packs_id) = resolve(category, MAGIC_PACK_ENUM_VALUE)? else {
        return Ok(HashMap::new());
    };
    Ok(dats
        .portal
        .get::<EnumIdMap>(packs_id)?
        .map(|m| m.client_enum_to_id.into_iter().collect())
        .unwrap_or_default())
}

/// The contract table as the quest tracker reads it: words, quest flags and
/// the cells its people stand in.
fn contract_catalog(table: &ContractTable) -> ContractCatalog {
    ContractCatalog::new(
        table
            .contracts
            .iter()
            .map(|(key, c)| {
                (
                    *key,
                    ContractEntry {
                        version: c.version,
                        contract_id: c.contract_id,
                        contract_name: c.contract_name.clone(),
                        description: c.description.clone(),
                        description_progress: c.description_progress.clone(),
                        name_npc_start: c.name_npc_start.clone(),
                        name_npc_end: c.name_npc_end.clone(),
                        questflag_stamped: c.questflag_stamped.clone(),
                        questflag_started: c.questflag_started.clone(),
                        questflag_finished: c.questflag_finished.clone(),
                        questflag_progress: c.questflag_progress.clone(),
                        questflag_timer: c.questflag_timer.clone(),
                        questflag_repeat_time: c.questflag_repeat_time.clone(),
                        location_npc_start_cell: c.location_npc_start.cell_id,
                        location_npc_end_cell: c.location_npc_end.cell_id,
                        location_quest_area_cell: c.location_quest_area.cell_id,
                    },
                )
            })
            .collect(),
    )
}

fn skill_base(skill: ac_dat::objects::SkillBase) -> SkillBase {
    let f = skill.formula;
    SkillBase {
        name: skill.name,
        description: skill.description,
        min_level: skill.min_level,
        trained_cost: skill.trained_cost,
        specialized_cost: skill.specialized_cost,
        formula: SkillFormula {
            additive_bonus: f.additive_bonus,
            attribute1_multiplier: f.attribute1_multiplier,
            attribute2_multiplier: f.attribute2_multiplier,
            divisor: f.divisor,
            attribute1: f.attribute1,
            attribute2: f.attribute2,
        },
    }
}

#[cfg(test)]
mod tests {
    //! Installed-dat conformance: OpenAC
    //! `tests/AcDream.Content.Tests/InstalledMagicCatalogFociTests.cs`.
    use super::*;

    fn dats() -> DatCollection {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(std::path::PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        DatCollection::open(dir).expect("open the retail dats")
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_foci_map_names_every_school_s_focus() {
        let tables = GameTables::load(&dats()).unwrap();
        // InstalledMagicCatalogFociTests.FociMap_ResolvesEveryRetailSchoolToAcesFociWcid.
        assert_eq!(
            [1, 2, 3, 4, 5].map(|school| tables.magic_pack_wcid(school)),
            [
                Some(15271),
                Some(15270),
                Some(15269),
                Some(15268),
                Some(43173)
            ]
        );
    }

    // TradeNoteDenominationsInstalledDatTests.TheInstalledMapGivesEachNoteClassItsValue
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_trade_note_map_gives_each_note_class_its_value() {
        let tables = GameTables::load(&dats()).unwrap();
        let notes = tables.trade_notes();
        assert_eq!(notes.len(), 14);
        assert_eq!(notes[&2621], 100);
        assert_eq!(notes[&2622], 500);
        assert_eq!(notes[&2623], 1_000);
        assert_eq!(notes[&2624], 5_000);
        assert_eq!(notes[&2625], 10_000);
        assert_eq!(notes[&20630], 250_000);
        assert!(!notes.contains_key(&273));
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_creature_kinds_are_named_for_plugins() {
        let tables = GameTables::load(&dats()).unwrap();
        // The retail chain from 0x2200000E (crates/ac-dat/tests/retail_ui.rs).
        let names = tables.creature_names();
        assert_eq!(names.resolve(1), "Olthoi");
        assert_eq!(names.resolve(77), "Ghost");
        assert_eq!(names.resolve(0), "");
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_spell_and_skill_tables_load() {
        let tables = GameTables::load(&dats()).unwrap();
        // RetailSpellMetadataProjectorTests: 6266 spells; the component set
        // separates self from other across every tier.
        assert_eq!(tables.spells.len(), 6266);
        let set = |id: u32| tables.spells.get(id).unwrap().component_set;
        let strength_self = ac_core::spells::SpellComponentSet {
            herb: 7,
            powder: 33,
            potion: 44,
            talisman: 60,
        };
        assert_eq!(set(0x0002), strength_self);
        assert_eq!(set(0x0534), strength_self);
        assert_eq!(set(0x0539).talisman, 49);
        assert_eq!(tables.spells.get(1).unwrap().name, "Strength Other I");
        assert_eq!(tables.skills[&24].name, "Run");
    }

    /// OpenAC `InteractionRetainedUiComposition.LoadFilterLanguagePatterns`:
    /// the chat audience (1) of the taboo table.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_taboo_table_gives_the_chat_patterns() {
        let tables = GameTables::load(&dats()).unwrap();
        let ChatTaboo::Patterns(patterns) = tables.chat_taboo() else {
            panic!("no taboo table: {:?}", tables.chat_taboo());
        };
        assert!(!patterns.is_empty());
        assert_eq!(
            ac_core::chat::language_filter::censor("hello there", Some(patterns)),
            "hello there"
        );
    }

    /// OpenAC `ChatPoseCatalog.Load`: each word names its motion by the
    /// motion command's name; the dat's chat pose table gives `waving` →
    /// `WAVESTATE` and the Cringe lines.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_pose_table_gives_the_speech_poses() {
        let tables = GameTables::load(&dats()).unwrap();
        let poses = tables.chat_poses();
        assert_eq!(poses.len(), 309, "every word names a motion command");
        let waving = poses.resolve("WAVING", true).unwrap();
        assert_eq!(waving.motion_command, ac_motion::motion_command::WAVE_STATE);
        let cringe = poses
            .resolve("cringe", false)
            .expect("cringe is a pose word");
        assert_eq!(cringe.motion_command, ac_motion::motion_command::CRINGE);
        assert_eq!(cringe.self_text, "cringe in terror.");
        assert_eq!(cringe.others_text, "cringes in terror.");
    }

    /// OpenAC `tests/AcDream.Content.Tests/ContractTableReaderInstalledDatTests.cs`.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_contract_table_names_the_quest_tracker_s_contracts() {
        let tables = GameTables::load(&dats()).unwrap();
        let catalog = tables.contracts();
        // 322 in the installed build; a floor, so another dat revision is
        // not a failure.
        assert!(catalog.len() >= 300, "{} contracts", catalog.len());
        let named = catalog
            .contracts()
            .values()
            .filter(|c| !c.contract_name.is_empty())
            .count();
        assert!(named > catalog.len() / 2, "{named} named");
        let offenders: Vec<String> = catalog
            .contracts()
            .values()
            .filter(|c| !c.description_progress.is_empty())
            .filter(|c| specifiers(&c.description_progress) != 1)
            .map(|c| format!("0x{:08X} {:?}", c.contract_id, c.description_progress))
            .collect();
        assert!(offenders.is_empty(), "{offenders:?}");
        assert!(std::ptr::eq(
            catalog.lookup(0xDEAD_BEEF),
            ContractEntry::unknown()
        ));
    }

    fn specifiers(format: &str) -> usize {
        let bytes = format.as_bytes();
        bytes
            .windows(2)
            .filter(|w| w[0] == b'%' && w[1] != b'%')
            .count()
    }
}
