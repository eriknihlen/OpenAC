//! The player's spellbook: learned spells with their power, the eight
//! favorite bars, desired component amounts, the spellbook filter, and the
//! active enchantment registry with its per-bucket list order.
//!
//! Nothing here raises events. Each change bumps one of three revision
//! counters (spellbook, enchantments, desired components) and the methods
//! return what was added or removed, so the owner can notify observers.

use std::collections::{BTreeMap, HashMap};
use std::sync::Arc;

use super::enchantment::{
    ActiveEnchantmentRecord, BUCKET_ADDITIVE, BUCKET_COOLDOWN, BUCKET_MULTIPLICATIVE, BUCKET_VITAE,
    EnchantmentTypeFlags, VitalMod, get_enchantments_in_effect, get_mod, get_skill_mod,
    make_identity,
};
use super::metadata::SpellMetadata;
use super::table::SpellTable;

/// Spell id offset of a cooldown enchantment over its cooldown group.
pub const COOLDOWN_SPELL_OFFSET: u32 = 0x8000;
/// Number of favorite spell bars.
pub const FAVORITE_BAR_COUNT: usize = 8;
/// The spellbook filter with every school and level shown.
pub const DEFAULT_SPELLBOOK_FILTERS: u32 = 0x3FFF;

/// A learned spell and the power the server sent with it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LearnedSpellRecord {
    /// Spell id.
    pub spell_id: u32,
    /// Spell power.
    pub power: f32,
}

/// What learning a spell changed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LearnOutcome {
    /// The spell is new to the book.
    Learned,
    /// The spell was known; its power changed.
    PowerChanged,
    /// Nothing changed.
    Unchanged,
}

/// The answer to a cooldown query.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CooldownCheck {
    /// The cooldown is still running.
    pub active: bool,
    /// Seconds left (negative when the first match had expired; zero when
    /// nothing matched).
    pub remaining: f64,
    /// The expired cooldown record the query removed, if any.
    pub expired: Option<ActiveEnchantmentRecord>,
}

/// Spell metadata can be installed once.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MetadataAlreadyInstalled;

impl std::fmt::Display for MetadataAlreadyInstalled {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("Spell metadata is already installed.")
    }
}

impl std::error::Error for MetadataAlreadyInstalled {}

/// The spellbook and enchantment registry.
#[derive(Debug, Clone)]
pub struct Spellbook {
    learned: BTreeMap<u32, f32>,
    /// Active enchantments in arrival order, one per identity.
    active: Vec<ActiveEnchantmentRecord>,
    /// Per bucket, identities in the game's list order (head first).
    order_by_bucket: BTreeMap<u32, Vec<u32>>,
    favorites: [Vec<u32>; FAVORITE_BAR_COUNT],
    desired_components: BTreeMap<u32, u32>,
    table: Arc<SpellTable>,
    metadata_installed: bool,
    filters: u32,
    spellbook_revision: u64,
    enchantment_revision: u64,
    desired_components_revision: u64,
}

impl Default for Spellbook {
    fn default() -> Self {
        Self::new(None)
    }
}

impl Spellbook {
    /// An empty book. With a table the metadata counts as installed.
    #[must_use]
    pub fn new(table: Option<Arc<SpellTable>>) -> Self {
        let metadata_installed = table.is_some();
        Self {
            learned: BTreeMap::new(),
            active: Vec::new(),
            order_by_bucket: BTreeMap::new(),
            favorites: Default::default(),
            desired_components: BTreeMap::new(),
            table: table.unwrap_or_default(),
            metadata_installed,
            filters: DEFAULT_SPELLBOOK_FILTERS,
            spellbook_revision: 0,
            enchantment_revision: 0,
            desired_components_revision: 0,
        }
    }

    /// The spell table.
    #[must_use]
    pub fn metadata(&self) -> &Arc<SpellTable> {
        &self.table
    }

    /// A spell's record.
    #[must_use]
    pub fn try_get_metadata(&self, spell_id: u32) -> Option<&SpellMetadata> {
        self.table.get(spell_id)
    }

