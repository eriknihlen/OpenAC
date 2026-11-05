//! The cast request (OpenAC `tests/AcDream.Runtime.Tests/Gameplay/
//! RuntimeSpellCastStateTests.cs`), the spellbook fed by the description and
//! the magic events (`tests/AcDream.Core.Net.Tests/GameEventWiringTests.cs`
//! magic parts), and the player's numbers reading enchanted.

use std::collections::{BTreeMap, HashMap};
use std::sync::Arc;

use ac_core::player::{
    AttributeKind, LiveSkillCreditResolver, SkillBase, SkillFormula, SkillFormulaBonusResolver,
};
use ac_core::spells::{SpellFlags, SpellTable};
use ac_proto::{AttributeEntry, Inbound, PlayerDescription, SkillEntry};

use super::*;
use crate::world::tests::empty_description;

const PLAYER: u32 = 0x5000_0001;

// -- The cast request -------------------------------------------------------

#[derive(Default)]
struct FakeOps {
    local_player_id: u32,
    cannot_send: bool,
    missing_components: bool,
    untargeted_sends: Vec<u32>,
    targeted_sends: Vec<(u32, u32)>,
    busy: u32,
    last_message: String,
    order: Vec<&'static str>,
}

impl SpellCastOperations for FakeOps {
    fn local_player_id(&self) -> u32 {
        self.local_player_id
    }
    fn can_send(&self) -> bool {
        !self.cannot_send
    }
    fn has_required_components(&self, _: u32) -> bool {
        !self.missing_components
    }
    fn is_target_compatible(&mut self, _: u32, _: &SpellMetadata, _: bool) -> bool {
        true
    }
    fn stop_completely(&mut self) {
        self.order.push("stop");
    }
    fn send_untargeted(&mut self, spell_id: u32) {
        self.order.push("send");
        self.untargeted_sends.push(spell_id);
    }
    fn send_targeted(&mut self, target_id: u32, spell_id: u32) {
        self.order.push("send");
        self.targeted_sends.push((target_id, spell_id));
    }
    fn display_message(&mut self, message: &str) {
        message.clone_into(&mut self.last_message);
    }
    fn increment_busy(&mut self) {
        self.order.push("busy");
        self.busy += 1;
    }
}

/// A spellbook knowing spell 1 with these flags, untargeted bit and mask.
fn book(flags: u32, untargeted: bool, target_mask: u32) -> Spellbook {
    let spell = SpellMetadata {
        flags: SpellFlags(flags),
        is_untargeted: untargeted,
        target_mask,
        ..SpellMetadata::new(1, "Test", 0)
    };
    let mut book = Spellbook::new(Some(Arc::new(SpellTable::create([spell]).unwrap())));
    book.on_spell_learned(1, 0.0);
    book
}

fn ops() -> FakeOps {
    FakeOps {
        local_player_id: 42,
        ..FakeOps::default()
    }
}

#[test]
fn a_self_spell_goes_at_the_player() {
    let book = book(0x8, true, 0x10);
    let mut ops = ops();
    let mut cast = SpellCast::default();
    assert_eq!(
        cast.cast(&book, 1, Some(99), &mut ops),
        CastRequestResult::Sent
    );
    assert_eq!(ops.targeted_sends, [(42, 1)]);
}

#[test]
fn a_targeted_spell_without_a_selection_sends_nothing() {
    let book = book(0, false, 0x10);
    let mut ops = ops();
    let mut cast = SpellCast::default();
    assert_eq!(
        cast.cast(&book, 1, None, &mut ops),
        CastRequestResult::NoTarget
    );
    assert!(ops.targeted_sends.is_empty());
    assert!(ops.last_message.to_lowercase().contains("select"));
}

#[test]
fn an_untargeted_cast_stops_then_sends_then_counts_busy() {
    let book = book(0, true, 0);
    let mut ops = ops();
    let mut cast = SpellCast::default();
    assert_eq!(cast.cast(&book, 1, None, &mut ops), CastRequestResult::Sent);
    assert_eq!(ops.order, ["stop", "send", "busy"]);
}

