//! Golden behavior from OpenAC
//! `tests/AcDream.Core.Tests/Player/LocalPlayerStateTests.cs` and
//! `SkillLevelConformanceTests.cs`.

use std::collections::BTreeMap;
use std::sync::Arc;

use super::*;
use crate::spells::{ActiveEnchantmentRecord, EnchantmentTypeFlags, SpellMetadata, SpellTable};

fn book(ids: &[u32]) -> Spellbook {
    Spellbook::new(Some(Arc::new(
        SpellTable::create(ids.iter().map(|&id| SpellMetadata::new(id, "Test", 0)))
            .expect("unique"),
    )))
}

/// A record as the server sends one: its operation bit (multiply or add)
/// matches its list, and its spell category is its own (the spell id).
fn typed(
    spell_id: u32,
    layer: u32,
    stat_mod_type: u32,
    key: u32,
    value: f32,
    bucket: u32,
) -> ActiveEnchantmentRecord {
    let operation = match bucket {
        1 => MULTIPLICATIVE,
        2 => ADDITIVE,
        // Vitae records carry the multiplicative bit.
        4 => MULTIPLICATIVE,
        _ => 0,
    };
    ActiveEnchantmentRecord {
        stat_mod_type: Some(stat_mod_type | operation),
        stat_mod_key: Some(key),
        stat_mod_value: Some(value),
        bucket,
        spell_category: spell_id,
        ..ActiveEnchantmentRecord::new(spell_id, layer, 60.0, 0)
    }
}

const MULTIPLICATIVE: u32 = 0x4000;
const ADDITIVE: u32 = 0x8000;

fn vitae(spell_id: u32, value: f32) -> ActiveEnchantmentRecord {
    ActiveEnchantmentRecord {
        duration: -1.0,
        ..typed(spell_id, 1, 0, 0, value, 4)
    }
}

const ATTRIBUTE: u32 = EnchantmentTypeFlags::ATTRIBUTE.0;
const SECOND_ATT: u32 = EnchantmentTypeFlags::SECOND_ATT.0;
const SKILL: u32 = EnchantmentTypeFlags::SKILL.0;

fn close(actual: f32, expected: f32) -> bool {
    (actual - expected).abs() < 1e-3
}

#[test]
fn defaults_are_unknown() {
    let s = LocalPlayerState::default();
    for kind in VitalKind::ALL {
        assert_eq!(s.get(kind), None);
    }
    assert_eq!(s.health_percent(), None);
    assert_eq!(s.stamina_percent(), None);
    assert_eq!(s.mana_percent(), None);
}

#[test]
fn vital_ids_map_both_id_systems() {
    for (id, kind) in [
        (1, VitalKind::Health),
        (2, VitalKind::Health),
        (3, VitalKind::Stamina),
        (4, VitalKind::Stamina),
        (5, VitalKind::Mana),
        (6, VitalKind::Mana),
        (7, VitalKind::Health),
        (8, VitalKind::Stamina),
        (9, VitalKind::Mana),
    ] {
        assert_eq!(vital_id_to_kind(id), Some(kind));
    }
    for id in [0, 10, 99] {
        assert_eq!(vital_id_to_kind(id), None);
    }
}

#[test]
fn attribute_ids_map_primary_attributes() {
    for (id, kind) in [
        (1, AttributeKind::Strength),
        (2, AttributeKind::Endurance),
        (3, AttributeKind::Quickness),
        (4, AttributeKind::Coordination),
        (5, AttributeKind::Focus),
        (6, AttributeKind::Self_),
    ] {
        assert_eq!(attribute_id_to_kind(id), Some(kind));
        assert_eq!(kind.id(), id);
    }
    for id in [0, 7, 99] {
        assert_eq!(attribute_id_to_kind(id), None);
    }
}

#[test]
fn vital_update_populates_snapshot() {
    let mut s = LocalPlayerState::default();
    assert_eq!(
        s.on_vital_update(4, 100, 120, 50_000, 180),
        Some(VitalKind::Stamina)
    );
    assert_eq!(
        s.get(VitalKind::Stamina),
        Some(VitalSnapshot {
            ranks: 100,
            start: 120,
            xp: 50_000,
            current: 180
        })
    );
}

#[test]
fn attribute_update_populates_snapshot() {
    let mut s = LocalPlayerState::default();
    s.on_attribute_update(2, 50, 100, 12_345);
    let endurance = s.get_attribute(AttributeKind::Endurance).expect("received");
    assert_eq!(endurance.ranks, 50);
    assert_eq!(endurance.start, 100);
    assert_eq!(endurance.xp, 12_345);
    assert_eq!(endurance.current(), 150);
}

