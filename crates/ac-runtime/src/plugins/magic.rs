//! API group E: spells and magic (OpenAC `ISpellCatalog`, `IMagicCommands`,
//! `IEnchantmentAutomation`) over the spellbook, the spell table, the cast
//! owner, and the enchantment tracker plugins share.

use std::collections::BTreeMap;
use std::sync::Arc;

use ac_core::spells::{SpellMetadata, SpellTable, Spellbook, can_target_self};
use ac_plugin::abi::magic::{
    CastCompletion, CastGate, CastRequest, CastResult, ComponentInfo, ReportedCast, SPELL_PAGE_MAX,
    SpellInfo, SpellList, SpellPage, TrackedEnchantment,
};

use super::{PluginBinding, Ran};
use crate::gameplay::GameplayCommand;
use crate::gameplay::magic::{CastRequestResult, SpellCastGate, cast_gate};
use crate::inventory::ObjectRef;
use crate::session::SessionCommand;

/// The damage-over-time spell flag.
const DAMAGE_OVER_TIME: u32 = 0x0001_0000;

/// The magic skill a school is cast with (OpenAC `SchoolSkillId`).
fn school_skill(school: u32) -> u32 {
    match school {
        4 => 31, // creature enchantment
        3 => 32, // item enchantment
        2 => 33, // life magic
        1 => 34, // war magic
        5 => 43, // void magic
        _ => 0,
    }
}

/// One spell as a plugin sees it (OpenAC `Project(SpellMetadata)`).
pub(super) fn project_spell(meta: &SpellMetadata) -> SpellInfo {
    let set = meta.component_set;
    SpellInfo {
        spell_id: meta.spell_id,
        name: meta.name.clone(),
        family: meta.family,
        tier: meta.generation,
        difficulty: meta.difficulty,
        mana_cost: meta.mana_cost,
        duration_seconds: meta.duration,
        school: school_skill(meta.school_id.0),
        description: meta.description.clone(),
        is_self_targeted: meta.is_self_targeted(),
        is_beneficial: meta.is_beneficial(),
        is_debuff: meta.is_debuff,
        is_offensive: meta.is_offensive,
        is_fellowship: meta.is_fellowship,
        is_untargeted: meta.is_untargeted,
        requires_turn_to: !(222..=235).contains(&meta.family) && !meta.is_untargeted,
        is_projectile: meta.is_projectile(),
        is_damage_over_time: meta.flags.0 & DAMAGE_OVER_TIME != 0,
        raw_flags: meta.flags.0,
        spell_type: meta.spell_type.0.cast_signed(),
        target_mask: meta.target_mask,
        base_range_constant: meta.base_range_constant,
        base_range_modifier: meta.base_range_modifier,
        formula_component_ids: meta.formula_components.clone(),
        icon_id: meta.icon_id,
        saying: meta.saying.clone(),
        component_set: (set.herb, set.powder, set.potion, set.talisman),
    }
}

/// The known spells sorted into OpenAC's three lists
/// (`RebuildSpellbook`): self buffs (beneficial, castable on the caster)
/// by family then highest tier; direct attacks by tier then difficulty;
/// combat spells (offensive or debuffs) by tier then name.
pub(super) fn known_lists(book: &Spellbook) -> [Vec<SpellInfo>; 3] {
    let mut buffs = Vec::new();
    let mut attacks = Vec::new();
    let mut combat = Vec::new();
    for spell_id in book.learned_spells() {
        let Some(meta) = book.try_get_metadata(spell_id) else {
            continue;
        };
        if meta.is_offensive || meta.is_debuff {
            combat.push(project_spell(meta));
        }
        if !meta.is_beneficial() || meta.is_debuff || meta.is_untargeted {
            if meta.is_offensive
                && !meta.is_debuff
                && !meta.is_beneficial()
                && !meta.is_self_targeted()
                && !meta.is_untargeted
                && meta.target_mask != 0
            {
                attacks.push(project_spell(meta));
            }
            continue;
        }
        if !can_target_self(meta) {
            continue;
        }
        buffs.push(project_spell(meta));
    }
    buffs.sort_by(|a, b| a.family.cmp(&b.family).then(b.tier.cmp(&a.tier)));
    attacks.sort_by(|a, b| b.tier.cmp(&a.tier).then(b.difficulty.cmp(&a.difficulty)));
    combat.sort_by(|a, b| b.tier.cmp(&a.tier).then_with(|| a.name.cmp(&b.name)));
    [buffs, attacks, combat]
}

