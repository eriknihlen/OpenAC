//! Typed [`Host`] methods for the game's API groups. Every one is a thin
//! wrapper over [`Host::call`] with the matching marker type in
//! [`crate::calls`]; the client's answer is the same with or without a
//! window.

use ac_plugin_abi::calls;
use ac_plugin_abi::character::{
    ActiveEnchantment, AdvancementKind, AdvancementRequest, AdvancementResult, Attribute,
    CharacterExperience, OptionResult, Skill, Vital,
};
use ac_plugin_abi::combat::{
    AttackHeight, AttackRequest, CombatMode, CombatResult, CombatSnapshot, CombatTarget,
};
use ac_plugin_abi::items::ItemResult;
use ac_plugin_abi::magic::{
    CastCompletion, CastGate, CastRequest, CastResult, ComponentInfo, ReportedCast, SPELL_PAGE_MAX,
    SpellInfo, SpellList, TrackedEnchantment,
};
use ac_plugin_abi::world::{
    ActivationCompletion, Contract, EntitySnapshot, Floorplan, IndoorCell, ItemProperties,
    WorldObject, WorldTime,
};

use crate::Host;

/// D. The character.
impl Host<'_> {
    /// The character's experience and luminance: total and unspent
    /// experience, the experience still needed for the next level (none at
    /// the top of the table), luminance held and its cap. `None` before the
    /// character's description arrives and from the moment it leaves the
    /// world. `Event::ExperienceChanged` carries each new reading.
    pub fn experience(&mut self) -> Option<CharacterExperience> {
        self.call::<calls::CharacterExperience>(&())
    }

    /// Every skill the server told, sorted by name.
    pub fn skills(&mut self) -> Vec<Skill> {
        self.call::<calls::CharacterSkills>(&())
    }

    /// One skill by id.
    pub fn skill(&mut self, skill_id: u32) -> Option<Skill> {
        self.call::<calls::CharacterSkill>(&skill_id)
    }

    /// The six attributes.
    pub fn attributes(&mut self) -> Vec<Attribute> {
        self.call::<calls::CharacterAttributes>(&())
    }

    /// Health, stamina and mana.
    pub fn vitals(&mut self) -> Vec<Vital> {
        self.call::<calls::CharacterVitals>(&())
    }

    /// The enchantments in effect on the character.
    pub fn enchantments(&mut self) -> Vec<ActiveEnchantment> {
        self.call::<calls::CharacterEnchantments>(&false)
    }

    /// Every timed enchantment in the multiplicative and additive lists.
    pub fn timed_enchantments(&mut self) -> Vec<ActiveEnchantment> {
        self.call::<calls::CharacterEnchantments>(&true)
    }

    /// Spend experience (or credits, to train) on one stat.
    pub fn advance(&mut self, kind: AdvancementKind, stat_id: u32, cost: u64) -> AdvancementResult {
        self.call::<calls::CharacterAdvance>(&AdvancementRequest {
            kind,
            stat_id,
            cost,
        })
    }

    /// Every character option by name, with its value.
    pub fn character_options(&mut self) -> Vec<(String, bool)> {
        self.call::<calls::CharacterOptions>(&())
    }

    /// One character option's value by name (ignoring case).
    pub fn character_option(&mut self, name: &str) -> Option<bool> {
        self.character_options()
            .into_iter()
            .find(|(n, _)| n.eq_ignore_ascii_case(name))
            .map(|(_, v)| v)
    }

    /// Set one character option by name.
    pub fn set_character_option(&mut self, name: &str, value: bool) -> OptionResult {
        self.call::<calls::CharacterSetOption>(&(name.to_owned(), value))
    }
}