#[test]
fn health_uses_half_endurance() {
    let mut s = LocalPlayerState::default();
    s.on_attribute_update(2, 50, 150, 0);
    s.on_vital_update(7, 0, 0, 0, 80);
    assert_eq!(s.get_max_approx(VitalKind::Health), Some(100));
    assert!(close(s.health_percent().expect("known"), 0.8));
}

#[test]
fn stamina_uses_full_endurance() {
    let mut s = LocalPlayerState::default();
    s.on_attribute_update(2, 50, 150, 0);
    s.on_vital_update(8, 0, 0, 0, 150);
    assert_eq!(s.get_max_approx(VitalKind::Stamina), Some(200));
    assert!(close(s.stamina_percent().expect("known"), 0.75));
}

#[test]
fn mana_uses_self() {
    let mut s = LocalPlayerState::default();
    s.on_attribute_update(6, 50, 50, 0);
    s.on_vital_update(9, 20, 80, 0, 100);
    assert_eq!(s.get_max_approx(VitalKind::Mana), Some(200));
    assert!(close(s.mana_percent().expect("known"), 0.5));
}

#[test]
fn zero_maximum_has_no_percent() {
    let mut s = LocalPlayerState::default();
    s.on_vital_update(8, 0, 0, 0, 0);
    assert_eq!(s.stamina_percent(), None);
}

#[test]
fn percent_clamps_to_one() {
    let mut s = LocalPlayerState::default();
    s.on_attribute_update(2, 50, 50, 0);
    s.on_vital_update(8, 0, 0, 0, 150);
    assert_eq!(s.stamina_percent(), Some(1.0));
}

#[test]
fn attribute_modifier_with_colliding_key_does_not_affect_health() {
    let mut b = book(&[1]);
    b.on_enchantment_added(typed(1, 1, ATTRIBUTE, 1, 0.99999, 1));
    let mut s = LocalPlayerState::new(Some(b));
    s.on_vital_update(7, 99_999, 0, 0, 99_999);
    assert_eq!(s.get_base_max_approx(VitalKind::Health), Some(99_999));
    assert_eq!(s.get_max_approx(VitalKind::Health), Some(99_999));
    assert_eq!(s.health_percent(), Some(1.0));
}

// GetMaxApprox_SecondaryAttributeModifierRoundsToNearest (OpenAC v0.1.25,
// #211: 100.75 shows as 101, no longer truncated to 100).
#[test]
fn secondary_attribute_modifier_rounds_to_nearest() {
    let mut b = book(&[1]);
    b.on_enchantment_added(typed(1, 1, SECOND_ATT, STAT_KEY_MAX_HEALTH, 0.75, 2));
    let mut s = LocalPlayerState::new(Some(b));
    s.on_vital_update(7, 100, 0, 0, 100);
    assert_eq!(s.get_max_approx(VitalKind::Health), Some(101));
}

// VitalMaximumAndVitaePenaltyRoundBeforePresentation (OpenAC v0.1.25):
// base 180, vitae 0.97, +23 from a vital spell.
#[test]
fn vital_maximum_and_vitae_penalty_round_before_presentation() {
    for (key, kind) in [
        (1, VitalKind::Health),
        (3, VitalKind::Stamina),
        (5, VitalKind::Mana),
    ] {
        let mut b = book(&[1, 2]);
        b.on_enchantment_added(vitae(1, 0.97));
        b.on_enchantment_added(typed(2, 2, SECOND_ATT, key, 23.0, 2));
        let mut s = LocalPlayerState::new(Some(b));
        s.on_vital_update(key, 180, 0, 0, 198);
        assert_eq!(s.get_max_approx(kind), Some(198), "{kind:?}");
        assert_eq!(s.get_vital_vitae_modifier(kind), -5, "{kind:?}");
        assert_eq!(s.get_base_max_approx(kind), Some(180), "{kind:?}");
    }
}

// VitalRoundingPreservesMinimumAndHalfUpBoundary (OpenAC v0.1.25):
// (base, vitae, maximum, vitae delta).
#[test]
fn vital_rounding_keeps_the_minimum_and_the_half_up_boundary() {
    for (base, multiplier, maximum, delta) in [
        (1, 0.1, 1, -1),
        (5, 0.6, 5, -2),
        (100, 0.975, 98, -2),
        (100, 0.974, 97, -3),
        (100, 1.0, 100, 0),
    ] {
        let mut b = book(&[1]);
        b.on_enchantment_added(vitae(1, multiplier));
        let mut s = LocalPlayerState::new(Some(b));
        s.on_vital_update(1, base, 0, 0, 0);
        assert_eq!(
            s.get_max_approx(VitalKind::Health),
            Some(maximum),
            "{base} x {multiplier}"
        );
        assert_eq!(
            s.get_vital_vitae_modifier(VitalKind::Health),
            delta,
            "{base} x {multiplier}"
        );
    }
}

