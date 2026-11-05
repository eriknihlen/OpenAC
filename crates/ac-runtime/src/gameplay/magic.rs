//! Magic: the spellbook fed from the character description and the magic
//! events, the spell-cast request and its gate, the spellbook's own
//! requests (favorites, filter, desired components, forgetting a spell),
//! and the "has expired" notice.
//!
//! The spellbook itself lives in the player's numbers
//! ([`crate::world::PlayerState`]), so attributes, vitals and skills read
//! with their enchantments.

use ac_core::chat::LogTextType;
use ac_core::player::{AttributeKind, VitalKind, skill_names};
use ac_core::properties::{PropertyBool, PropertyInt};
use ac_core::spells::{
    ActiveEnchantmentRecord, BUCKET_ADDITIVE, BUCKET_COOLDOWN, BUCKET_MULTIPLICATIVE, BUCKET_VITAE,
    ComponentObject, SpellComponentRequirements, SpellComponentWorld, SpellFormulaDefinition,
    SpellMetadata, SpellTargetCandidate, Spellbook, evaluate, get_vitae_multiplier,
};
use ac_proto::PlayerEnchantment;
use ac_proto::{GameEventEnvelope, GameEventType, PlayerDescription, ProtoError, magic};

use crate::world::World;

/// The spell the death penalty is recorded under.
pub const VITAE_PENALTY_SPELL_ID: u32 = 0x29A;

/// The answer the server gave to the last cast this client asked for.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct SpellCastCompletion {
    /// Counts completions; 0 before the first.
    pub revision: u64,
    /// The spell.
    pub spell_id: u32,
    /// Its target (0 for none).
    pub target_id: u32,
    /// The server's error (0 for success).
    pub weenie_error: u32,
}

impl SpellCastCompletion {
    /// The cast went through.
    #[must_use]
    pub fn is_success(&self) -> bool {
        self.revision != 0 && self.weenie_error == 0
    }
}

/// What a cast request did.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CastRequestResult {
    /// The request went out.
    Sent,
    /// The spell is not in the spellbook (or has no record).
    UnknownSpell,
    /// The spell needs a target and nothing is selected.
    NoTarget,
    /// The selected object cannot take the spell.
    IncompatibleTarget,
    /// Components are missing.
    MissingComponents,
    /// Not now: not in the world.
    Unavailable,
}

/// Whether a spell could be cast at the current selection.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SpellCastGate {
    /// The spell is not known or has no record.
    Unknown,
    /// The spell needs no target.
    NoTargetNeeded,
    /// The selection can take the spell.
    TargetCompatible,
    /// The selection cannot.
    TargetIncompatible,
    /// Nothing is selected.
    NoTargetSelected,
}

/// What the cast asks of the rest of the client.
pub trait SpellCastOperations {
    /// The player's id (0 before login).
    fn local_player_id(&self) -> u32;
    /// A request can go out (in the world).
    fn can_send(&self) -> bool;
    /// The player carries the spell's components.
    fn has_required_components(&self, spell_id: u32) -> bool;
    /// The target can take the spell; with `show_message` the refusal is
    /// said.
    fn is_target_compatible(
        &mut self,
        target_id: u32,
        spell: &SpellMetadata,
        show_message: bool,
    ) -> bool;
    /// Stop the player's movement before casting.
    fn stop_completely(&mut self);
    /// Send an untargeted cast.
    fn send_untargeted(&mut self, spell_id: u32);
    /// Send a targeted cast.
    fn send_targeted(&mut self, target_id: u32, spell_id: u32);
    /// Say something to the player.
    fn display_message(&mut self, message: &str);
    /// Count the player busy until the server's use-done.
    fn increment_busy(&mut self);
}

/// The casts in flight and the last answer.
///
/// A cast may be asked for again while an earlier one still waits for its
/// use-done: the server's answer carries no spell, so the casts out are
/// counted, and an answer names a spell only while every cast out is the
/// same spell at the same target.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct SpellCast {
    last_requested_spell: Option<u32>,
    last_requested_target: Option<u32>,
    pending_spell: Option<u32>,
    pending_target: Option<u32>,
    outstanding_receipts: u64,
    last_completion: SpellCastCompletion,
    last_result: Option<CastRequestResult>,
    requests: u64,
}