#[test]
fn missing_components_send_nothing_and_leave_the_gate_alone() {
    let book = book(0x8, true, 0x10);
    let mut stocked = ops();
    let mut empty = FakeOps {
        missing_components: true,
        ..ops()
    };
    let mut cast = SpellCast::default();
    assert_eq!(
        cast.cast(&book, 1, None, &mut empty),
        CastRequestResult::MissingComponents
    );
    assert!(empty.untargeted_sends.is_empty() && empty.targeted_sends.is_empty());
    assert_eq!(empty.busy, 0);
    assert_eq!(
        SpellCast::evaluate_gate(&book, 1, None, &mut stocked),
        SpellCast::evaluate_gate(&book, 1, None, &mut empty)
    );
}

#[test]
fn outside_the_world_nothing_counts_busy() {
    let book = book(0, true, 0);
    let mut ops = FakeOps {
        cannot_send: true,
        ..ops()
    };
    let mut cast = SpellCast::default();
    assert_eq!(
        cast.cast(&book, 1, None, &mut ops),
        CastRequestResult::Unavailable
    );
    assert_eq!(ops.busy, 0);
    assert_eq!(cast.last_requested_spell(), None);
}

#[test]
fn the_use_done_answers_the_cast_in_flight_once() {
    let book = book(0, false, 0x10);
    let mut ops = ops();
    let mut cast = SpellCast::default();
    assert_eq!(
        cast.cast(&book, 1, Some(99), &mut ops),
        CastRequestResult::Sent
    );
    assert_eq!(cast.pending_spell(), Some(1));
    assert!(cast.complete_use(0x0402));
    assert_eq!(
        cast.last_completion(),
        SpellCastCompletion {
            revision: 1,
            spell_id: 1,
            target_id: 99,
            weenie_error: 0x0402,
        }
    );
    assert_eq!(cast.pending_spell(), None);
    assert!(!cast.complete_use(0));
    assert_eq!(cast.last_completion().revision, 1);
}

// OpenAC `Cast_ReissuesWhileAReceiptIsPending`: a cast asked for again
// while the first still waits for its use-done goes out, without a refusal,
// and the two answers are both the same spell's.
#[test]
fn a_cast_is_reissued_while_a_receipt_is_pending() {
    let book = book(0, false, 0x10);
    let mut ops = ops();
    let mut cast = SpellCast::default();
    assert_eq!(
        cast.cast(&book, 1, Some(99), &mut ops),
        CastRequestResult::Sent
    );
    assert_eq!(
        cast.cast(&book, 1, Some(99), &mut ops),
        CastRequestResult::Sent
    );
    assert_eq!(ops.targeted_sends.len(), 2);
    assert_eq!(ops.busy, 2);
    assert_eq!(ops.last_message, "");
    assert_eq!(cast.pending_spell(), Some(1));
    assert_eq!(cast.pending_target(), Some(99));
    assert!(cast.complete_use(0));
    assert_eq!(cast.pending_spell(), Some(1));
    assert!(cast.complete_use(0));
    assert_eq!(cast.pending_spell(), None);
}

// OpenAC `Cast_DifferentOutstandingSpellsDoNotClaimAnUnidentifiedReceipt`:
// with two different spells out, neither answer is claimed; once those are
// in, a lone cast is answered again.
#[test]
fn different_outstanding_spells_do_not_claim_an_unidentified_receipt() {
    let spells = [1, 2].map(|id| SpellMetadata {
        is_untargeted: true,
        ..SpellMetadata::new(id, if id == 1 { "Test" } else { "Other" }, 0)
    });
    let mut book = Spellbook::new(Some(Arc::new(SpellTable::create(spells).unwrap())));
    book.on_spell_learned(1, 0.0);
    book.on_spell_learned(2, 0.0);
    let mut ops = ops();
    let mut cast = SpellCast::default();

    assert_eq!(cast.cast(&book, 1, None, &mut ops), CastRequestResult::Sent);
    assert_eq!(cast.cast(&book, 2, None, &mut ops), CastRequestResult::Sent);
    assert_eq!(cast.pending_spell(), None);
    assert!(!cast.complete_use(0));
    assert!(!cast.complete_use(0));
    assert_eq!(cast.last_completion(), SpellCastCompletion::default());

    assert_eq!(cast.cast(&book, 2, None, &mut ops), CastRequestResult::Sent);
    assert!(cast.complete_use(0));
    assert_eq!(cast.last_completion().spell_id, 2);
}