#[test]
fn attribute_buffs_feed_vital_formula_exact_live_regression() {
    let mut b = book(&[1, 2]);
    b.on_enchantment_added(typed(1, 1, ATTRIBUTE, 2, 15.0, 2)); // Endurance
    b.on_enchantment_added(typed(2, 2, ATTRIBUTE, 6, 15.0, 2)); // Self
    let mut s = LocalPlayerState::new(Some(b));
    s.on_attribute_update(2, 0, 30, 0);
    s.on_attribute_update(6, 0, 10, 0);
    s.on_vital_update(7, 0, 15, 0, 38);
    s.on_vital_update(8, 0, 30, 0, 75);
    s.on_vital_update(9, 0, 0, 0, 25);

    assert_eq!(s.get_base_max_approx(VitalKind::Health), Some(30));
    assert_eq!(s.get_base_max_approx(VitalKind::Stamina), Some(60));
    assert_eq!(s.get_base_max_approx(VitalKind::Mana), Some(10));
    assert_eq!(s.get_max_approx(VitalKind::Health), Some(38));
    assert_eq!(s.get_max_approx(VitalKind::Stamina), Some(75));
    assert_eq!(s.get_max_approx(VitalKind::Mana), Some(25));
    assert_eq!(s.health_percent(), Some(1.0));
    assert_eq!(s.stamina_percent(), Some(1.0));
    assert_eq!(s.mana_percent(), Some(1.0));
}

#[test]
fn health_rounds_half_endurance_and_includes_gear() {
    let mut s = LocalPlayerState::default();
    s.on_int_properties(BTreeMap::from([(PropertyInt::GEAR_MAX_HEALTH.0, 7)]));
    s.on_attribute_update(2, 0, 45, 0);
    s.on_vital_update(7, 0, 10, 0, 40);
    assert_eq!(s.get_base_max_approx(VitalKind::Health), Some(40));
    assert_eq!(s.get_max_approx(VitalKind::Health), Some(40));
}

#[test]
fn health_adds_two_per_enlightenment_before_vitae() {
    let mut b = book(&[1]);
    b.on_enchantment_added(vitae(1, 0.95));
    let mut s = LocalPlayerState::new(Some(b));
    s.on_int_properties(BTreeMap::from([(PropertyInt::ENLIGHTENMENT.0, 4)]));
    s.on_vital_update(1, 100, 0, 0, 103);
    s.on_vital_update(3, 100, 0, 0, 95);
    // (100 + 4 x 2) x 0.95 = 102.6, shown as 103; stamina has no such term.
    assert_eq!(s.get_base_max_approx(VitalKind::Health), Some(108));
    assert_eq!(s.get_max_approx(VitalKind::Health), Some(103));
    assert_eq!(s.get_max_approx(VitalKind::Stamina), Some(95));
}

#[test]
fn gear_health_update_mid_session_moves_the_maximum() {
    let mut s = LocalPlayerState::default();
    s.on_int_properties(BTreeMap::new());
    s.on_vital_update(1, 100, 0, 0, 100);
    s.on_int_property_update(PropertyInt::GEAR_MAX_HEALTH.0, 12);
    assert_eq!(s.get_max_approx(VitalKind::Health), Some(112));
}

#[test]
fn vital_current_updates_only_current() {
    let mut s = LocalPlayerState::default();
    s.on_attribute_update(2, 50, 150, 0);
    s.on_vital_update(8, 0, 0, 50_000, 180);
    assert_eq!(s.on_vital_current(8, 90), Some(VitalKind::Stamina));
    let stamina = s.get(VitalKind::Stamina).expect("received");
    assert_eq!(stamina.ranks, 0);
    assert_eq!(stamina.start, 0);
    assert_eq!(stamina.xp, 50_000);
    assert_eq!(stamina.current, 90);
    assert!(close(s.stamina_percent().expect("known"), 0.45));
}

#[test]
fn vital_current_before_full_record_is_ignored() {
    let mut s = LocalPlayerState::default();
    assert_eq!(s.on_vital_current(4, 90), None);
    assert_eq!(s.get(VitalKind::Stamina), None);
    assert_eq!(s.stamina_percent(), None);
}