/// The known-spell lists as last built, with their revision. OpenAC builds
/// its three lists when the spellbook changes and hands plugins the same
/// lists until the next change; across the wasm boundary a plugin gets a
/// copy, so the revision tells it whether the copy it kept is still the
/// lists.
#[derive(Debug, Default)]
pub struct KnownSpells {
    /// What the lists were built from: the spell table and the learned
    /// spell ids, in id order; `None` while there is no spellbook.
    source: Option<(Arc<SpellTable>, Vec<u32>)>,
    /// Advances on every rebuild; 0 before the first.
    revision: u64,
    /// Self buffs, attacks, combat spells ([`known_lists`]).
    lists: [Vec<SpellInfo>; 3],
}

impl KnownSpells {
    /// Rebuilds the lists (and advances the revision) when `book`'s spell
    /// table or learned spells differ from what they were built from. The
    /// lists depend on nothing else: a spell's power, the favorites and the
    /// filter leave them as they are.
    pub(super) fn refresh(&mut self, book: Option<&Spellbook>) {
        let same = match (&self.source, book) {
            (None, None) => true,
            (Some((table, learned)), Some(book)) => {
                Arc::ptr_eq(table, book.metadata())
                    && learned.iter().copied().eq(book.learned_spells())
            }
            _ => false,
        };
        if same {
            return;
        }
        self.revision += 1;
        match book {
            None => {
                self.source = None;
                self.lists = Default::default();
            }
            Some(book) => {
                self.source = Some((Arc::clone(book.metadata()), book.learned_spells().collect()));
                self.lists = known_lists(book);
            }
        }
    }

    /// The revision of the lists as last built.
    pub(super) fn revision(&self) -> u64 {
        self.revision
    }

    /// One list as last built.
    pub(super) fn list(&self, list: SpellList) -> &[SpellInfo] {
        let index = match list {
            SpellList::SelfBuffs => 0,
            SpellList::Attacks => 1,
            SpellList::Combat => 2,
        };
        &self.lists[index]
    }
}

/// What the plugins report and the client confirms of enchantments on
/// targets (OpenAC's enchantment ledger): one entry per target and spell,
/// the later expiry winning; wall time, so every plugin reads the same
/// seconds left.
#[derive(Debug, Default)]
pub struct EnchantmentTracker {
    entries: BTreeMap<(u32, u32), Tracked>,
    /// The cast completion the tracker last took in.
    seen_completion: u64,
}

#[derive(Debug, Clone, Copy, PartialEq)]
struct Tracked {
    family: u32,
    quality: i32,
    untargeted: bool,
    expires_unix_ms: f64,
}

impl EnchantmentTracker {
    /// `spell` landed on `target` for `duration` seconds at `now_ms`.
    fn track(&mut self, target: u32, meta: &SpellMetadata, duration: f64, now_ms: i64) {
        #[allow(clippy::cast_precision_loss)]
        let expires = now_ms as f64 + duration * 1000.0;
        let tracked = Tracked {
            family: meta.family,
            quality: meta.difficulty,
            untargeted: meta.is_untargeted,
            expires_unix_ms: expires,
        };
        let key = (target, meta.spell_id);
        if self
            .entries
            .get(&key)
            .is_none_or(|old| tracked.expires_unix_ms > old.expires_unix_ms)
        {
            self.entries.insert(key, tracked);
        }
    }

    /// A plugin reports a cast (OpenAC `ReportCast`): refused without a
    /// target, a spell, a known spell, or a positive finite duration.
    pub(super) fn report(
        &mut self,
        book: Option<&Spellbook>,
        cast: &ReportedCast,
        now_ms: i64,
    ) -> bool {
        if cast.target_id == 0
            || cast.spell_id == 0
            || !cast.duration_seconds.is_finite()
            || cast.duration_seconds <= 0.0
        {
            return false;
        }
        let Some(meta) = book.and_then(|b| b.try_get_metadata(cast.spell_id)) else {
            return false;
        };
        self.track(cast.target_id, meta, cast.duration_seconds, now_ms);
        true
    }

