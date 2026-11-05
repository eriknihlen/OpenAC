//! Spells and magic (API group E): the spell catalog, casting and its
//! gate, and the enchantment tracker plugins share.

use crate::{wire_enum, wire_record};

wire_record! {
    /// Everything the client knows about one spell, from the spell table.
    pub struct SpellInfo {
        /// Its id.
        pub spell_id: u32,
        /// Its name.
        pub name: String,
        /// Its family (one of a family is in force at a time).
        pub family: u32,
        /// Its rank in the family.
        pub tier: i32,
        /// The skill it demands.
        pub difficulty: i32,
        /// Mana one cast costs.
        pub mana_cost: i32,
        /// How long its effect lasts, seconds; 0 for none.
        pub duration_seconds: f32,
        /// The magic skill it is cast with (31 creature, 32 item, 33 life,
        /// 34 war, 43 void), 0 when unknown.
        pub school: u32,
        /// Its description.
        pub description: String,
        /// It can only be cast on the caster.
        pub is_self_targeted: bool,
        /// It helps its target.
        pub is_beneficial: bool,
        /// A debuff.
        pub is_debuff: bool,
        /// Offensive.
        pub is_offensive: bool,
        /// A fellowship spell.
        pub is_fellowship: bool,
        /// Cast at nothing.
        pub is_untargeted: bool,
        /// The caster turns to the target first.
        pub requires_turn_to: bool,
        /// A projectile.
        pub is_projectile: bool,
        /// Damage over time.
        pub is_damage_over_time: bool,
        /// The spell table's flag bits.
        pub raw_flags: u32,
        /// Its meta type.
        pub spell_type: i32,
        /// What it can be cast at (item type bits).
        pub target_mask: u32,
        /// Base range constant.
        pub base_range_constant: f32,
        /// Base range modifier.
        pub base_range_modifier: f32,
        /// Its formula's components.
        pub formula_component_ids: Vec<u32>,
        /// Its icon.
        pub icon_id: u32,
        /// What the caster says.
        pub saying: String,
        /// The herb, powder, potion and talisman of its formula.
        pub component_set: (u32, u32, u32, u32),
    }
}

/// The most spells one [`SpellPage`] carries.
pub const SPELL_PAGE_MAX: u32 = 256;

/// Unused by the client since 2026-10-14: it counts no fuel and grants
/// none (a call is bounded by wall time). Until then, the
/// fuel a plugin's call was granted for each record of a paged reply it
/// received: twice what decoding one spell record was measured to cost.
/// Kept, unchanged, so tools that model the old budgets (the plugins' fuel
/// harnesses) still build; it is no part of the calling convention.
pub const FUEL_PER_PAGED_RECORD: u64 = 32_000;

/// Unused by the client since 2026-10-14, as [`FUEL_PER_PAGED_RECORD`]:
/// the most fuel paged replies granted one call in all.
pub const FUEL_PAGED_GRANT_MAX: u64 = 400_000_000;

wire_record! {
    /// One page of the spell table ([`crate::Import::SpellsPage`]).
    pub struct SpellPage {
        /// How many spells the table holds.
        pub total: u32,
        /// This page's spells, in id order.
        pub spells: Vec<SpellInfo>,
    }
}

/// How many records a paged reply carries; `None` for a reply that is not a
/// page. (The client used it for fuel grants until 2026-10-14, see
/// [`FUEL_PER_PAGED_RECORD`].)
#[must_use]
pub fn paged_records(import: crate::Import, reply: &[u8]) -> Option<usize> {
    use crate::Wire;
    match import {
        crate::Import::SpellsPage => SpellPage::from_bytes(reply).ok().map(|p| p.spells.len()),
        _ => None,
    }
}

impl SpellInfo {
    /// The spell's quality (its difficulty).
    #[must_use]
    pub fn quality(&self) -> i32 {
        self.difficulty
    }
}

wire_record! {
    /// One spell component, from the component table.
    pub struct ComponentInfo {
        /// Its component id.
        pub component_id: u32,
        /// The weenie class carrying it.
        pub weenie_class_id: u32,
        /// Its name.
        pub name: String,
        /// How fast it burns.
        pub burn_rate: f64,
        /// The gesture it makes.
        pub gesture_id: u32,
        /// The gesture's speed.
        pub gesture_speed: f64,
        /// Its icon.
        pub icon_id: u32,
        /// Its category (the sort key).
        pub sort_key: u32,
        /// Its type's name.
        pub component_type: String,
        /// The word it speaks.
        pub word: String,
    }
}

wire_enum! {
    /// Which known spells a list holds.
    pub enum SpellList: "spell list" {
        /// Beneficial spells the caster can cast on itself, by family then
        /// highest tier.
        SelfBuffs = 0,
        /// Direct attacks, highest tier and difficulty first.
        Attacks = 1,
        /// Offensive spells and debuffs, highest tier then name.
        Combat = 2,
    }
}

wire_enum! {
    /// Whether a spell could be cast now.
    pub enum CastGate: "cast gate" {
        /// Not in the world.
        Unavailable = 0,
        /// It can be cast.
        Ready = 1,
        /// Not in the spellbook.
        NotKnown = 2,
        /// The client holds up another cast (kept for the wire; a cast
        /// still waiting for its answer does not hold one up).
        Busy = 3,
        /// It needs a target and none is selected.
        NoTargetSelected = 4,
        /// The selection cannot take it.
        TargetIncompatible = 5,
        /// The target named could not be selected.
        Refused = 6,
    }
}

wire_enum! {
    /// What a cast request did.
    pub enum CastResult: "cast result" {
        /// Sent.
        Sent = 0,
        /// Not in the spellbook.
        UnknownSpell = 1,
        /// It needs a target and none is selected.
        NoTarget = 2,
        /// The target cannot take it.
        IncompatibleTarget = 3,
        /// Components are missing.
        MissingComponents = 4,
        /// Not now (not in the world, or a cast is in flight).
        Unavailable = 5,
    }
}

wire_record! {
    /// Cast a spell, at a target or (0) at the selection.
    pub struct CastRequest {
        /// The spell.
        pub spell_id: u32,
        /// The target, selected first; 0 for the selection (or none).
        pub target_id: u32,
    }
}

wire_record! {
    /// The server's answer to the last cast.
    pub struct CastCompletion {
        /// Counts completions; 0 before the first.
        pub revision: i64,
        /// The spell.
        pub spell_id: u32,
        /// Its target, 0 for none.
        pub target_id: u32,
        /// The server's error, 0 for success.
        pub weenie_error: u32,
    }
}

impl CastCompletion {
    /// The cast went through.
    #[must_use]
    pub fn is_success(&self) -> bool {
        self.revision != 0 && self.weenie_error == 0
    }
}

wire_record! {
    /// An enchantment the tracker believes is on a target.
    pub struct TrackedEnchantment {
        /// The target.
        pub target_id: u32,
        /// The spell.
        pub spell_id: u32,
        /// Its family.
        pub family: u32,
        /// Its quality (difficulty).
        pub quality: i32,
        /// An untargeted spell.
        pub is_untargeted: bool,
        /// Seconds left.
        pub seconds_remaining: f64,
    }
}

wire_record! {
    /// Tell the tracker a spell landed on a target for so long.
    pub struct ReportedCast {
        /// The target.
        pub target_id: u32,
        /// The spell.
        pub spell_id: u32,
        /// How long, seconds.
        pub duration_seconds: f64,
    }
}