impl SpellCast {
    /// What the last cast request did (a plugin's cast reads its answer
    /// here).
    #[must_use]
    pub fn last_result(&self) -> Option<CastRequestResult> {
        self.last_result
    }

    /// How many cast requests were made (sent or refused), so a caller
    /// can tell that [`Self::last_result`] answers its own request.
    #[must_use]
    pub fn request_count(&self) -> u64 {
        self.requests
    }

    /// The last spell asked for.
    #[must_use]
    pub fn last_requested_spell(&self) -> Option<u32> {
        self.last_requested_spell
    }

    /// The last target asked for.
    #[must_use]
    pub fn last_requested_target(&self) -> Option<u32> {
        self.last_requested_target
    }

    /// The spell whose use-done is awaited (`None` with none out, or with
    /// casts of different spells or targets out).
    #[must_use]
    pub fn pending_spell(&self) -> Option<u32> {
        self.pending_spell
    }

    /// The target of the awaited spell.
    #[must_use]
    pub fn pending_target(&self) -> Option<u32> {
        self.pending_target
    }

    /// The last answer.
    #[must_use]
    pub fn last_completion(&self) -> SpellCastCompletion {
        self.last_completion
    }

    /// Whether the spell could be cast at `selected` now.
    pub fn evaluate_gate(
        spellbook: &Spellbook,
        spell_id: u32,
        selected: Option<u32>,
        ops: &mut impl SpellCastOperations,
    ) -> SpellCastGate {
        let Some(spell) = known(spellbook, spell_id) else {
            return SpellCastGate::Unknown;
        };
        if spell.is_self_targeted() || spell.is_untargeted || spell.target_mask == 0 {
            return SpellCastGate::NoTargetNeeded;
        }
        let Some(target) = selected.filter(|&t| t != 0) else {
            return SpellCastGate::NoTargetSelected;
        };
        if ops.is_target_compatible(target, spell, false) {
            SpellCastGate::TargetCompatible
        } else {
            SpellCastGate::TargetIncompatible
        }
    }

    /// Ask to cast a known spell: at the player for a self spell, at
    /// nothing for an untargeted one, else at the selection.
    pub fn cast(
        &mut self,
        spellbook: &Spellbook,
        spell_id: u32,
        selected: Option<u32>,
        ops: &mut impl SpellCastOperations,
    ) -> CastRequestResult {
        let result = self.request(spellbook, spell_id, selected, ops);
        self.last_result = Some(result);
        self.requests = self.requests.wrapping_add(1);
        result
    }

    fn request(
        &mut self,
        spellbook: &Spellbook,
        spell_id: u32,
        selected: Option<u32>,
        ops: &mut impl SpellCastOperations,
    ) -> CastRequestResult {
        let Some(spell) = known(spellbook, spell_id) else {
            ops.display_message("You do not know that spell.");
            return CastRequestResult::UnknownSpell;
        };
        if !ops.has_required_components(spell_id) {
            ops.display_message("You do not have all of this spell's components.");
            return CastRequestResult::MissingComponents;
        }
        let (target, untargeted) = if spell.is_self_targeted() {
            let player = ops.local_player_id();
            let target = (player != 0).then_some(player);
            (target, target.is_none())
        } else if spell.is_untargeted || spell.target_mask == 0 {
            (None, true)
        } else {
            let Some(target) = selected.filter(|&t| t != 0) else {
                ops.display_message("You must select a suitable target.");
                return CastRequestResult::NoTarget;
            };
            if !ops.is_target_compatible(target, spell, true) {
                return CastRequestResult::IncompatibleTarget;
            }
            (Some(target), false)
        };
        // Only being out of the world refuses here: a cast still waiting
        // for its use-done does not hold up the next request.
        if !ops.can_send() {
            ops.display_message("You cannot cast a spell right now.");
            return CastRequestResult::Unavailable;
        }
        ops.stop_completely();
        self.last_requested_spell = Some(spell_id);
        self.last_requested_target = target;
        if self.outstanding_receipts == 0 {
            self.pending_spell = Some(spell_id);
            self.pending_target = target;
        } else if self.pending_spell != Some(spell_id) || self.pending_target != target {
            // Casts of different spells (or at different targets) are out:
            // an answer can no longer be told apart.
            self.pending_spell = None;
            self.pending_target = None;
        }
        match target {
            Some(target) if !untargeted => ops.send_targeted(target, spell_id),
            _ => ops.send_untargeted(spell_id),
        }
        ops.increment_busy();
        self.outstanding_receipts = self.outstanding_receipts.saturating_add(1);
        CastRequestResult::Sent
    }