#[test]
fn attribute_update_names_derived_vitals() {
    let mut s = LocalPlayerState::default();
    let endurance = s.on_attribute_update(2, 10, 100, 500).expect("known id");
    assert_eq!(endurance.kind, AttributeKind::Endurance);
    assert_eq!(
        endurance.affected_vitals,
        [VitalKind::Health, VitalKind::Stamina]
    );
    let self_ = s.on_attribute_update(6, 5, 100, 250).expect("known id");
    assert_eq!(self_.affected_vitals, [VitalKind::Mana]);
    let quickness = s.on_attribute_update(3, 1, 100, 10).expect("known id");
    assert!(quickness.affected_vitals.is_empty());
    assert_eq!(s.on_attribute_update(7, 1, 1, 1), None);
}

#[test]
fn attribute_update_refreshes_percent() {
    let mut s = LocalPlayerState::default();
    s.on_vital_update(8, 0, 0, 0, 100);
    assert_eq!(s.stamina_percent(), None);
    s.on_attribute_update(2, 50, 150, 0);
    assert!(close(s.stamina_percent().expect("known"), 0.5));
}

#[test]
fn skill_update_stores_formula_adjusted_level() {
    let mut s = LocalPlayerState::default();
    s.on_skill_update(24, 12, 2, 3456, 30, 0, 1.5, 80);
    let run = s.get_skill(24).expect("received");
    assert_eq!(run.current_level(), 122);
    assert_eq!(run.status, 2);
    assert_eq!(run.xp, 3456);
}

#[test]
fn clear_returns_to_pre_login() {
    let mut s = LocalPlayerState::default();
    s.on_vital_update(7, 1, 2, 3, 4);
    s.on_attribute_update(1, 5, 6, 7);
    s.on_skill_update(8, 9, 2, 10, 11, 12, 13.0, 14);
    s.on_int_properties(BTreeMap::from([(1, 42)]));
    s.clear();
    assert_eq!(s.get(VitalKind::Health), None);
    assert_eq!(s.get_attribute(AttributeKind::Strength), None);
    assert!(s.skills().is_empty());
    assert!(s.int_properties().is_empty());
    s.clear();
    assert!(s.skills().is_empty());
}

#[test]
fn effective_attribute_without_spellbook_is_base() {
    let mut s = LocalPlayerState::default();
    s.on_attribute_update(1, 100, 100, 0);
    assert_eq!(
        s.get_effective_attribute(AttributeKind::Strength),
        Some(200)
    );
}

#[test]
fn effective_attribute_unseen_is_none() {
    let s = LocalPlayerState::new(Some(Spellbook::default()));
    assert_eq!(s.get_effective_attribute(AttributeKind::Strength), None);
}

#[test]
fn attributes_are_vitae_immune() {
    let mut b = book(&[1, 2]);
    b.on_enchantment_added(vitae(1, 0.67));
    let mut s = LocalPlayerState::new(Some(b));
    s.on_attribute_update(1, 100, 100, 0);
    assert_eq!(
        s.get_effective_attribute(AttributeKind::Strength),
        Some(200)
    );
}

#[test]
fn attribute_buff_multiplies_and_truncates() {
    let mut b = book(&[1, 2]);
    b.on_enchantment_added(typed(2, 1, ATTRIBUTE, 1, 1.1, 1));
    let mut s = LocalPlayerState::new(Some(b));
    s.on_attribute_update(1, 100, 100, 0);
    assert_eq!(
        s.get_effective_attribute(AttributeKind::Strength),
        Some(220)
    );
}

#[test]
fn all_attribute_enchantment_stacks_and_feeds_health_and_skills() {
    let mut b = book(&[1, 2]);
    b.on_enchantment_added(typed(1, 1, ATTRIBUTE, 1, 40.0, 2));
    // A blessing that raises every attribute.
    b.on_enchantment_added(typed(
        2,
        2,
        (EnchantmentTypeFlags::ATTRIBUTE | EnchantmentTypeFlags::MULTIPLE_STAT).0,
        0,
        9.0,
        2,
    ));
    let mut s = LocalPlayerState::new(Some(b));
    s.set_skill_formula_bonus_resolver(Some(Arc::new(|_, _, attrs: &BTreeMap<u32, u32>| {
        attrs[&2]
    })));
    s.on_attribute_update(1, 0, 300, 0);
    s.on_attribute_update(2, 0, 200, 0);
    s.on_vital_update(7, 0, 0, 0, 0);
    s.on_skill_update(24, 0, 2, 0, 0, 0, 0.0, 200);

    assert_eq!(
        s.get_effective_attribute(AttributeKind::Strength),
        Some(349)
    );
    assert_eq!(s.get_base_max_approx(VitalKind::Health), Some(100));
    assert_eq!(s.get_max_approx(VitalKind::Health), Some(105));
    assert_eq!(s.attribute_enchantment_skill_delta(24), 9);
}