    /// The client's own successful cast of a lasting spell at a target is
    /// taken in once (OpenAC `ObserveSuccessfulLocalCast`).
    pub(super) fn observe(&mut self, book: Option<&Spellbook>, last: CastCompletion, now_ms: i64) {
        let revision = u64::try_from(last.revision).unwrap_or(0);
        if revision == 0 || revision <= self.seen_completion {
            return;
        }
        self.seen_completion = revision;
        if !last.is_success() || last.target_id == 0 {
            return;
        }
        let Some(meta) = book.and_then(|b| b.try_get_metadata(last.spell_id)) else {
            return;
        };
        if meta.duration <= 0.0 {
            return;
        }
        self.track(last.target_id, meta, f64::from(meta.duration), now_ms);
    }

    /// What is believed on `target` now, by family then spell; expired
    /// entries are dropped first (OpenAC `Capture`).
    pub(super) fn capture(&mut self, target: u32, now_ms: i64) -> Vec<TrackedEnchantment> {
        if target == 0 {
            return Vec::new();
        }
        #[allow(clippy::cast_precision_loss)]
        let now = now_ms as f64;
        self.entries.retain(|_, t| t.expires_unix_ms > now);
        let mut out: Vec<TrackedEnchantment> = self
            .entries
            .iter()
            .filter(|((t, _), _)| *t == target)
            .map(|((t, spell), tracked)| TrackedEnchantment {
                target_id: *t,
                spell_id: *spell,
                family: tracked.family,
                quality: tracked.quality,
                is_untargeted: tracked.untargeted,
                seconds_remaining: ((tracked.expires_unix_ms - now) / 1000.0).max(0.0),
            })
            .collect();
        out.sort_by(|a, b| a.family.cmp(&b.family).then(a.spell_id.cmp(&b.spell_id)));
        out
    }

    /// Forget one target's entries, or (0) all; the last cast completion is
    /// consumed so it cannot come back (OpenAC `ForgetReported`).
    pub(super) fn forget(&mut self, target: u32, last_completion: u64) {
        self.seen_completion = last_completion;
        if target == 0 {
            self.entries.clear();
        } else {
            self.entries.retain(|(t, _), _| *t != target);
        }
    }
}