    /// The server's use-done for a cast: records the answer when the casts
    /// out name one spell. False when no cast was out, or when the answer
    /// cannot be told apart.
    pub fn complete_use(&mut self, weenie_error: u32) -> bool {
        if self.outstanding_receipts == 0 {
            return false;
        }
        self.outstanding_receipts -= 1;
        let Some(spell_id) = self.pending_spell else {
            return false;
        };
        self.last_completion = SpellCastCompletion {
            revision: self.last_completion.revision + 1,
            spell_id,
            target_id: self.pending_target.unwrap_or(0),
            weenie_error,
        };
        if self.outstanding_receipts == 0 {
            self.pending_spell = None;
            self.pending_target = None;
        }
        true
    }

    /// Forget everything (the session ended).
    pub fn reset(&mut self) {
        *self = Self::default();
    }
}

fn known(spellbook: &Spellbook, spell_id: u32) -> Option<&SpellMetadata> {
    if !spellbook.knows(spell_id) {
        return None;
    }
    spellbook.try_get_metadata(spell_id)
}

// -- Enchantment records ----------------------------------------------------

/// The bucket the stat-mod type implies, for records sent without one.
#[must_use]
pub fn classify_bucket(stat_mod_type: u32) -> u32 {
    const MULTIPLICATIVE: u32 = 0x0000_4000;
    const ADDITIVE: u32 = 0x0000_8000;
    const VITAE: u32 = 0x0080_0000;
    const COOLDOWN: u32 = 0x0100_0000;
    if stat_mod_type & VITAE != 0 {
        BUCKET_VITAE
    } else if stat_mod_type & COOLDOWN != 0 {
        BUCKET_COOLDOWN
    } else if stat_mod_type & MULTIPLICATIVE != 0 {
        BUCKET_MULTIPLICATIVE
    } else if stat_mod_type & ADDITIVE != 0 {
        BUCKET_ADDITIVE
    } else {
        0
    }
}

/// A wire enchantment as the spellbook keeps it: its times moved onto the
/// session clock (`received_at`, seconds), its bucket from the record or
/// classified from its stat-mod type.
#[must_use]
pub fn active_enchantment(
    e: &ac_proto::Enchantment,
    bucket: Option<u32>,
    received_at: f64,
) -> ActiveEnchantmentRecord {
    ActiveEnchantmentRecord {
        spell_id: u32::from(e.spell_id),
        layer_id: u32::from(e.layer),
        duration: e.duration,
        caster_guid: e.caster_guid,
        stat_mod_type: Some(e.stat_mod_type),
        stat_mod_key: Some(e.stat_mod_key),
        stat_mod_value: Some(e.stat_mod_value),
        bucket: bucket
            .filter(|&b| b != 0)
            .unwrap_or_else(|| classify_bucket(e.stat_mod_type)),
        start_time: received_at + e.start_time,
        spell_category: u32::from(e.spell_category),
        power_level: e.power_level,
        degrade_modifier: e.degrade_modifier,
        degrade_limit: e.degrade_limit,
        last_time_degraded: received_at + e.last_time_degraded,
        spell_set_id: e.spell_set_id,
    }
}

fn description_enchantment(e: &PlayerEnchantment, received_at: f64) -> ActiveEnchantmentRecord {
    active_enchantment(&e.enchantment, Some(e.bucket as u32), received_at)
}

/// The character description's spellbook part: learned spells,
/// enchantments, favorite bars, desired components, the filter.
pub fn apply_description(world: &mut World, description: &PlayerDescription, received_at: f64) {
    let enchantments: Vec<_> = description
        .enchantments
        .iter()
        .map(|e| description_enchantment(e, received_at))
        .collect();
    let desired: Vec<(u32, u32)> = description
        .desired_comps
        .iter()
        .map(|c| (c.id, c.amount))
        .collect();
    world.update_spellbook(|book| {
        book.replace_manifest(
            description.spells.iter().map(|(&id, &power)| (id, power)),
            enchantments,
            &description.hotbar_spells,
            &desired,
            description.spellbook_filters,
        );
    });
}