#[test]
fn effective_skill_without_spellbook_is_base() {
    let mut s = LocalPlayerState::default();
    s.on_skill_update(6, 300, 2, 0, 3, 0, 0.0, 0);
    assert_eq!(s.get_effective_skill(6), Some(303));
    assert_eq!(s.get_skill_vitae_modifier(6), 0);
}

#[test]
fn effective_skill_unseen_is_none() {
    let s = LocalPlayerState::new(Some(Spellbook::default()));
    assert_eq!(s.get_effective_skill(6), None);
}

#[test]
fn thirty_three_percent_vitae_golden() {
    let mut b = book(&[1, 2]);
    b.on_enchantment_added(vitae(1, 0.67));
    let mut s = LocalPlayerState::new(Some(b));
    s.on_skill_update(6, 300, 2, 0, 3, 0, 0.0, 0);
    assert_eq!(s.get_effective_skill(6), Some(203));
    assert_eq!(s.get_skill_vitae_modifier(6), -100);
}

#[test]
fn buff_plus_vitae_composition() {
    let mut b = book(&[1, 2]);
    b.on_enchantment_added(vitae(1, 0.67));
    b.on_enchantment_added(typed(2, 2, SKILL, 6, 50.0, 2));
    let mut s = LocalPlayerState::new(Some(b));
    s.on_skill_update(6, 297, 2, 0, 3, 0, 0.0, 0);
    assert_eq!(s.get_effective_skill(6), Some(251));
    assert_eq!(s.get_skill_vitae_modifier(6), -99);
}

#[test]
fn attribute_buffs_raise_the_formula_term() {
    let mut b = book(&[1, 2, 3]);
    b.on_enchantment_added(typed(1, 1, ATTRIBUTE, 5, 45.0, 2)); // Focus
    b.on_enchantment_added(typed(2, 2, ATTRIBUTE, 6, 45.0, 2)); // Self
    b.on_enchantment_added(typed(3, 3, SKILL, 0x1F, 50.0, 2));
    let mut s = LocalPlayerState::new(Some(b));
    s.set_skill_formula_bonus_resolver(Some(Arc::new(|skill, _, attrs: &BTreeMap<u32, u32>| {
        if skill == 0x1F {
            (f64::from(attrs[&5] + attrs[&6]) / 4.0 + 0.5).floor() as u32
        } else {
            0
        }
    })));
    s.on_attribute_update(5, 0, 251, 0);
    s.on_attribute_update(6, 0, 251, 0);
    s.on_skill_update(0x1F, 200, 2, 0, 15, 0, 0.0, 126);

    let value = s.get_skill_value(0x1F, None).expect("received");
    assert_eq!(s.attribute_enchantment_skill_delta(0x1F), 22);
    assert_eq!(value.unenchanted_level, 341);
    assert_eq!(value.effective_level, 413);
    assert_eq!(s.get_effective_skill(0x1F), Some(413));
}

#[test]
fn attribute_delta_needs_resolver_and_buffs() {
    let mut s = LocalPlayerState::new(Some(book(&[1])));
    s.on_attribute_update(3, 0, 100, 0);
    s.on_skill_update(24, 50, 2, 0, 0, 0, 0.0, 100);
    assert_eq!(s.attribute_enchantment_skill_delta(24), 0);

    s.set_skill_formula_bonus_resolver(Some(Arc::new(|_, _, attrs: &BTreeMap<u32, u32>| {
        attrs[&3]
    })));
    assert_eq!(s.attribute_enchantment_skill_delta(24), 0);
    assert_eq!(s.get_effective_skill(24), Some(150));

    s.spellbook_mut()
        .expect("spellbook")
        .on_enchantment_added(typed(1, 1, ATTRIBUTE, 3, 20.0, 2));
    assert_eq!(s.attribute_enchantment_skill_delta(24), 20);
    assert_eq!(s.get_effective_skill(24), Some(170));
    assert_eq!(
        s.get_skill_value(24, None)
            .expect("received")
            .unenchanted_level,
        150
    );
}

#[test]
fn wire_update_keeps_the_login_formula_bonus() {
    let mut s = LocalPlayerState::default();
    s.on_skill_update(6, 10, 2, 100, 0, 0, 0.0, 120);
    s.on_skill_wire_update(6, 11, 2, 2000, 0, 0, 5.0);
    let snap = s.skills()[&6];
    assert_eq!(snap.ranks, 11);
    assert_eq!(snap.xp, 2000);
    assert_eq!(snap.formula_bonus, 120);
    assert_eq!(snap.base_level(), 131);
}