/// C. World reads.
impl Host<'_> {
    /// Every object the client knows, by id.
    pub fn objects(&mut self) -> Vec<WorldObject> {
        self.call::<calls::WorldObjects>(&())
    }

    /// One object.
    pub fn object(&mut self, object_id: u32) -> Option<WorldObject> {
        self.call::<calls::WorldObject>(&object_id)
    }

    /// Every property the client holds for an object.
    pub fn properties(&mut self, object_id: u32) -> Option<ItemProperties> {
        self.call::<calls::WorldProperties>(&object_id)
    }

    /// One integer property of an object.
    pub fn int_property(&mut self, object_id: u32, property: u32) -> Option<i32> {
        self.call::<calls::WorldIntProperty>(&(object_id, property))
    }

    /// Ask for an object's appraisal (the assess window stays closed).
    pub fn identify(&mut self, object_id: u32) -> ItemResult {
        self.call::<calls::WorldIdentify>(&object_id)
    }

    /// Use a world object (walking there first when needed).
    pub fn activate(&mut self, object_id: u32) -> ItemResult {
        self.call::<calls::WorldActivate>(&object_id)
    }

    /// The last activation that ended.
    pub fn last_activation(&mut self) -> ActivationCompletion {
        self.call::<calls::WorldLastActivation>(&())
    }

    /// The open outside container, 0 for none.
    pub fn open_container(&mut self) -> u32 {
        self.call::<calls::WorldOpenContainer>(&())
    }

    /// Every object placed in the world with where it stands.
    pub fn entities(&mut self) -> Vec<EntitySnapshot> {
        self.call::<calls::WorldEntities>(&())
    }

    /// The fixed decoration of the landscape around the character:
    /// buildings, trees, rocks (no server identity; never in a command).
    pub fn scenery(&mut self) -> Vec<EntitySnapshot> {
        self.call::<calls::WorldScenery>(&())
    }

    /// The tracked contracts.
    pub fn contracts(&mut self) -> Vec<Contract> {
        self.call::<calls::WorldContracts>(&())
    }

    /// The game's calendar and time of day.
    pub fn world_time(&mut self) -> WorldTime {
        self.call::<calls::WorldTime>(&())
    }

    /// The landblock the character stands in.
    pub fn current_landblock(&mut self) -> u32 {
        self.call::<calls::DungeonLandblock>(&())
    }

    /// Whether a cell belongs to a sealed dungeon.
    pub fn is_sealed_dungeon(&mut self, cell_id: u32) -> bool {
        self.call::<calls::DungeonIsSealed>(&cell_id)
    }

    /// A landblock's dungeon floorplan.
    pub fn dungeon_floorplan(&mut self, landblock_id: u32) -> Option<Floorplan> {
        self.call::<calls::DungeonFloorplan>(&landblock_id)
    }

    /// Every indoor cell a landblock lists, with its environment piece and
    /// placement as the data stores them (OpenAC `CaptureIndoorCells`), and
    /// from API 1.5 its doorways and the cells the data lists as seen from
    /// it. Read from the game files once per landblock and kept.
    pub fn indoor_cells(&mut self, landblock_id: u32) -> Vec<IndoorCell> {
        self.call::<calls::DungeonIndoorCells>(&landblock_id)
    }

    /// Whether projectile paths can be asked now (in the world).
    pub fn projectiles_available(&mut self) -> bool {
        self.call::<calls::ProjectilesAvailable>(&())
    }

    /// Fly a probe sphere from the character to a target through the
    /// client's collision world (OpenAC `EvaluatePath`, with markers per
    /// step when `query.diagnostics` is set).
    pub fn evaluate_projectile_path(
        &mut self,
        query: &ac_plugin_abi::projectile::ProjectilePathQuery,
    ) -> ac_plugin_abi::projectile::ProjectilePathResult {
        self.call::<calls::ProjectilesEvaluatePath>(query)
    }
}