/// One magic event applied to the spellbook. Returns the text the player
/// reads when an enchantment ran out (a dispel says so in its own chat).
///
/// # Errors
/// The payload did not parse.
pub fn apply_event(
    world: &mut World,
    event: &GameEventEnvelope<'_>,
    received_at: f64,
) -> Result<Vec<String>, ProtoError> {
    let payload = event.payload;
    let mut expired = Vec::new();
    match event.event_type {
        GameEventType::MAGIC_UPDATE_SPELL => {
            let spell = magic::parse_spell_id(payload)?;
            // The update carries no power; a learned spell starts at 0.
            world.update_spellbook(|b| b.on_spell_learned(spell, 0.0));
        }
        GameEventType::MAGIC_REMOVE_SPELL => {
            let spell = magic::parse_spell_id(payload)?;
            world.update_spellbook(|b| b.on_spell_forgotten(spell));
        }
        GameEventType::MAGIC_UPDATE_ENCHANTMENT => {
            let e = magic::parse_update_enchantment(payload)?;
            let record = active_enchantment(&e, None, received_at);
            world.update_spellbook(|b| b.on_enchantment_added(record));
        }
        GameEventType::MAGIC_UPDATE_MULTIPLE_ENCHANTMENTS => {
            let list = magic::parse_update_multiple_enchantments(payload)?;
            let records: Vec<_> = list
                .iter()
                .map(|e| active_enchantment(e, None, received_at))
                .collect();
            world.update_spellbook(|b| b.on_enchantments_added(records));
        }
        GameEventType::MAGIC_REMOVE_ENCHANTMENT => {
            let id = magic::LayeredSpellId::parse(payload)?;
            let (spell, layer) = (u32::from(id.spell_id), u32::from(id.layer));
            world.update_spellbook(|b| b.on_enchantment_removed(layer, spell));
            expired.extend(expiry_text(world, spell));
        }
        GameEventType::MAGIC_REMOVE_MULTIPLE_ENCHANTMENTS => {
            let list = magic::parse_layered_spell_list(payload)?;
            world.update_spellbook(|b| {
                b.on_enchantments_removed(
                    list.iter()
                        .map(|i| (u32::from(i.spell_id), u32::from(i.layer))),
                )
            });
            for item in &list {
                expired.extend(expiry_text(world, u32::from(item.spell_id)));
            }
        }
        GameEventType::MAGIC_DISPEL_ENCHANTMENT => {
            let id = magic::LayeredSpellId::parse(payload)?;
            world.update_spellbook(|b| {
                b.on_enchantment_removed(u32::from(id.layer), u32::from(id.spell_id))
            });
        }
        GameEventType::MAGIC_DISPEL_MULTIPLE_ENCHANTMENTS => {
            let list = magic::parse_layered_spell_list(payload)?;
            world.update_spellbook(|b| {
                b.on_enchantments_removed(
                    list.iter()
                        .map(|i| (u32::from(i.spell_id), u32::from(i.layer))),
                )
            });
        }
        GameEventType::MAGIC_PURGE_ENCHANTMENTS => {
            world.update_spellbook(Spellbook::on_purge_all);
        }
        GameEventType::MAGIC_PURGE_BAD_ENCHANTMENTS => {
            world.update_spellbook(Spellbook::on_purge_bad_enchantments);
        }
        _ => {}
    }
    Ok(expired)
}

/// "<name> has expired." for a spell with a record (the vitae spell reads
/// "<name> penalty"); nothing for a cooldown or a spell without a record.
fn expiry_text(world: &World, spell_id: u32) -> Option<String> {
    if spell_id >= 0x8000 {
        return None;
    }
    let meta = world.spellbook()?.try_get_metadata(spell_id)?;
    Some(if spell_id == VITAE_PENALTY_SPELL_ID {
        format!("{} penalty has expired.", meta.name)
    } else {
        format!("{} has expired.", meta.name)
    })
}

/// The text class of the expiry notice.
pub const EXPIRY_TEXT_TYPE: LogTextType = LogTextType::Magic;

// -- Component and target checks over the world ------------------------------

/// The component check's view of the world.
#[derive(Debug)]
pub struct ComponentWorld<'a>(pub &'a World);