    /// Installs the spell table once. Installing the same table again is
    /// allowed and does nothing.
    ///
    /// # Errors
    /// [`MetadataAlreadyInstalled`] when a different table is installed.
    pub fn install_metadata(
        &mut self,
        table: Arc<SpellTable>,
    ) -> Result<(), MetadataAlreadyInstalled> {
        if self.metadata_installed {
            return if Arc::ptr_eq(&self.table, &table) {
                Ok(())
            } else {
                Err(MetadataAlreadyInstalled)
            };
        }
        self.table = table;
        self.metadata_installed = true;
        if !self.learned.is_empty() {
            self.spellbook_revision += 1;
        }
        if !self.active.is_empty() {
            self.enchantment_revision += 1;
        }
        Ok(())
    }

    /// Advances when learned spells, favorites or the filter change.
    #[must_use]
    pub fn spellbook_revision(&self) -> u64 {
        self.spellbook_revision
    }

    /// Advances when the active enchantments change.
    #[must_use]
    pub fn enchantment_revision(&self) -> u64 {
        self.enchantment_revision
    }

    /// Advances when the desired component amounts change.
    #[must_use]
    pub fn desired_components_revision(&self) -> u64 {
        self.desired_components_revision
    }

    /// The vital-maximum modifier (vitae included).
    #[must_use]
    pub fn get_vital_mod(&self, stat_key: u32) -> VitalMod {
        get_mod(
            self.stat_enchantments_in_list_order(),
            stat_key,
            EnchantmentTypeFlags::SECOND_ATT,
            true,
        )
    }

    /// The attribute modifier (attributes ignore vitae).
    #[must_use]
    pub fn get_attribute_mod(&self, attribute_id: u32) -> VitalMod {
        get_mod(
            self.stat_enchantments_in_list_order(),
            attribute_id,
            EnchantmentTypeFlags::ATTRIBUTE,
            false,
        )
    }

    /// The skill modifier (vitae included).
    #[must_use]
    pub fn get_skill_mod(&self, skill_id: u32) -> VitalMod {
        get_skill_mod(self.stat_enchantments_in_list_order(), skill_id)
    }

    /// The multiplicative, additive and vitae records, each list from its
    /// head: the order the stat math takes them in, which decides exact
    /// stacking ties.
    fn stat_enchantments_in_list_order(&self) -> Vec<&ActiveEnchantmentRecord> {
        let by_identity: HashMap<u32, &ActiveEnchantmentRecord> = self
            .active
            .iter()
            .map(|record| (record.identity(), record))
            .collect();
        [BUCKET_MULTIPLICATIVE, BUCKET_ADDITIVE, BUCKET_VITAE]
            .into_iter()
            .flat_map(|bucket| {
                self.order_by_bucket
                    .get(&bucket)
                    .into_iter()
                    .flatten()
                    .filter_map(|identity| by_identity.get(identity).copied())
                    .filter(move |record| record.bucket == bucket)
            })
            .collect()
    }