#[test]
fn freshly_trained_skill_derives_its_term_through_the_resolver() {
    let mut s = LocalPlayerState::default();
    s.set_skill_formula_bonus_resolver(Some(Arc::new(
        |skill, _, attrs: &BTreeMap<u32, u32>| match (skill, attrs.get(&4)) {
            (33, Some(coordination)) => coordination / 4,
            _ => 0,
        },
    )));
    s.on_attribute_update(4, 0, 80, 0);
    s.on_skill_wire_update(33, 0, 2, 0, 0, 0, 0.0);
    assert_eq!(s.skills()[&33].formula_bonus, 20);
}

#[test]
fn movement_skill_totals() {
    let mut s = LocalPlayerState::default();
    assert_eq!(s.movement_skill_totals(), (-1, -1));
    s.on_skill_update(SKILL_RUN, 10, 2, 0, 5, 0, 0.0, 100);
    s.on_skill_update(SKILL_JUMP, 1, 2, 0, 2, 0, 0.0, 50);
    assert_eq!(s.movement_skill_totals(), (115, 53));
}

// SkillLevelConformanceTests: a maxed character, both attributes behind
// the skill at 315 so its formula term is 315.

const SKILL_ID: u32 = 24;
const TRAINED: u32 = 2;
const SPECIALIZED: u32 = 3;

fn maxed(spellbook: Option<Spellbook>) -> LocalPlayerState {
    let mut s = LocalPlayerState::new(spellbook);
    s.set_skill_formula_bonus_resolver(Some(Arc::new(
        |skill, _, _: &BTreeMap<u32, u32>| {
            if skill == SKILL_ID { 315 } else { 0 }
        },
    )));
    s.on_attribute_update(1, 215, 100, 0);
    s.on_attribute_update(4, 215, 100, 0);
    s
}

fn train(s: &mut LocalPlayerState, ranks: u32, init: u32, status: u32) {
    s.on_skill_wire_update(SKILL_ID, ranks, status, 0, init, 0, 0.0);
}

fn all_skills_five() -> AugmentationBonuses {
    AugmentationBonuses {
        all_skills: 5,
        ..AugmentationBonuses::default()
    }
}

#[test]
fn trained_at_the_ceiling() {
    let mut s = maxed(None);
    train(&mut s, 208, 0, TRAINED);
    let value = s.get_skill_value(SKILL_ID, None).expect("received");
    assert_eq!(value.unenchanted_level, 523);
    assert_eq!(value.effective_level, 523);
}

#[test]
fn specialized_carries_its_starting_value() {
    let mut s = maxed(None);
    train(&mut s, 226, 10, SPECIALIZED);
    let value = s.get_skill_value(SKILL_ID, None).expect("received");
    assert_eq!(value.unenchanted_level, 551);
    assert_eq!(value.effective_level, 551);
}

#[test]
fn all_skills_augmentation_adds_five_first() {
    for (ranks, init, status, expected) in [(208, 0, TRAINED, 528), (226, 10, SPECIALIZED, 556)] {
        let mut s = maxed(None);
        train(&mut s, ranks, init, status);
        let value = s
            .get_skill_value(SKILL_ID, Some(all_skills_five()))
            .expect("received");
        assert_eq!(value.unenchanted_level, expected);
        assert_eq!(value.effective_level, expected);
    }
}

#[test]
fn spells_scale_first_and_add_second() {
    let mut b = book(&[1, 2]);
    b.on_enchantment_added(typed(1, 1, SKILL, SKILL_ID, 1.15, 1));
    b.on_enchantment_added(typed(2, 2, SKILL, SKILL_ID, 20.0, 2));
    let mut s = maxed(Some(b));
    train(&mut s, 208, 0, TRAINED);
    let value = s.get_skill_value(SKILL_ID, None).expect("received");
    // 523 x 1.15 = 601.45, + 20 = 621.45.
    assert_eq!(value.unenchanted_level, 523);
    assert_eq!(value.effective_level, 621);
}

#[test]
fn vitae_scales_the_augmented_skill() {
    let mut b = book(&[1]);
    b.on_enchantment_added(ActiveEnchantmentRecord {
        stat_mod_type: Some(MULTIPLICATIVE),
        stat_mod_value: Some(0.95),
        bucket: 4,
        ..ActiveEnchantmentRecord::new(1, 1, 0.0, 0)
    });
    let mut s = maxed(Some(b));
    train(&mut s, 208, 0, TRAINED);
    let value = s
        .get_skill_value(SKILL_ID, Some(all_skills_five()))
        .expect("received");
    // 523 + 5 = 528, and 528 x 0.95 = 501.6.
    assert_eq!(value.unenchanted_level, 528);
    assert_eq!(value.effective_level, 501);
    assert_eq!(value.vitae_modifier, -27);
}