impl<'a> ComponentWorld<'a> {
    fn table(&self) -> TableComponentWorld<'a> {
        TableComponentWorld {
            objects: self.0.items(),
            player: self.0.player_guid().unwrap_or(0),
        }
    }
}

impl SpellComponentWorld for ComponentWorld<'_> {
    fn player_id(&self) -> u32 {
        self.table().player_id()
    }

    fn player_bool(&self, key: PropertyBool) -> Option<bool> {
        self.table().player_bool(key)
    }

    fn player_int(&self, key: PropertyInt) -> Option<i32> {
        self.table().player_int(key)
    }

    fn objects(&self) -> impl Iterator<Item = ComponentObject> + '_ {
        self.0.items().objects().map(component_object)
    }

    fn object(&self, object_id: u32) -> Option<ComponentObject> {
        self.table().object(object_id)
    }
}

/// The component check's view of a copy of the item table (a window's).
#[derive(Debug, Clone, Copy)]
pub struct TableComponentWorld<'a> {
    /// The item table.
    pub objects: &'a ac_core::items::ClientObjectTable,
    /// The player's object id (0 before the character entered).
    pub player: u32,
}

impl SpellComponentWorld for TableComponentWorld<'_> {
    fn player_id(&self) -> u32 {
        self.player
    }

    fn player_bool(&self, key: PropertyBool) -> Option<bool> {
        let player = self.objects.get(self.player)?;
        player.properties.bools.get(&key.0).copied()
    }

    fn player_int(&self, key: PropertyInt) -> Option<i32> {
        let player = self.objects.get(self.player)?;
        player.properties.ints.get(&key.0).copied()
    }

    fn objects(&self) -> impl Iterator<Item = ComponentObject> + '_ {
        self.objects.objects().map(component_object)
    }

    fn object(&self, object_id: u32) -> Option<ComponentObject> {
        self.objects.get(object_id).map(component_object)
    }
}

/// An examined spell's components in formula order, each with whether
/// the player carries one: the formula the player would cast it with
/// (scarabs and prismatic tapers when the school's focus is carried, the
/// school is infused or components are switched off; otherwise the
/// account's personalised recipe), empty slots dropped.
#[must_use]
pub fn examine_components(
    requirements: &SpellComponentRequirements,
    world: &impl SpellComponentWorld,
    account_name: &str,
    spell: &SpellMetadata,
) -> Vec<(u32, bool)> {
    let formula = SpellFormulaDefinition {
        formula_version: spell.formula_version,
        component_ids: spell.formula_components.clone(),
        school: spell.school_id.0,
    };
    requirements
        .appropriate_formula_for(world, &formula, account_name)
        .into_iter()
        .filter(|&id| id != 0)
        .map(|id| (id, requirements.is_component_owned(world, id)))
        .collect()
}

fn component_object(item: &ac_core::items::ClientObject) -> ComponentObject {
    ComponentObject {
        object_id: item.object_id(),
        weenie_class_id: item.weenie_class_id,
        container_id: item.container_id,
        wielder_id: item.wielder_id,
    }
}

/// Whether `target_id` can take the spell, and the refusal text when not.
#[must_use]
pub fn target_check(world: &World, target_id: u32, spell: &SpellMetadata) -> Option<String> {
    target_check_in(
        world.items(),
        world.player_guid().unwrap_or(0),
        target_id,
        spell,
    )
}

/// [`target_check`] against a copy of the item table (a window's).
#[must_use]
pub fn target_check_in(
    objects: &ac_core::items::ClientObjectTable,
    player: u32,
    target_id: u32,
    spell: &SpellMetadata,
) -> Option<String> {
    let Some(target) = objects.get(target_id) else {
        return Some(String::new());
    };
    let candidate = SpellTargetCandidate {
        object_id: target_id,
        stack_size: target.stack_size,
        item_type: target.item_type,
        name: &target.name,
        public_weenie_bitfield: target.public_weenie_bitfield.map(|f| f.bits()),
        pet_owner_id: target.pet_owner_id,
    };
    evaluate(player, &candidate, spell)
        .err()
        .map(|refusal| refusal.message())
}