// OpenAC `FailedReissuePreservesTheEarlierOutstandingCast` has no
// counterpart: it pins the rollback after a send that throws, and a send
// here cannot fail once the request is past the in-world check.

#[test]
fn a_spell_the_book_does_not_hold_is_refused() {
    let book = book(0, true, 0);
    let mut ops = ops();
    let mut cast = SpellCast::default();
    assert_eq!(
        cast.cast(&book, 2670, None, &mut ops),
        CastRequestResult::UnknownSpell
    );
    assert!(ops.untargeted_sends.is_empty() && ops.targeted_sends.is_empty());
    assert_eq!(ops.busy, 0);
    assert_eq!(ops.last_message, "You do not know that spell.");
}

// -- The spellbook from the wire -----------------------------------------------

fn event(event_type: GameEventType, payload: &[u8]) -> Vec<u8> {
    let mut body = Vec::new();
    for word in [ac_proto::Opcode::GameEvent.value(), PLAYER, 1, event_type.0] {
        body.extend_from_slice(&word.to_le_bytes());
    }
    body.extend_from_slice(payload);
    body
}

/// GameEventWiringTests.BuildEnchantment: a 60-byte enchantment record.
#[allow(clippy::too_many_arguments)]
fn enchantment(
    spell: u16,
    layer: u16,
    duration: f64,
    start: f64,
    degraded: f64,
    stat_mod_type: u32,
    key: u32,
    value: f32,
) -> Vec<u8> {
    let mut w = ac_bits::Writer::new();
    w.u16(spell);
    w.u16(layer);
    w.u16(3);
    w.u16(0);
    w.u32(8);
    w.f64(start);
    w.f64(duration);
    w.u32(0xBEEF);
    w.f32(0.1);
    w.f32(-1.0);
    w.f64(degraded);
    w.u32(stat_mod_type);
    w.u32(key);
    w.f32(value);
    w.into_vec()
}

fn layered(spell: u16, layer: u16) -> Vec<u8> {
    let mut w = ac_bits::Writer::new();
    w.u16(spell);
    w.u16(layer);
    w.into_vec()
}

/// A world whose player is described, with the spell table
/// (GameEventWiringTests.SpellbookWithNames: 1234 Fire Protection Self, 666
/// Vitae; and its TestSpell(42), with 2 for Strength Self I). Enchantments
/// only count for spells the table holds.
fn described_world() -> World {
    let table = SpellTable::create([
        SpellMetadata::new(1234, "Fire Protection Self", 0),
        SpellMetadata::new(666, "Vitae", 0),
        SpellMetadata::new(42, "Test", 0),
        SpellMetadata::new(2, "Strength Self I", 1),
    ])
    .unwrap();
    let mut world = World::new();
    world.set_player(PLAYER);
    world.install_player_rules(Arc::new(table), None);
    world.apply_player_description(&empty_description());
    world
}

fn apply(world: &mut World, bytes: &[u8], received_at: f64) -> Vec<String> {
    let Inbound::GameEvent(event) = Inbound::parse(bytes).unwrap() else {
        panic!("not a game event");
    };
    apply_event(world, &event, received_at).unwrap()
}

#[test]
fn an_enchantment_moves_onto_the_session_clock_and_finds_its_bucket() {
    // WireAll_MagicUpdateEnchantment_ConvertsRelativeTimesAndClassifiesBucket.
    let mut world = described_world();
    let bytes = event(
        GameEventType::MAGIC_UPDATE_ENCHANTMENT,
        &enchantment(42, 3, 60.0, 10.0, 2.0, 0x0000_8000, 7, 1.25),
    );
    apply(&mut world, &bytes, 100.0);
    let book = world.spellbook().unwrap();
    let [record] = book.active_enchantments() else {
        panic!("one record");
    };
    assert_eq!(
        (
            record.start_time,
            record.last_time_degraded,
            record.bucket,
            record.duration
        ),
        (110.0, 102.0, 2, 60.0)
    );
}