    /// Learned spell ids, ascending.
    pub fn learned_spells(&self) -> impl Iterator<Item = u32> + '_ {
        self.learned.keys().copied()
    }

    /// Learned spells with their power, by spell id.
    #[must_use]
    pub fn learned_spell_snapshot(&self) -> Vec<LearnedSpellRecord> {
        self.learned
            .iter()
            .map(|(&spell_id, &power)| LearnedSpellRecord { spell_id, power })
            .collect()
    }

    /// The active enchantments, in arrival order.
    #[must_use]
    pub fn active_enchantments(&self) -> &[ActiveEnchantmentRecord] {
        &self.active
    }

    /// The active enchantments sorted by identity.
    #[must_use]
    pub fn active_enchantment_snapshot(&self) -> Vec<ActiveEnchantmentRecord> {
        let mut snapshot = self.active.clone();
        snapshot.sort_by_key(ActiveEnchantmentRecord::identity);
        snapshot
    }

    /// The enchantments in effect (see
    /// [`get_enchantments_in_effect`](super::get_enchantments_in_effect)),
    /// walking each list in the game's order.
    #[must_use]
    pub fn enchantments_in_effect_snapshot(&self) -> Vec<ActiveEnchantmentRecord> {
        let mut ordered = Vec::new();
        self.append_bucket(BUCKET_MULTIPLICATIVE, &mut ordered);
        self.append_bucket(BUCKET_ADDITIVE, &mut ordered);
        get_enchantments_in_effect(&ordered)
    }

    /// A favorite bar (empty for a bar that does not exist).
    #[must_use]
    pub fn get_favorites(&self, tab_index: usize) -> &[u32] {
        self.favorites.get(tab_index).map_or(&[], Vec::as_slice)
    }

    /// Desired component amounts by component id.
    #[must_use]
    pub fn desired_components(&self) -> &BTreeMap<u32, u32> {
        &self.desired_components
    }

    /// The spellbook filter bits.
    #[must_use]
    pub fn spellbook_filters(&self) -> u32 {
        self.filters
    }

    /// Number of learned spells.
    #[must_use]
    pub fn learned_count(&self) -> usize {
        self.learned.len()
    }

    /// Number of active enchantments.
    #[must_use]
    pub fn active_count(&self) -> usize {
        self.active.len()
    }

    /// True when a cooldown is active.
    #[must_use]
    pub fn has_cooldown_enchantments(&self) -> bool {
        self.order_by_bucket
            .get(&BUCKET_COOLDOWN)
            .is_some_and(|order| !order.is_empty())
    }

    /// True when the spell is learned.
    #[must_use]
    pub fn knows(&self, spell_id: u32) -> bool {
        self.learned.contains_key(&spell_id)
    }

    /// The server taught a spell (or updated its power).
    pub fn on_spell_learned(&mut self, spell_id: u32, power: f32) -> LearnOutcome {
        match self.learned.get_mut(&spell_id) {
            None => {
                self.learned.insert(spell_id, power);
                self.spellbook_revision += 1;
                LearnOutcome::Learned
            }
            Some(current) if *current != power => {
                *current = power;
                self.spellbook_revision += 1;
                LearnOutcome::PowerChanged
            }
            Some(_) => LearnOutcome::Unchanged,
        }
    }

    /// The server removed a spell. True when it was known.
    pub fn on_spell_forgotten(&mut self, spell_id: u32) -> bool {
        let removed = self.learned.remove(&spell_id).is_some();
        if removed {
            self.spellbook_revision += 1;
        }
        removed
    }

    /// An enchantment arrived or was refreshed. A new one goes to the head
    /// of its bucket's list; a refresh keeps its place.
    pub fn on_enchantment_added(&mut self, record: ActiveEnchantmentRecord) {
        self.upsert(record, true);
        self.enchantment_revision += 1;
    }

    /// Several enchantments arrived at once.
    pub fn on_enchantments_added(
        &mut self,
        records: impl IntoIterator<Item = ActiveEnchantmentRecord>,
    ) {
        for record in records {
            self.upsert(record, true);
        }
        self.enchantment_revision += 1;
    }

    /// An enchantment was removed or dispelled. Returns the removed record.
    pub fn on_enchantment_removed(
        &mut self,
        layer_id: u32,
        spell_id: u32,
    ) -> Option<ActiveEnchantmentRecord> {
        let removed = self.remove_active(make_identity(spell_id, layer_id));
        if removed.is_some() {
            self.enchantment_revision += 1;
        }
        removed
    }

    /// Several enchantments were removed, as `(spell, layer)` pairs.
    /// Returns the records that were present.
    pub fn on_enchantments_removed(
        &mut self,
        spells: impl IntoIterator<Item = (u32, u32)>,
    ) -> Vec<ActiveEnchantmentRecord> {
        let removed: Vec<_> = spells
            .into_iter()
            .filter_map(|(spell, layer)| self.remove_active(make_identity(spell, layer)))
            .collect();
        if !removed.is_empty() {
            self.enchantment_revision += 1;
        }
        removed
    }

    /// Whether a cooldown group is running at `current_time`. The first
    /// cooldown record of the group (in list order) decides; if it has run
    /// out it is removed and the answer is "not active". Group 0 is never
    /// active.
    pub fn on_cooldown(&mut self, cooldown_id: u32, current_time: f64) -> CooldownCheck {
        let none = CooldownCheck {
            active: false,
            remaining: 0.0,
            expired: None,
        };
        if cooldown_id == 0 {
            return none;
        }
        let Some(order) = self.order_by_bucket.get(&BUCKET_COOLDOWN) else {
            return none;
        };
        let cooldown_spell_id = cooldown_id.wrapping_add(COOLDOWN_SPELL_OFFSET);
        let found = order.iter().find_map(|&identity| {
            self.active
                .iter()
                .find(|r| r.identity() == identity)
                .filter(|r| r.spell_id & 0xFFFF == cooldown_spell_id)
                .copied()
        });
        let Some(record) = found else {
            return none;
        };

        let remaining = record.duration + record.start_time - current_time;
        if remaining > 0.0 {
            return CooldownCheck {
                active: true,
                remaining,
                expired: None,
            };
        }
        self.remove_active(record.identity());
        self.enchantment_revision += 1;
        CooldownCheck {
            active: false,
            remaining,
            expired: Some(record),
        }
    }

    /// Removes every timed multiplicative and additive enchantment. Returns
    /// the removed records.
    pub fn on_purge_all(&mut self) -> Vec<ActiveEnchantmentRecord> {
        self.purge(|_| true)
    }

    /// Removes every timed multiplicative and additive enchantment that is
    /// not beneficial. Returns the removed records.
    pub fn on_purge_bad_enchantments(&mut self) -> Vec<ActiveEnchantmentRecord> {
        self.purge(|record| {
            record.stat_mod_type.unwrap_or(0) & EnchantmentTypeFlags::BENEFICIAL.bits() == 0
        })
    }

    fn purge(
        &mut self,
        also: impl Fn(&ActiveEnchantmentRecord) -> bool,
    ) -> Vec<ActiveEnchantmentRecord> {
        let removed: Vec<_> = self
            .active
            .iter()
            .filter(|r| is_purgeable(r) && also(r))
            .copied()
            .collect();
        for record in &removed {
            self.remove_active(record.identity());
        }
        if !removed.is_empty() {
            self.enchantment_revision += 1;
        }
        removed
    }

    /// Replaces everything from the character description: learned spells,
    /// enchantments (kept in the order received), favorite bars, desired
    /// components and the filter.
    pub fn replace_manifest(
        &mut self,
        learned_spells: impl IntoIterator<Item = (u32, f32)>,
        enchantments: impl IntoIterator<Item = ActiveEnchantmentRecord>,
        favorites: &[Vec<u32>],
        desired_components: &[(u32, u32)],
        spellbook_filters: u32,
    ) {
        self.learned = learned_spells.into_iter().collect();
        self.active.clear();
        self.order_by_bucket.clear();
        for enchantment in enchantments {
            self.upsert(enchantment, false);
        }
        for (index, bar) in self.favorites.iter_mut().enumerate() {
            bar.clear();
            if let Some(source) = favorites.get(index) {
                bar.extend_from_slice(source);
            }
        }
        self.desired_components = desired_components.iter().copied().collect();
        self.filters = spellbook_filters;
        self.bump_all();
    }

    /// Puts a spell on a favorite bar at `position` (clamped to the bar's
    /// end), moving it if it was already there.
    pub fn set_favorite(&mut self, tab_index: usize, position: usize, spell_id: u32) {
        let Some(bar) = self.favorites.get_mut(tab_index) else {
            return;
        };
        let position = position.min(bar.len());
        if let Some(existing) = bar.iter().position(|&id| id == spell_id) {
            bar.remove(existing);
        }
        let position = position.min(bar.len());
        bar.insert(position, spell_id);
        self.spellbook_revision += 1;
    }

    /// Takes a spell off a favorite bar.
    pub fn remove_favorite(&mut self, tab_index: usize, spell_id: u32) {
        let Some(bar) = self.favorites.get_mut(tab_index) else {
            return;
        };
        if let Some(existing) = bar.iter().position(|&id| id == spell_id) {
            bar.remove(existing);
            self.spellbook_revision += 1;
        }
    }

    /// Sets the spellbook filter.
    pub fn set_spellbook_filters(&mut self, filters: u32) {
        if self.filters != filters {
            self.filters = filters;
            self.spellbook_revision += 1;
        }
    }

    /// Sets a desired component amount.
    pub fn set_desired_component(&mut self, component_id: u32, amount: u32) {
        if self.desired_components.get(&component_id) == Some(&amount) {
            return;
        }
        self.desired_components.insert(component_id, amount);
        self.desired_components_revision += 1;
    }

    /// Clears every desired component amount.
    pub fn clear_desired_components(&mut self) {
        if self.desired_components.is_empty() {
            return;
        }
        self.desired_components.clear();
        self.desired_components_revision += 1;
    }

    /// Forgets the character: learned spells, enchantments, favorites,
    /// desired components; the filter returns to its default. The spell
    /// table stays.
    pub fn clear(&mut self) {
        self.learned.clear();
        self.active.clear();
        self.order_by_bucket.clear();
        for bar in &mut self.favorites {
            bar.clear();
        }
        self.desired_components.clear();
        self.filters = DEFAULT_SPELLBOOK_FILTERS;
        self.bump_all();
    }

    fn bump_all(&mut self) {
        self.spellbook_revision += 1;
        self.enchantment_revision += 1;
        self.desired_components_revision += 1;
    }

    fn append_bucket(&self, bucket: u32, destination: &mut Vec<ActiveEnchantmentRecord>) {
        let Some(order) = self.order_by_bucket.get(&bucket) else {
            return;
        };
        for &identity in order {
            if let Some(record) = self.active.iter().find(|r| r.identity() == identity)
                && record.bucket == bucket
            {
                destination.push(*record);
            }
        }
    }

    /// Adds or replaces a record. A live update puts a new identity at the
    /// head of its bucket list; the login manifest appends, keeping the
    /// received order. A replacement in the same bucket keeps its place.
    fn upsert(&mut self, record: ActiveEnchantmentRecord, at_head: bool) {
        let identity = record.identity();
        if let Some(slot) = self.active.iter_mut().find(|r| r.identity() == identity) {
            let previous = std::mem::replace(slot, record);
            if previous.bucket == record.bucket {
                let order = self.order_by_bucket.entry(record.bucket).or_default();
                if !order.contains(&identity) {
                    insert_into(order, identity, at_head);
                }
                return;
            }
            self.remove_from_order(&previous);
        } else {
            self.active.push(record);
        }
        insert_into(
            self.order_by_bucket.entry(record.bucket).or_default(),
            identity,
            at_head,
        );
    }

    fn remove_active(&mut self, identity: u32) -> Option<ActiveEnchantmentRecord> {
        let index = self.active.iter().position(|r| r.identity() == identity)?;
        let record = self.active.remove(index);
        self.remove_from_order(&record);
        Some(record)
    }

    fn remove_from_order(&mut self, record: &ActiveEnchantmentRecord) {
        let Some(order) = self.order_by_bucket.get_mut(&record.bucket) else {
            return;
        };
        let identity = record.identity();
        if let Some(index) = order.iter().position(|&id| id == identity) {
            order.remove(index);
        }
        if order.is_empty() {
            self.order_by_bucket.remove(&record.bucket);
        }
    }
}