/// Whether `spell_id` could be cast at `selected` now, judged against a
/// copy of the item table ([`SpellCast::evaluate_gate`] with the session's
/// target check).
#[must_use]
pub fn cast_gate(
    objects: &ac_core::items::ClientObjectTable,
    player: u32,
    spellbook: &Spellbook,
    spell_id: u32,
    selected: Option<u32>,
) -> SpellCastGate {
    let Some(spell) = known(spellbook, spell_id) else {
        return SpellCastGate::Unknown;
    };
    if spell.is_self_targeted() || spell.is_untargeted || spell.target_mask == 0 {
        return SpellCastGate::NoTargetNeeded;
    }
    let Some(target) = selected.filter(|&t| t != 0) else {
        return SpellCastGate::NoTargetSelected;
    };
    if target_check_in(objects, player, target, spell).is_none() {
        SpellCastGate::TargetCompatible
    } else {
        SpellCastGate::TargetIncompatible
    }
}

// -- Views --------------------------------------------------------------------

/// One spell as a host lists it.
#[derive(Debug, Clone, PartialEq)]
pub struct SpellSummary {
    /// Spell id.
    pub spell_id: u32,
    /// Name (empty without the spell table).
    pub name: String,
    /// School name.
    pub school: String,
    /// Spell level by the formula's power.
    pub level: i32,
    /// Mana cost.
    pub mana: i32,
    /// Whether the player carries its components.
    pub has_components: bool,
}

/// The spellbook as a host shows it.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct SpellbookView {
    /// Known spells (matching the filter), by name.
    pub spells: Vec<SpellSummary>,
    /// How many spells the player knows in all.
    pub known: usize,
    /// The favorite bars.
    pub favorites: Vec<Vec<u32>>,
    /// The spellbook filter bits.
    pub filters: u32,
    /// Desired component amounts.
    pub desired_components: Vec<(u32, u32)>,
}

/// One active enchantment as a host shows it.
#[derive(Debug, Clone, PartialEq)]
pub struct EnchantmentView {
    /// Spell id.
    pub spell_id: u32,
    /// Its name (empty without the spell table).
    pub name: String,
    /// Layer.
    pub layer: u32,
    /// Seconds left; `None` for a permanent one.
    pub remaining_seconds: Option<f64>,
    /// Who cast it.
    pub caster_guid: u32,
    /// Bucket (1 multiplicative, 2 additive, 4 vitae, 8 cooldown).
    pub bucket: u32,
    /// Stat-mod type, key and value.
    pub stat_mod: (u32, u32, f32),
}

/// One number on the character sheet: its base and its value with spells.
#[derive(Debug, Clone, PartialEq)]
pub struct SheetLine {
    /// Name.
    pub name: String,
    /// Without enchantments.
    pub base: i64,
    /// With enchantments (and vitae).
    pub current: i64,
}

/// The player's numbers with enchantments applied.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CharacterSheet {
    /// Health, stamina, mana: current over enchanted maximum.
    pub vitals: Vec<(String, u32, u32)>,
    /// The six attributes.
    pub attributes: Vec<SheetLine>,
    /// Trained and specialized skills.
    pub skills: Vec<SheetLine>,
    /// The vitae multiplier (1.0 with no death penalty).
    pub vitae: f32,
}

/// The known spells whose name contains `filter` (all with `None`).
#[must_use]
pub fn spellbook_view(
    world: &World,
    tables: &super::tables::GameTables,
    account: &str,
    filter: Option<&str>,
) -> SpellbookView {
    let Some(book) = world.spellbook() else {
        return SpellbookView::default();
    };
    let wanted = filter.map(str::to_lowercase);
    let components = ComponentWorld(world);
    let mut spells: Vec<SpellSummary> = book
        .learned_spells()
        .map(|id| {
            let meta = book.try_get_metadata(id);
            SpellSummary {
                spell_id: id,
                name: meta.map(|m| m.name.clone()).unwrap_or_default(),
                school: meta.map(|m| m.school.clone()).unwrap_or_default(),
                level: meta.map_or(0, |m| m.generation),
                mana: meta.map_or(0, |m| m.mana_cost),
                has_components: tables.components().has_required_components(
                    &components,
                    account,
                    id,
                ),
            }
        })
        .filter(|s| {
            wanted
                .as_ref()
                .is_none_or(|w| s.name.to_lowercase().contains(w.as_str()))
        })
        .collect();
    spells.sort_by(|a, b| a.name.cmp(&b.name).then(a.spell_id.cmp(&b.spell_id)));
    SpellbookView {
        spells,
        known: book.learned_count(),
        favorites: (0..ac_core::spells::FAVORITE_BAR_COUNT)
            .map(|tab| book.get_favorites(tab).to_vec())
            .collect(),
        filters: book.spellbook_filters(),
        desired_components: book
            .desired_components()
            .iter()
            .map(|(&k, &v)| (k, v))
            .collect(),
    }
}