#[test]
fn a_skill_enchantment_reads_into_the_skill_at_once() {
    // WireAll_MagicUpdateEnchantment_PropagatesFullStatModMidSession: an
    // additive skill enchantment of 25 on a 100-rank skill reads 125.
    let mut world = described_world();
    let mut description = empty_description();
    description.skills.push(SkillEntry {
        skill: 7,
        ranks: 100,
        adjust_pp: 1,
        advancement_class: 2,
        xp: 0,
        init: 0,
        resistance: 0,
        last_used: 0.0,
    });
    world.apply_player_description(&description);
    let bytes = event(
        GameEventType::MAGIC_UPDATE_ENCHANTMENT,
        &enchantment(42, 3, 60.0, 10.0, 2.0, 0x0000_8010, 7, 25.0),
    );
    apply(&mut world, &bytes, 100.0);
    let numbers = world.player().unwrap().numbers();
    assert_eq!(numbers.get_effective_skill(7), Some(125));
}

#[test]
fn a_purge_removes_the_timed_enchantments() {
    // WireAll_MagicPurgeEnchantments_CallsOnPurgeAll.
    let mut world = described_world();
    for (spell, mod_type) in [(1u16, 0x0000_4000u32), (2, 0x0000_8000)] {
        let bytes = event(
            GameEventType::MAGIC_UPDATE_ENCHANTMENT,
            &enchantment(spell, spell, 100.0, 0.0, 0.0, mod_type, 1, 1.0),
        );
        apply(&mut world, &bytes, 0.0);
    }
    assert_eq!(world.spellbook().unwrap().active_count(), 2);
    apply(
        &mut world,
        &event(GameEventType::MAGIC_PURGE_ENCHANTMENTS, &[]),
        0.0,
    );
    assert_eq!(world.spellbook().unwrap().active_count(), 0);
}

#[test]
fn an_expired_enchantment_is_announced_and_a_dispelled_one_is_not() {
    // AnExpiredEnchantment_AnnouncesItsExpiryAtTheRetailTextType,
    // ADispelledEnchantment_LeavesQuietly, ExpiredVitaeReadsAsAPenalty,
    // AnExpiredSpellMissingFromTheTablePrintsNothing.
    let mut world = described_world();
    let removed = |spell| event(GameEventType::MAGIC_REMOVE_ENCHANTMENT, &layered(spell, 1));
    assert_eq!(
        apply(&mut world, &removed(1234), 0.0),
        ["Fire Protection Self has expired."]
    );
    assert_eq!(EXPIRY_TEXT_TYPE, LogTextType::Magic);
    let dispelled = event(GameEventType::MAGIC_DISPEL_ENCHANTMENT, &layered(1234, 1));
    assert!(apply(&mut world, &dispelled, 0.0).is_empty());
    assert_eq!(
        apply(&mut world, &removed(0x29A), 0.0),
        ["Vitae penalty has expired."]
    );
    assert!(apply(&mut world, &removed(4321), 0.0).is_empty());
}

#[test]
fn learned_and_forgotten_spells_reach_the_spellbook() {
    // WireAll_MagicUpdateSpell_RoutesToSpellbook.
    let mut world = described_world();
    apply(
        &mut world,
        &event(GameEventType::MAGIC_UPDATE_SPELL, &0x3E1u32.to_le_bytes()),
        0.0,
    );
    assert!(world.spellbook().unwrap().knows(0x3E1));
    apply(
        &mut world,
        &event(GameEventType::MAGIC_REMOVE_SPELL, &0x3E1u32.to_le_bytes()),
        0.0,
    );
    assert!(!world.spellbook().unwrap().knows(0x3E1));
}