fn insert_into(order: &mut Vec<u32>, identity: u32, at_head: bool) {
    if at_head {
        order.insert(0, identity);
    } else {
        order.push(identity);
    }
}

fn is_purgeable(record: &ActiveEnchantmentRecord) -> bool {
    matches!(record.bucket, BUCKET_MULTIPLICATIVE | BUCKET_ADDITIVE) && record.duration != -1.0
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::spells::STAT_KEY_MAX_HEALTH;

    // Golden behavior from OpenAC
    // `tests/AcDream.Core.Tests/Spells/SpellbookTests.cs`.

    fn table(ids: &[u32]) -> Arc<SpellTable> {
        Arc::new(
            SpellTable::create(ids.iter().map(|&id| SpellMetadata::new(id, "Test", 0)))
                .expect("unique ids"),
        )
    }

    fn effect(spell_id: u32, layer: u32, category: u32) -> ActiveEnchantmentRecord {
        ActiveEnchantmentRecord {
            bucket: 1,
            start_time: 10.0,
            spell_category: category,
            power_level: 7,
            ..ActiveEnchantmentRecord::new(spell_id, layer, 60.0, 1)
        }
    }

    fn bucketed(
        spell_id: u32,
        duration: f64,
        bucket: u32,
        stat_mod_type: Option<u32>,
    ) -> ActiveEnchantmentRecord {
        ActiveEnchantmentRecord {
            bucket,
            stat_mod_type,
            ..ActiveEnchantmentRecord::new(spell_id, spell_id, duration, 0)
        }
    }

    #[test]
    fn install_metadata_installs_once_and_refreshes_learned_state() {
        let mut book = Spellbook::default();
        book.on_spell_learned(1, 0.0);
        let before = book.spellbook_revision();
        let t = table(&[1]);
        assert_eq!(book.install_metadata(Arc::clone(&t)), Ok(()));
        assert!(Arc::ptr_eq(book.metadata(), &t));
        assert!(book.try_get_metadata(1).is_some());
        assert_eq!(book.spellbook_revision(), before + 1);
        assert_eq!(book.install_metadata(Arc::clone(&t)), Ok(()));
        assert_eq!(
            book.install_metadata(Arc::new(SpellTable::empty())),
            Err(MetadataAlreadyInstalled)
        );
    }

    #[test]
    fn learning_is_idempotent() {
        let mut book = Spellbook::default();
        assert_eq!(book.on_spell_learned(0x3E1, 0.0), LearnOutcome::Learned);
        assert_eq!(book.on_spell_learned(0x3E1, 0.0), LearnOutcome::Unchanged);
        assert_eq!(
            book.on_spell_learned(0x3E1, 2.0),
            LearnOutcome::PowerChanged
        );
        assert_eq!(book.learned_count(), 1);
        assert!(book.knows(0x3E1));
    }

    #[test]
    fn forgetting_removes() {
        let mut book = Spellbook::default();
        book.on_spell_learned(0x3E1, 0.0);
        assert!(book.on_spell_forgotten(0x3E1));
        assert!(!book.knows(0x3E1));
        assert!(!book.on_spell_forgotten(0x3E1));
    }

    #[test]
    fn enchantment_added_is_stored_by_layer() {
        let mut book = Spellbook::default();
        book.on_enchantment_added(ActiveEnchantmentRecord::new(42, 7, 300.0, 0xBEEF));
        assert_eq!(book.active_count(), 1);
        assert_eq!(book.active_enchantments()[0].layer_id, 7);
    }

    #[test]
    fn same_layer_replaces() {
        let mut book = Spellbook::default();
        book.on_enchantment_added(ActiveEnchantmentRecord::new(42, 7, 300.0, 0));
        book.on_enchantment_added(ActiveEnchantmentRecord::new(42, 7, 600.0, 0));
        assert_eq!(book.active_count(), 1);
        assert!(book.active_enchantments()[0].duration == 600.0);
    }

    #[test]
    fn same_layer_different_spell_stays_distinct() {
        let mut book = Spellbook::default();
        book.on_enchantment_added(ActiveEnchantmentRecord::new(42, 7, 300.0, 0));
        book.on_enchantment_added(ActiveEnchantmentRecord::new(43, 7, 600.0, 0));
        assert_eq!(book.active_count(), 2);
    }

    #[test]
    fn vital_mod_follows_enchantment_changes() {
        let t =
            Arc::new(SpellTable::create([SpellMetadata::new(42, "Test", 100)]).expect("unique"));
        let mut book = Spellbook::new(Some(t));
        let record = |value: f32| ActiveEnchantmentRecord {
            stat_mod_type: Some(
                (EnchantmentTypeFlags::SECOND_ATT | EnchantmentTypeFlags::MULTIPLICATIVE).bits(),
            ),
            stat_mod_key: Some(STAT_KEY_MAX_HEALTH),
            stat_mod_value: Some(value),
            bucket: 1,
            ..ActiveEnchantmentRecord::new(42, 7, 300.0, 0)
        };
        book.on_enchantment_added(record(1.5));
        assert!(book.get_vital_mod(STAT_KEY_MAX_HEALTH).multiplier() == 1.5);
        book.on_enchantment_added(record(1.25));
        assert!(book.get_vital_mod(STAT_KEY_MAX_HEALTH).multiplier() == 1.25);
    }

    // The records below tie exactly (same category, power and start), so
    // the list order decides: the list is walked from its head and the
    // later challenger takes an exact tie.

    #[test]
    fn live_new_identity_inserts_at_list_head() {
        let mut book = Spellbook::default();
        book.on_enchantment_added(effect(1, 1, 12));
        book.on_enchantment_added(effect(2, 2, 12));
        // List [2, 1]: the record that arrived first is the challenger.
        let in_effect = book.enchantments_in_effect_snapshot();
        assert_eq!(in_effect.len(), 1);
        assert_eq!(in_effect[0].spell_id, 1);
    }

    #[test]
    fn live_refresh_keeps_list_position() {
        let mut book = Spellbook::default();
        book.on_enchantment_added(effect(1, 1, 12));
        book.on_enchantment_added(effect(2, 2, 12));
        book.on_enchantment_added(effect(1, 1, 12));
        // Still [2, 1]; moving the refresh to the head would make it [1, 2].
        let in_effect = book.enchantments_in_effect_snapshot();
        assert_eq!(in_effect.len(), 1);
        assert_eq!(in_effect[0].spell_id, 1);
    }

    #[test]
    fn manifest_preserves_received_order() {
        let mut book = Spellbook::default();
        book.replace_manifest([], [effect(1, 1, 12), effect(2, 2, 12)], &[], &[], 0x3FFF);
        // [1, 2] as received.
        let in_effect = book.enchantments_in_effect_snapshot();
        assert_eq!(in_effect.len(), 1);
        assert_eq!(in_effect[0].spell_id, 2);
    }

    #[test]
    fn stat_queries_walk_the_lists_in_their_order() {
        let record = |spell_id: u32, value: f32| ActiveEnchantmentRecord {
            stat_mod_type: Some(
                (EnchantmentTypeFlags::SECOND_ATT | EnchantmentTypeFlags::ADDITIVE).bits(),
            ),
            stat_mod_key: Some(STAT_KEY_MAX_HEALTH),
            stat_mod_value: Some(value),
            ..effect(spell_id, spell_id, 12)
        };
        let mut live = Spellbook::default();
        live.on_enchantment_added(ActiveEnchantmentRecord {
            bucket: 2,
            ..record(1, 10.0)
        });
        live.on_enchantment_added(ActiveEnchantmentRecord {
            bucket: 2,
            ..record(2, 20.0)
        });
        assert!(live.get_vital_mod(STAT_KEY_MAX_HEALTH).additive() == 10.0);
        let mut manifest = Spellbook::default();
        manifest.replace_manifest(
            [],
            [1, 2].map(|id| ActiveEnchantmentRecord {
                bucket: 2,
                ..record(id, 10.0 * id as f32)
            }),
            &[],
            &[],
            0x3FFF,
        );
        assert!(manifest.get_vital_mod(STAT_KEY_MAX_HEALTH).additive() == 20.0);
    }

    #[test]
    fn replace_manifest_replaces_favorites_filters_and_desired_components() {
        let mut book = Spellbook::default();
        book.on_spell_learned(999, 0.0);
        book.replace_manifest(
            [(42, 123.0)],
            [ActiveEnchantmentRecord::new(50, 2, 60.0, 1)],
            &[vec![42, 43]],
            &[(100, 25)],
            0xA5,
        );
        assert!(!book.knows(999));
        assert!(book.knows(42));
        assert_eq!(book.get_favorites(0), [42, 43]);
        assert_eq!(book.desired_components()[&100], 25);
        assert_eq!(book.spellbook_filters(), 0xA5);
        assert_eq!(book.active_count(), 1);
    }

    #[test]
    fn set_favorite_moving_within_bar_shifts() {
        let mut book = Spellbook::default();
        book.set_favorite(0, 0, 1);
        book.set_favorite(0, 1, 2);
        book.set_favorite(0, 2, 3);
        book.set_favorite(0, 1, 1);
        assert_eq!(book.get_favorites(0), [2, 1, 3]);
    }

    #[test]
    fn set_favorite_moving_to_end_appends() {
        let mut book = Spellbook::default();
        book.set_favorite(0, 0, 1);
        book.set_favorite(0, 1, 2);
        book.set_favorite(0, 2, 3);
        book.set_favorite(0, 2, 1);
        assert_eq!(book.get_favorites(0), [2, 3, 1]);
    }

    #[test]
    fn remove_favorite_keeps_bar_contiguous() {
        let mut book = Spellbook::default();
        book.set_favorite(0, 0, 1);
        book.set_favorite(0, 1, 2);
        book.set_favorite(0, 2, 3);
        book.remove_favorite(0, 2);
        assert_eq!(book.get_favorites(0), [1, 3]);
        assert!(book.get_favorites(8).is_empty());
    }

    #[test]
    fn enchantment_removed_returns_record() {
        let mut book = Spellbook::default();
        book.on_enchantment_added(ActiveEnchantmentRecord::new(42, 7, 300.0, 0));
        let removed = book.on_enchantment_removed(7, 42).expect("present");
        assert_eq!(removed.spell_id, 42);
        assert_eq!(book.active_count(), 0);
    }

    #[test]
    fn cooldown_removes_first_expired_match_then_reveals_next() {
        let mut book = Spellbook::default();
        let expired = ActiveEnchantmentRecord {
            bucket: BUCKET_COOLDOWN,
            start_time: 0.0,
            ..ActiveEnchantmentRecord::new(0x802A, 1, 5.0, 0)
        };
        let active = ActiveEnchantmentRecord {
            layer_id: 2,
            duration: 30.0,
            start_time: 10.0,
            ..expired
        };
        book.replace_manifest([], [expired, active], &[], &[], 0);

        let first = book.on_cooldown(0x2A, 20.0);
        assert!(!first.active);
        assert!(first.remaining == -15.0);
        assert_eq!(first.expired, Some(expired));
        assert_eq!(book.active_count(), 1);

        let second = book.on_cooldown(0x2A, 20.0);
        assert!(second.active);
        assert!(second.remaining == 20.0);
    }

    #[test]
    fn cooldown_ignores_same_spell_outside_cooldown_bucket() {
        let mut book = Spellbook::default();
        book.on_enchantment_added(ActiveEnchantmentRecord {
            bucket: 1,
            start_time: 10.0,
            ..ActiveEnchantmentRecord::new(0x802A, 1, 30.0, 0)
        });
        let check = book.on_cooldown(0x2A, 20.0);
        assert!(!check.active);
        assert!(check.remaining == 0.0);
        assert_eq!(book.active_count(), 1);
    }

    #[test]
    fn cooldown_zero_group_is_inactive() {
        let mut book = Spellbook::default();
        book.on_enchantment_added(ActiveEnchantmentRecord {
            bucket: BUCKET_COOLDOWN,
            start_time: 10.0,
            ..ActiveEnchantmentRecord::new(0x8000, 1, 30.0, 0)
        });
        let check = book.on_cooldown(0, 20.0);
        assert!(!check.active);
        assert_eq!(book.active_count(), 1);
        assert!(book.has_cooldown_enchantments());
    }

    #[test]
    fn purge_all_removes_only_timed_mult_and_add() {
        let mut book = Spellbook::default();
        book.on_enchantment_added(bucketed(1, 100.0, 1, None));
        book.on_enchantment_added(bucketed(2, 100.0, 2, None));
        book.on_enchantment_added(bucketed(3, -1.0, 2, None));
        book.on_enchantment_added(bucketed(4, 100.0, 4, None));
        book.on_enchantment_added(bucketed(5, 100.0, 8, None));
        assert_eq!(book.on_purge_all().len(), 2);
        let ids: Vec<u32> = book
            .active_enchantment_snapshot()
            .iter()
            .map(|r| r.spell_id)
            .collect();
        assert_eq!(ids, [3, 4, 5]);
    }

    #[test]
    fn purge_bad_keeps_beneficial_and_permanent() {
        let beneficial = EnchantmentTypeFlags::BENEFICIAL.bits();
        let mut book = Spellbook::default();
        book.on_enchantment_added(bucketed(1, 100.0, 1, Some(0)));
        book.on_enchantment_added(bucketed(2, 100.0, 2, Some(beneficial)));
        book.on_enchantment_added(bucketed(3, -1.0, 2, Some(0)));
        book.on_enchantment_added(bucketed(4, 100.0, 8, Some(0)));
        book.on_purge_bad_enchantments();
        let ids: Vec<u32> = book
            .active_enchantment_snapshot()
            .iter()
            .map(|r| r.spell_id)
            .collect();
        assert_eq!(ids, [2, 3, 4]);
    }

    #[test]
    fn clear_resets_filter() {
        let mut book = Spellbook::default();
        book.set_spellbook_filters(1);
        book.set_desired_component(5, 10);
        book.clear();
        assert_eq!(book.spellbook_filters(), DEFAULT_SPELLBOOK_FILTERS);
        assert!(book.desired_components().is_empty());
    }
}