#[test]
fn augmentations_read_from_int_properties() {
    let mut s = maxed(None);
    s.on_int_property_update(PropertyInt::LUM_AUG_ALL_SKILLS.0, 5);
    train(&mut s, 208, 0, TRAINED);
    assert_eq!(s.get_effective_skill(SKILL_ID), Some(528));
}

// Stacking as the game client stacks for its attribute, vital and skill
// panels: the records that apply to the stat are culled from the
// multiplicative list, then the additive list, each walked from its head
// (the newest record), into one list holding one record per spell
// category. Test cases adapted from PR #36 by evantaylor.

const STRENGTH: u32 = 1;
const MELEE_DEFENSE: u32 = 6;

/// `typed` in spell category `category` at `power`, started at `start`.
fn ranked(
    record: ActiveEnchantmentRecord,
    category: u32,
    power: u32,
    start: f64,
) -> ActiveEnchantmentRecord {
    ActiveEnchantmentRecord {
        spell_category: category,
        power_level: power,
        start_time: start,
        ..record
    }
}

/// A spellbook holding `records` (each arriving in turn), whose spell table
/// knows every spell, with the record's own category as its family.
fn book_holding(records: &[ActiveEnchantmentRecord]) -> Spellbook {
    let table = SpellTable::create(
        records
            .iter()
            .map(|r| SpellMetadata::new(r.spell_id, "Test", r.spell_category)),
    )
    .expect("unique");
    book_with_table(table, records)
}

fn book_with_table(table: SpellTable, records: &[ActiveEnchantmentRecord]) -> Spellbook {
    let mut b = Spellbook::new(Some(Arc::new(table)));
    for &record in records {
        b.on_enchantment_added(record);
    }
    b
}

fn strength_in(b: Spellbook) -> Option<i32> {
    let mut s = LocalPlayerState::new(Some(b));
    s.on_attribute_update(STRENGTH, 100, 100, 0);
    s.get_effective_attribute(AttributeKind::Strength)
}

fn strength_with(records: &[ActiveEnchantmentRecord]) -> Option<i32> {
    strength_in(book_holding(records))
}

fn skill_with(skill_id: u32, base: u32, records: &[ActiveEnchantmentRecord]) -> Option<i32> {
    let mut s = LocalPlayerState::new(Some(book_holding(records)));
    s.on_skill_update(skill_id, base, 2, 0, 0, 0, 0.0, 0);
    s.get_effective_skill(skill_id)
}

#[test]
fn a_spell_missing_from_the_spell_table_counts() {
    let record = typed(9999, 1, ATTRIBUTE, STRENGTH, 40.0, 2);
    assert_eq!(
        strength_in(book_with_table(SpellTable::empty(), &[record])),
        Some(240)
    );
}

#[test]
fn records_group_by_their_own_spell_category() {
    // Both records name category 7; the table's family (0 here, which
    // would keep them apart) plays no part. The stronger one counts alone.
    let weak = ranked(typed(1, 1, ATTRIBUTE, STRENGTH, 40.0, 2), 7, 400, 0.0);
    let strong = ranked(typed(2, 2, ATTRIBUTE, STRENGTH, 56.0, 2), 7, 16300, 0.0);
    let table =
        || SpellTable::create([1, 2].map(|id| SpellMetadata::new(id, "Test", 0))).expect("unique");
    assert_eq!(
        strength_in(book_with_table(table(), &[weak, strong])),
        Some(256)
    );
    assert_eq!(
        strength_in(book_with_table(table(), &[strong, weak])),
        Some(256)
    );
}

#[test]
fn spell_category_zero_is_a_category_like_any_other() {
    let first = ranked(typed(1, 1, ATTRIBUTE, STRENGTH, 40.0, 2), 0, 1, 0.0);
    let second = ranked(typed(2, 2, ATTRIBUTE, STRENGTH, 30.0, 2), 0, 2, 0.0);
    assert_eq!(strength_with(&[first, second]), Some(230));
}

#[test]
fn a_higher_power_level_beats_a_higher_spell_id() {
    // PR #36's Melee Defense: a +56 timed buff of high power against a +45
    // permanent one of a higher spell id, same category.
    let timed = ranked(
        typed(2245, 2, SKILL, MELEE_DEFENSE, 56.0, 2),
        37,
        16300,
        0.0,
    );
    let permanent = ranked(typed(4560, 1, SKILL, MELEE_DEFENSE, 45.0, 2), 37, 400, 0.0);
    assert_eq!(
        skill_with(MELEE_DEFENSE, 574, &[timed, permanent]),
        Some(630)
    );
    assert_eq!(
        skill_with(MELEE_DEFENSE, 574, &[permanent, timed]),
        Some(630)
    );
}