#[test]
fn the_description_fills_the_spellbook() {
    // WireAll_PlayerDescription_FeedsSpellbook, and the enchantments of the
    // description move onto the clock it arrived at.
    let mut world = described_world();
    let mut description = empty_description();
    description.spells = BTreeMap::from([(0x3E1, 2.0), (0x3E2, 2.0)]);
    let bytes = enchantment(1234, 1, 1800.0, -5.0, 0.0, 0x0000_8001, 1, 10.0);
    let mut r = ac_bits::Reader::new(&bytes);
    description.enchantments.push(ac_proto::PlayerEnchantment {
        bucket: ac_proto::EnchantmentBucket::Additive,
        enchantment: ac_proto::Enchantment::read(&mut r).unwrap(),
    });
    description.hotbar_spells = vec![vec![0x3E2, 0x3E1]];
    world.apply_player_description(&description);
    apply_description(&mut world, &description, 50.0);
    let book = world.spellbook().unwrap();
    assert!(book.knows(0x3E1) && book.knows(0x3E2));
    assert_eq!(book.get_favorites(0), [0x3E2, 0x3E1]);
    assert_eq!(book.active_enchantments()[0].start_time, 45.0);
    let views = enchantments_view(&world, 50.0);
    assert_eq!(views[0].name, "Fire Protection Self");
    assert_eq!(views[0].remaining_seconds, Some(1795.0));
}

#[test]
fn an_attribute_buff_reads_on_the_character_sheet() {
    // With the spellbook installed the player's numbers read enchanted
    // (Strength Self I adds 10, as seen live).
    let mut world = described_world();
    let mut description = empty_description();
    description.attributes.push(AttributeEntry {
        id: 1,
        ranks: 90,
        start: 10,
        xp: 0,
        current: None,
    });
    world.apply_player_description(&description);
    let bytes = event(
        GameEventType::MAGIC_UPDATE_ENCHANTMENT,
        &enchantment(2, 1, 1800.0, 0.0, 0.0, 0x0000_8001, 1, 10.0),
    );
    apply(&mut world, &bytes, 0.0);
    let numbers = world.player().unwrap().numbers();
    assert_eq!(
        numbers.get_effective_attribute(AttributeKind::Strength),
        Some(110)
    );
    let sheet = character_sheet(&world);
    assert_eq!(
        (sheet.attributes[0].base, sheet.attributes[0].current),
        (100, 110)
    );
}

#[test]
fn the_description_carries_the_attribute_term_into_the_skill() {
    // OpenAC `tests/AcDream.Headless.Tests/HeadlessSkillFormulaBindingTests.cs`
    // APlayerDescriptionCarriesTheAttributeTermIntoTheSkillLevel: Strength
    // and Coordination 315, Run (Strength + Coordination) / 2 trained with
    // 208 ranks: term 315, base level 523.
    let skills = HashMap::from([(
        24,
        SkillBase {
            min_level: 1,
            formula: SkillFormula {
                additive_bonus: 0,
                attribute1_multiplier: 1,
                attribute2_multiplier: 1,
                divisor: 2,
                attribute1: 1,
                attribute2: 4,
            },
            ..SkillBase::default()
        },
    )]);
    let resolver = LiveSkillCreditResolver::new(skills);
    let resolver: SkillFormulaBonusResolver =
        Arc::new(move |skill, class, attributes| resolver.resolve(skill, class, attributes));
    let mut world = World::new();
    world.set_player(PLAYER);
    world.install_player_rules(Arc::new(SpellTable::empty()), Some(resolver));
    let description = PlayerDescription {
        attributes: [1, 4]
            .map(|id| AttributeEntry {
                id,
                ranks: 215,
                start: 100,
                xp: 0,
                current: None,
            })
            .to_vec(),
        skills: vec![SkillEntry {
            skill: 24,
            ranks: 208,
            adjust_pp: 1,
            advancement_class: 2,
            xp: 0,
            init: 0,
            resistance: 0,
            last_used: 0.0,
        }],
        ..empty_description()
    };
    world.apply_player_description(&description);
    let run = world.player().unwrap().numbers().get_skill(24).unwrap();
    assert_eq!((run.formula_bonus, run.base_level()), (315, 523));
}