/// E. Spells and magic.
impl Host<'_> {
    /// Every spell in the spell table, in id order. Read page by page
    /// ([`calls::SpellsPage`], at most `SPELL_PAGE_MAX` spells a reply).
    pub fn all_spells(&mut self) -> Vec<SpellInfo> {
        let mut spells = Vec::new();
        loop {
            let first = u32::try_from(spells.len()).unwrap_or(u32::MAX);
            let page = self.call::<calls::SpellsPage>(&(first, SPELL_PAGE_MAX));
            let done =
                page.spells.is_empty() || spells.len() + page.spells.len() >= page.total as usize;
            spells.extend(page.spells);
            if done {
                return spells;
            }
        }
    }

    /// One spell by id.
    pub fn spell(&mut self, spell_id: u32) -> Option<SpellInfo> {
        self.call::<calls::SpellsGet>(&spell_id)
    }

    /// A spell by name: exact (ignoring case), then with `partial` the
    /// first whose name contains it.
    pub fn find_spell(&mut self, name: &str, partial: bool) -> Option<SpellInfo> {
        self.call::<calls::SpellsFind>(&(name.to_owned(), partial))
    }

    /// One of the known-spell lists. Each call copies the whole list (a
    /// few hundred records for a high-level caster); a plugin that reads it
    /// often keeps it in a [`KnownSpellCache`], which reads it again only
    /// when [`Self::known_spells_revision`] moves.
    pub fn known_spells(&mut self, list: SpellList) -> Vec<SpellInfo> {
        self.call::<calls::SpellsKnown>(&list)
    }

    /// One of the known-spell lists as spell ids only, in the same order:
    /// a small fraction of [`Self::known_spells`]' cost.
    pub fn known_spell_ids(&mut self, list: SpellList) -> Vec<u32> {
        self.call::<calls::SpellsKnownIds>(&list)
    }

    /// The known-spell lists' revision: the same number means the same
    /// lists (all three), so whatever a plugin read of them is still
    /// current. It moves when a spell is learned or forgotten or the
    /// spellbook is replaced (another character), never goes back in a
    /// session, and is 0 before the first spellbook.
    pub fn known_spells_revision(&mut self) -> u64 {
        self.call::<calls::SpellsKnownRevision>(&())
    }

    /// Whether the character knows a spell.
    pub fn is_spell_known(&mut self, spell_id: u32) -> bool {
        self.call::<calls::SpellsIsKnown>(&spell_id)
    }

    /// One spell component.
    pub fn spell_component(&mut self, component_id: u32) -> Option<ComponentInfo> {
        self.call::<calls::SpellsComponent>(&component_id)
    }

    /// Seconds left on a cooldown group.
    pub fn cooldown_remaining(&mut self, cooldown_id: u32) -> f64 {
        self.call::<calls::SpellsCooldown>(&cooldown_id)
    }

    /// Whether the client holds up another cast (OpenAC `IsCasting`; a
    /// cast still waiting for its answer does not).
    pub fn is_casting(&mut self) -> bool {
        self.call::<calls::MagicIsCasting>(&())
    }

    /// Whether a spell could be cast now, at `target_id` (selected first)
    /// or (0) the selection.
    pub fn cast_gate(&mut self, spell_id: u32, target_id: u32) -> CastGate {
        self.call::<calls::MagicGate>(&CastRequest {
            spell_id,
            target_id,
        })
    }

    /// Cast a spell at `target_id` (selected first) or (0) the selection.
    pub fn cast(&mut self, spell_id: u32, target_id: u32) -> CastResult {
        self.call::<calls::MagicCast>(&CastRequest {
            spell_id,
            target_id,
        })
    }

    /// Whether a spell could be cast now at `target_id`, without selecting
    /// it: the selection is neither read nor changed. Refused when the
    /// object is not in the world.
    pub fn cast_gate_without_selecting(&mut self, spell_id: u32, target_id: u32) -> CastGate {
        self.call::<calls::MagicGateWithoutSelecting>(&CastRequest {
            spell_id,
            target_id,
        })
    }

    /// Cast a spell at `target_id` without selecting it: the selection is
    /// neither read nor changed. An incompatible target when the object is
    /// not in the world.
    pub fn cast_without_selecting(&mut self, spell_id: u32, target_id: u32) -> CastResult {
        self.call::<calls::MagicCastWithoutSelecting>(&CastRequest {
            spell_id,
            target_id,
        })
    }

    /// Whether the character carries a spell's components.
    pub fn has_components(&mut self, spell_id: u32) -> bool {
        self.call::<calls::MagicHasComponents>(&spell_id)
    }

    /// The server's answer to the last cast.
    pub fn last_cast(&mut self) -> CastCompletion {
        self.call::<calls::MagicLastCompletion>(&())
    }

    /// Tell the enchantment tracker a spell landed on a target.
    pub fn report_cast(&mut self, target_id: u32, spell_id: u32, duration_seconds: f64) -> bool {
        self.call::<calls::EnchantmentsReport>(&ReportedCast {
            target_id,
            spell_id,
            duration_seconds,
        })
    }

    /// What the tracker believes is on a target.
    pub fn tracked_enchantments(&mut self, target_id: u32) -> Vec<TrackedEnchantment> {
        self.call::<calls::EnchantmentsCapture>(&target_id)
    }

    /// Forget what the tracker holds for a target (0: everything).
    pub fn forget_enchantments(&mut self, target_id: u32) {
        self.call::<calls::EnchantmentsForget>(&target_id);
    }
}