/// The active enchantments, newest first within each bucket, with their
/// time left at session time `now` (seconds).
#[must_use]
pub fn enchantments_view(world: &World, now: f64) -> Vec<EnchantmentView> {
    let Some(book) = world.spellbook() else {
        return Vec::new();
    };
    book.active_enchantments()
        .iter()
        .map(|e| EnchantmentView {
            spell_id: e.spell_id,
            name: book
                .try_get_metadata(e.spell_id)
                .map(|m| m.name.clone())
                .unwrap_or_default(),
            layer: e.layer_id,
            remaining_seconds: (e.duration >= 0.0)
                .then(|| (e.start_time + e.duration - now).max(0.0)),
            caster_guid: e.caster_guid,
            bucket: e.bucket,
            stat_mod: (
                e.stat_mod_type.unwrap_or(0),
                e.stat_mod_key.unwrap_or(0),
                e.stat_mod_value.unwrap_or(0.0),
            ),
        })
        .collect()
}

/// The player's numbers with enchantments, the way the character sheet
/// reads them.
#[must_use]
pub fn character_sheet(world: &World) -> CharacterSheet {
    let Some(player) = world.player() else {
        return CharacterSheet::default();
    };
    let n = player.numbers();
    let vitals = [
        ("Health", VitalKind::Health),
        ("Stamina", VitalKind::Stamina),
        ("Mana", VitalKind::Mana),
    ]
    .into_iter()
    .filter_map(|(name, kind)| {
        let v = n.get(kind)?;
        Some((name.to_owned(), v.current, n.get_max_approx(kind)?))
    })
    .collect();
    let attributes = [
        ("Strength", AttributeKind::Strength),
        ("Endurance", AttributeKind::Endurance),
        ("Coordination", AttributeKind::Coordination),
        ("Quickness", AttributeKind::Quickness),
        ("Focus", AttributeKind::Focus),
        ("Self", AttributeKind::Self_),
    ]
    .into_iter()
    .filter_map(|(name, kind)| {
        let a = n.get_attribute(kind)?;
        Some(SheetLine {
            name: name.to_owned(),
            base: i64::from(a.current()),
            current: i64::from(n.get_effective_attribute(kind)?),
        })
    })
    .collect();
    let skills = n
        .skills()
        .values()
        // Trained (2) and specialized (3).
        .filter(|s| s.status >= 2)
        .filter_map(|s| {
            Some(SheetLine {
                name: skill_names::describe(s.skill_id.cast_signed()),
                base: i64::from(s.base_level()),
                current: i64::from(n.get_effective_skill(s.skill_id)?),
            })
        })
        .collect();
    let vitae = player
        .spellbook()
        .map_or(1.0, |b| get_vitae_multiplier(b.active_enchantments()));
    CharacterSheet {
        vitals,
        attributes,
        skills,
        vitae,
    }
}

/// A spell named by id or by name among the known spells (exact before
/// prefix, without case). `None` when nothing matches.
#[must_use]
pub fn resolve_spell(world: &World, text: &str) -> Option<u32> {
    let text = text.trim();
    let id = match text.strip_prefix("0x").or_else(|| text.strip_prefix("0X")) {
        Some(hex) => u32::from_str_radix(hex, 16).ok(),
        None => text.parse::<u32>().ok(),
    };
    if id.is_some() {
        return id;
    }
    let book = world.spellbook()?;
    let wanted = text.to_lowercase();
    let mut best: Option<(bool, String, u32)> = None;
    for spell in book.learned_spells() {
        let Some(meta) = book.try_get_metadata(spell) else {
            continue;
        };
        let name = meta.name.to_lowercase();
        let exact = name == wanted;
        if !exact && !name.starts_with(&wanted) {
            continue;
        }
        let rank = (!exact, name, spell);
        if best.as_ref().is_none_or(|b| rank < *b) {
            best = Some(rank);
        }
    }
    best.map(|(_, _, id)| id)
}

#[cfg(test)]
#[path = "magic_tests.rs"]
mod tests;