#[test]
fn at_equal_power_the_later_start_wins() {
    let earlier = ranked(typed(4560, 1, SKILL, MELEE_DEFENSE, 45.0, 2), 37, 400, 10.0);
    let later = ranked(typed(2245, 2, SKILL, MELEE_DEFENSE, 56.0, 2), 37, 400, 20.0);
    assert_eq!(skill_with(MELEE_DEFENSE, 574, &[earlier, later]), Some(630));
    assert_eq!(skill_with(MELEE_DEFENSE, 574, &[later, earlier]), Some(630));
}

#[test]
fn an_exact_tie_goes_to_the_older_record() {
    // A new record goes to the head of its list and the head is culled
    // first, so the record that arrived first is the later challenger and
    // takes an exact tie. No spell-id tiebreak.
    let older = ranked(typed(1, 1, ATTRIBUTE, STRENGTH, 40.0, 2), 7, 400, 5.0);
    let newer = ranked(typed(2, 2, ATTRIBUTE, STRENGTH, 56.0, 2), 7, 400, 5.0);
    assert_eq!(strength_with(&[older, newer]), Some(240));
    assert_eq!(strength_with(&[newer, older]), Some(256));
}

#[test]
fn a_multiplier_and_an_addition_of_one_category_compete() {
    let scale = ranked(typed(1, 1, ATTRIBUTE, STRENGTH, 1.1, 1), 5, 10, 0.0);
    let add = ranked(typed(2, 2, ATTRIBUTE, STRENGTH, 10.0, 2), 5, 5, 0.0);
    assert_eq!(strength_with(&[scale, add]), Some(220));
    let stronger_add = ranked(add, 5, 20, 0.0);
    assert_eq!(strength_with(&[scale, stronger_add]), Some(210));
}

#[test]
fn a_multiple_stat_record_applies_whatever_its_key() {
    let blessing = typed(
        1,
        1,
        (EnchantmentTypeFlags::ATTRIBUTE | EnchantmentTypeFlags::MULTIPLE_STAT).0,
        3,
        9.0,
        2,
    );
    assert_eq!(strength_with(&[blessing]), Some(209));
}

#[test]
fn an_all_attack_skills_record_raises_the_attack_skills_only() {
    let attack = typed(1, 1, SKILL | 0x1_0000, 0, 10.0, 2);
    for skill in [33, 34, 41, 43, 44, 45, 46, 47, 49] {
        assert_eq!(
            skill_with(skill, 100, &[attack]),
            Some(110),
            "skill {skill}"
        );
    }
    for skill in [1, 6, 24, 35, 36, 42, 48, 50] {
        assert_eq!(
            skill_with(skill, 100, &[attack]),
            Some(100),
            "skill {skill}"
        );
    }
}

#[test]
fn an_all_defense_skills_record_raises_the_defense_skills_only() {
    let defense = typed(1, 1, SKILL | 0x2_0000, 0, 10.0, 2);
    for skill in [6, 7, 15, 48] {
        assert_eq!(
            skill_with(skill, 100, &[defense]),
            Some(110),
            "skill {skill}"
        );
    }
    for skill in [5, 8, 14, 16, 24, 45, 47, 49] {
        assert_eq!(
            skill_with(skill, 100, &[defense]),
            Some(100),
            "skill {skill}"
        );
    }
}

#[test]
fn the_type_bits_decide_add_or_multiply_with_add_first() {
    // A record carrying both operation bits adds, whichever list holds it.
    let both = ActiveEnchantmentRecord {
        stat_mod_type: Some(ATTRIBUTE | MULTIPLICATIVE | ADDITIVE),
        ..typed(1, 1, ATTRIBUTE, STRENGTH, 10.0, 1)
    };
    assert_eq!(strength_with(&[both]), Some(210));
    // One with neither bit changes nothing.
    let neither = ActiveEnchantmentRecord {
        stat_mod_type: Some(ATTRIBUTE),
        ..typed(2, 2, ATTRIBUTE, STRENGTH, 10.0, 2)
    };
    assert_eq!(strength_with(&[neither]), Some(200));
}

#[test]
fn vitae_then_each_record_rounds_to_a_float_in_turn() {
    // 200 x 0.95 = 190, x 1.1 = 209 exactly in single precision; scaling
    // by the product 0.95 x 1.1 first gives 208.99998, shown as 208.
    let b = book_holding(&[vitae(1, 0.95), typed(2, 2, SKILL, MELEE_DEFENSE, 1.1, 1)]);
    let mut s = LocalPlayerState::new(Some(b));
    s.on_skill_update(MELEE_DEFENSE, 200, 2, 0, 0, 0, 0.0, 0);
    assert_eq!(s.get_effective_skill(MELEE_DEFENSE), Some(209));
}