/// F. Combat.
impl Host<'_> {
    /// The combat state now.
    pub fn combat(&mut self) -> CombatSnapshot {
        self.call::<calls::CombatSnapshot>(&())
    }

    /// The hostile creatures within `maximum_metres`.
    pub fn hostile_targets(&mut self, maximum_metres: f32) -> Vec<CombatTarget> {
        self.call::<calls::CombatHostileTargets>(&maximum_metres)
    }

    /// Enter a combat mode.
    pub fn enter_combat_mode(&mut self, mode: CombatMode) -> CombatResult {
        self.call::<calls::CombatEnterMode>(&mode)
    }

    /// Leave peace for the wielded weapon's mode.
    pub fn enter_default_combat_mode(&mut self) -> CombatResult {
        self.call::<calls::CombatEnterDefaultMode>(&())
    }

    /// Start a swing at a creature.
    pub fn begin_attack(
        &mut self,
        target_id: u32,
        height: AttackHeight,
        power: f32,
    ) -> CombatResult {
        self.call::<calls::CombatBeginAttack>(&AttackRequest {
            target_id,
            height,
            power,
        })
    }

    /// Release the swing.
    pub fn release_attack(&mut self) -> CombatResult {
        self.call::<calls::CombatReleaseAttack>(&())
    }

    /// Stop attacking.
    pub fn abort_attack(&mut self) -> CombatResult {
        self.call::<calls::CombatAbortAttack>(&())
    }

    /// Let go of a creature the server stopped talking about.
    pub fn dismiss_ghost(&mut self, target_id: u32) -> CombatResult {
        self.call::<calls::CombatDismissGhost>(&target_id)
    }

    /// Take combat over from the client's automatic targeting and repeat
    /// (`true`) or hand it back; whether this plugin holds it now.
    pub fn combat_control(&mut self, acquire: bool) -> bool {
        self.call::<calls::CombatControl>(&acquire)
    }
}

/// The character's known-spell lists kept across calls: each list is read
/// from the client the first time it is asked for after the lists'
/// revision ([`Host::known_spells_revision`]) moved, and answered from here
/// otherwise, so a plugin that consults the lists every tick pays one small
/// call per read instead of decoding hundreds of records.
#[derive(Debug, Default, Clone)]
pub struct KnownSpellCache {
    /// The revision the kept lists belong to; `None` before the first read.
    revision: Option<u64>,
    /// Self buffs, attacks, combat spells; `None` until read at
    /// `revision`.
    lists: [Option<Vec<SpellInfo>>; 3],
}

impl KnownSpellCache {
    /// An empty cache.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// One known-spell list, as [`Host::known_spells`] answers it now.
    pub fn list(&mut self, host: &mut Host<'_>, list: SpellList) -> &[SpellInfo] {
        let revision = host.known_spells_revision();
        if self.revision != Some(revision) {
            self.revision = Some(revision);
            self.lists = Default::default();
        }
        let slot = &mut self.lists[match list {
            SpellList::SelfBuffs => 0,
            SpellList::Attacks => 1,
            SpellList::Combat => 2,
        }];
        slot.get_or_insert_with(|| host.known_spells(list))
    }

    /// The revision the kept lists belong to, `None` before the first read.
    #[must_use]
    pub fn revision(&self) -> Option<u64> {
        self.revision
    }

    /// Forget the kept lists: the next read asks the client again.
    pub fn clear(&mut self) {
        *self = Self::default();
    }
}