#[test]
fn a_spell_is_named_by_id_or_by_its_name() {
    let mut world = described_world();
    let mut description = empty_description();
    description.spells = BTreeMap::from([(1234, 0.0), (666, 0.0)]);
    apply_description(&mut world, &description, 0.0);
    assert_eq!(resolve_spell(&world, "1234"), Some(1234));
    assert_eq!(resolve_spell(&world, "0x29A"), Some(0x29A));
    assert_eq!(resolve_spell(&world, "fire protection self"), Some(1234));
    assert_eq!(resolve_spell(&world, "Vit"), Some(666));
    assert_eq!(resolve_spell(&world, "Nothing"), None);
}

// -- The examination window's component list ---------------------------------
//
// Golden values from OpenAC
// `tests/AcDream.Core.Tests/Spells/SpellComponentRequirementServiceTests.cs`
// (`InfusedSchoolOrDirectlyCarriedFocus_UsesScarabOnlyFormula`: a War I
// spell carried with its school's focus is scarab 1 plus one prismatic taper
// 0xBC; `FocusNestedInsidePack_DoesNotCountAsRetailMagicPackOwnership`), as
// `MagicRuntime.GetExamineComponents` lists them.

const WAR_FOCUS_CLASS: u32 = 900;

fn examine_table(focus_container: Option<u32>) -> ac_core::items::ClientObjectTable {
    let mut objects = ac_core::items::ClientObjectTable::new();
    let mut ev = Vec::new();
    let mut put = |id: u32, class: u32, container: u32| {
        let mut o = ac_core::items::ClientObject::new(id);
        o.weenie_class_id = class;
        o.container_id = container;
        objects.add_or_update(o, &mut ev);
    };
    put(PLAYER, 0, 0);
    put(0x8000_0010, 700, PLAYER);
    // The scarab and the prismatic taper.
    put(0x8000_0011, 101, PLAYER);
    put(0x8000_0012, 188, PLAYER);
    if let Some(container) = focus_container {
        put(0x8000_0013, WAR_FOCUS_CLASS, container);
    }
    objects
}

fn war_spell() -> SpellMetadata {
    SpellMetadata {
        school_id: ac_core::spells::MagicSchool::WAR_MAGIC,
        formula_components: vec![1, 63, 10],
        formula_version: 0,
        ..SpellMetadata::new(50, "War I", 0)
    }
}

fn examine_requirements() -> SpellComponentRequirements {
    SpellComponentRequirements::new(
        HashMap::new(),
        HashMap::from([(1, 101), (0xBC, 188), (63, 163), (10, 110)]),
        HashMap::from([(1, WAR_FOCUS_CLASS)]),
    )
}

#[test]
fn a_carried_focus_shows_the_spell_as_scarabs_and_prismatic_tapers() {
    let objects = examine_table(Some(PLAYER));
    let world = TableComponentWorld {
        objects: &objects,
        player: PLAYER,
    };
    assert_eq!(
        examine_components(&examine_requirements(), &world, "testaccount", &war_spell()),
        [(1, true), (0xBC, true)]
    );
}

#[test]
fn without_a_focus_in_the_main_pack_the_spell_shows_its_own_components() {
    // No focus, and a focus inside a side pack: neither counts.
    for focus in [None, Some(0x8000_0010)] {
        let objects = examine_table(focus);
        let world = TableComponentWorld {
            objects: &objects,
            player: PLAYER,
        };
        assert_eq!(
            examine_components(&examine_requirements(), &world, "testaccount", &war_spell()),
            [(1, true), (63, false), (10, false)]
        );
    }
}

#[test]
fn the_examined_formula_is_personalised_for_the_account() {
    let formula = vec![6, 63, 10, 64, 20, 30, 65, 40];
    let spell = SpellMetadata {
        formula_components: formula.clone(),
        formula_version: 3,
        ..war_spell()
    };
    let objects = examine_table(None);
    let world = TableComponentWorld {
        objects: &objects,
        player: PLAYER,
    };
    let expected: Vec<u32> =
        ac_core::spells::customize_for_account(&formula, 3, "CanonicalAccount")
            .into_iter()
            .filter(|&c| c != 0)
            .collect();
    let shown: Vec<u32> =
        examine_components(&examine_requirements(), &world, "CanonicalAccount", &spell)
            .into_iter()
            .map(|(id, _)| id)
            .collect();
    assert_eq!(shown, expected);
    assert_ne!(shown, formula);
}