impl PluginBinding<'_> {
    fn spellbook(&self) -> Option<&Spellbook> {
        self.state.world.spellbook()
    }

    /// Every spell in the spell table.
    fn spells_all(&self) -> Vec<SpellInfo> {
        let Some(book) = self.spellbook() else {
            return Vec::new();
        };
        let table = book.metadata();
        let mut ids: Vec<u32> = table.spell_ids().collect();
        ids.sort_unstable();
        ids.into_iter()
            .filter_map(|id| table.get(id).map(project_spell))
            .collect()
    }

    /// A page of the spell table in id order: `count` (at most
    /// [`SPELL_PAGE_MAX`]) from index `first`, with the table's size.
    pub(super) fn spells_page(&self, first: u32, count: u32) -> SpellPage {
        let Some(book) = self.spellbook() else {
            return SpellPage::default();
        };
        let table = book.metadata();
        let mut ids: Vec<u32> = table.spell_ids().collect();
        ids.sort_unstable();
        let total = u32::try_from(ids.len()).unwrap_or(u32::MAX);
        let first = usize::try_from(first).unwrap_or(usize::MAX);
        let count = usize::try_from(count.min(SPELL_PAGE_MAX)).unwrap_or(0);
        SpellPage {
            total,
            spells: ids
                .into_iter()
                .skip(first)
                .take(count)
                .filter_map(|id| table.get(id).map(project_spell))
                .collect(),
        }
    }

    /// One spell.
    pub(super) fn spells_get(&self, spell_id: u32) -> Option<SpellInfo> {
        self.spellbook()?
            .try_get_metadata(spell_id)
            .map(project_spell)
    }

    /// A spell by name: exact ignoring case, then (`partial`) the first
    /// whose name contains it (OpenAC `TryFindByName`).
    pub(super) fn spells_find(&self, (name, partial): (String, bool)) -> Option<SpellInfo> {
        if name.trim().is_empty() {
            return None;
        }
        let all = self.spells_all();
        let wanted = name.to_lowercase();
        if let Some(found) = all.iter().find(|s| s.name.to_lowercase() == wanted) {
            return Some(found.clone());
        }
        if !partial {
            return None;
        }
        all.into_iter()
            .find(|s| s.name.to_lowercase().contains(&wanted))
    }

    /// The known-spell lists, rebuilt first if the spellbook changed.
    fn known_spells(&mut self) -> &KnownSpells {
        let state = &mut *self.state;
        let known = &mut state.plugin_services.known_spells;
        known.refresh(state.world.spellbook());
        known
    }

    /// One of the known-spell lists.
    pub(super) fn spells_known(&mut self, list: SpellList) -> Vec<SpellInfo> {
        self.known_spells().list(list).to_vec()
    }

    /// One of the known-spell lists, ids only, in the same order.
    pub(super) fn spells_known_ids(&mut self, list: SpellList) -> Vec<u32> {
        self.known_spells()
            .list(list)
            .iter()
            .map(|spell| spell.spell_id)
            .collect()
    }

    /// The known-spell lists' revision.
    pub(super) fn spells_known_revision(&mut self) -> u64 {
        self.known_spells().revision()
    }

    /// Whether the character knows a spell.
    pub(super) fn spells_is_known(&self, spell_id: u32) -> bool {
        self.spellbook().is_some_and(|b| b.knows(spell_id))
    }

    /// One spell component (by component id) carried by a weenie class.
    pub(super) fn spells_component(&self, component_id: u32) -> Option<ComponentInfo> {
        let tables = self.state.gameplay.tables();
        let details = tables.component_details(component_id)?;
        Some(ComponentInfo {
            component_id,
            weenie_class_id: details.weenie_class_id,
            name: details.name.clone(),
            burn_rate: f64::from(details.burn_rate),
            gesture_id: details.gesture,
            gesture_speed: f64::from(details.gesture_speed),
            icon_id: details.icon,
            sort_key: details.category,
            component_type: ac_core::spells::ComponentType(details.component_type)
                .name()
                .unwrap_or("Undef")
                .to_owned(),
            word: details.word.clone(),
        })
    }

    /// Seconds left on a cooldown group (an expired one is dropped, as
    /// the spellbook's check does).
    pub(super) fn spells_cooldown(&mut self, cooldown_id: u32) -> f64 {
        if cooldown_id == 0 {
            return 0.0;
        }
        let now = self.session_seconds();
        self.state
            .world
            .update_spellbook(|b| b.on_cooldown(cooldown_id, now))
            .filter(|check| check.active)
            .map_or(0.0, |check| check.remaining.max(0.0))
    }

    /// Whether the client holds up another cast (OpenAC `IsCasting`): it
    /// never does; a cast still waiting for its answer does not block the
    /// next request.
    pub(super) fn magic_is_casting(&self) -> bool {
        false
    }

    /// Selects a target a plugin named: it must be in the world (OpenAC
    /// `SelectExplicitTarget`).
    fn select_target(&mut self, target: u32) -> bool {
        if !self.in_world() || target == 0 || self.state.world.object(target).is_none() {
            return false;
        }
        let command = SessionCommand::Gameplay(GameplayCommand::Select(ObjectRef::Guid(target)));
        matches!(self.run(command), Ran::Done)
    }

    /// Whether a plugin may cast at `target` without selecting it: an
    /// object in the world (OpenAC `IsCastTarget`).
    fn is_cast_target(&self, target: u32) -> bool {
        self.in_world() && target != 0 && self.state.world.object(target).is_some()
    }

    /// Whether the spell could be cast now (OpenAC `EvaluateGate`).
    pub(super) fn magic_gate(&mut self, request: CastRequest) -> CastGate {
        if request.target_id != 0 && !self.select_target(request.target_id) {
            return CastGate::Refused;
        }
        let selected = self.state.gameplay.selection().selected();
        self.gate_at(request.spell_id, selected, false)
    }

    /// Whether the spell could be cast now at the request's target,
    /// without reading or changing the selection (OpenAC
    /// `EvaluateGateWithoutSelecting`).
    pub(super) fn magic_gate_without_selecting(&mut self, request: CastRequest) -> CastGate {
        self.gate_at(request.spell_id, Some(request.target_id), true)
    }

    /// The gate of a cast at `target` (the selection or a named object).
    /// A named object (`named`) must be in the world, which is asked after
    /// whether the spell is known.
    fn gate_at(&mut self, spell_id: u32, target: Option<u32>, named: bool) -> CastGate {
        if !self.in_world() {
            return CastGate::Unavailable;
        }
        let Some(book) = self.spellbook() else {
            return CastGate::Unavailable;
        };
        if !book.knows(spell_id) {
            return CastGate::NotKnown;
        }
        if named && !self.is_cast_target(target.unwrap_or(0)) {
            return CastGate::Refused;
        }
        let world = &self.state.world;
        let gate = cast_gate(
            world.items(),
            world.player_guid().unwrap_or(0),
            book,
            spell_id,
            target,
        );
        match gate {
            SpellCastGate::Unknown => CastGate::NotKnown,
            SpellCastGate::NoTargetNeeded | SpellCastGate::TargetCompatible => CastGate::Ready,
            SpellCastGate::NoTargetSelected => CastGate::NoTargetSelected,
            SpellCastGate::TargetIncompatible => CastGate::TargetIncompatible,
        }
    }

    /// Cast a spell, through the session's own cast (OpenAC
    /// `RequestCast`).
    pub(super) fn magic_cast(&mut self, request: CastRequest) -> CastResult {
        if request.target_id != 0 && !self.select_target(request.target_id) {
            return CastResult::IncompatibleTarget;
        }
        self.send_cast(
            GameplayCommand::Cast {
                spell: request.spell_id.to_string(),
                target: None,
            },
            request.spell_id,
        )
    }

    /// Cast a spell at the request's target without reading or changing
    /// the selection (OpenAC `RequestCastWithoutSelecting`).
    pub(super) fn magic_cast_without_selecting(&mut self, request: CastRequest) -> CastResult {
        if !self.in_world() {
            return CastResult::Unavailable;
        }
        if !self.is_cast_target(request.target_id) {
            return CastResult::IncompatibleTarget;
        }
        self.send_cast(
            GameplayCommand::CastWithoutSelecting {
                spell: request.spell_id,
                target: request.target_id,
            },
            request.spell_id,
        )
    }

    /// Runs a cast command through the session's own cast and reports
    /// how it went.
    fn send_cast(&mut self, command: GameplayCommand, spell_id: u32) -> CastResult {
        if !self.in_world() {
            return CastResult::Unavailable;
        }
        if !self.spells_is_known(spell_id) {
            return CastResult::UnknownSpell;
        }
        let command = SessionCommand::Gameplay(command);
        let before = self.state.gameplay.spell_cast().request_count();
        if matches!(self.run(command), Ran::NoConnection) {
            return CastResult::Unavailable;
        }
        let cast = self.state.gameplay.spell_cast();
        if cast.request_count() == before {
            // The owner refused before asking the cast (no spellbook).
            return CastResult::Unavailable;
        }
        match cast.last_result() {
            Some(CastRequestResult::Sent) => CastResult::Sent,
            Some(CastRequestResult::UnknownSpell) => CastResult::UnknownSpell,
            Some(CastRequestResult::NoTarget) => CastResult::NoTarget,
            Some(CastRequestResult::IncompatibleTarget) => CastResult::IncompatibleTarget,
            Some(CastRequestResult::MissingComponents) => CastResult::MissingComponents,
            _ => CastResult::Unavailable,
        }
    }

    /// Whether the character carries a spell's components.
    pub(super) fn magic_has_components(&self, spell_id: u32) -> bool {
        self.state
            .gameplay
            .has_spell_components(&self.state.world, spell_id)
    }

    /// The server's answer to the last cast.
    pub(super) fn magic_last_completion(&mut self) -> CastCompletion {
        let last = self.state.gameplay.spell_cast().last_completion();
        let completion = CastCompletion {
            revision: i64::try_from(last.revision).unwrap_or(i64::MAX),
            spell_id: last.spell_id,
            target_id: last.target_id,
            weenie_error: last.weenie_error,
        };
        let now = self.wall_unix_ms;
        let book = self.state.world.spellbook();
        self.state
            .plugin_services
            .enchantments
            .observe(book, completion.clone(), now);
        completion
    }

    /// Tell the tracker a spell landed.
    pub(super) fn enchantments_report(&mut self, cast: ReportedCast) -> bool {
        let now = self.wall_unix_ms;
        let book = self.state.world.spellbook();
        self.state
            .plugin_services
            .enchantments
            .report(book, &cast, now)
    }

    /// What the tracker believes is on a target (the client's own last
    /// cast taken in first).
    pub(super) fn enchantments_capture(&mut self, target: u32) -> Vec<TrackedEnchantment> {
        if target == 0 {
            return Vec::new();
        }
        let _ = self.magic_last_completion();
        let now = self.wall_unix_ms;
        self.state.plugin_services.enchantments.capture(target, now)
    }

    /// Forget a target's entries (0: all).
    pub(super) fn enchantments_forget(&mut self, target: u32) {
        let last = self.state.gameplay.spell_cast().last_completion().revision;
        self.state.plugin_services.enchantments.forget(target, last);
    }
}
