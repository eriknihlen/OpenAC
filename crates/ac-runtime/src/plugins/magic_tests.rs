//! Spells and magic against the spellbook. Cases from OpenAC
//! `tests/AcDream.Runtime.Tests/Plugins/RuntimeAutomationSurfaceTests.cs`.

use std::sync::Arc;

use ac_core::spells::{SpellFlags, SpellMetadata, SpellTable};
use ac_plugin::abi::calls;
use ac_plugin::abi::magic::{
    CastCompletion, CastGate, CastRequest, CastResult, ReportedCast, SpellList,
};

use super::events_tests::described;
use super::magic::EnchantmentTracker;
use super::tests::{PLAYER, call, in_world_with, with_binding};
use crate::session::{Outbox, SessionState};

/// OpenAC `TimedBeneficial`: a creature enchantment of one minute.
fn timed_beneficial(id: u32, name: &str, flags: u32, target_mask: u32) -> SpellMetadata {
    let mut spell = SpellMetadata::new(id, name, id);
    spell.duration = 60.0;
    spell.difficulty = 50;
    spell.flags = SpellFlags(flags);
    spell.generation = 1;
    spell.target_mask = target_mask;
    spell
}

/// OpenAC `DurationSpell`: Fire Vulnerability Other VII, family 7,
/// difficulty 350, a minute long.
fn duration_spell() -> SpellMetadata {
    let mut spell = SpellMetadata::new(42, "Fire Vulnerability Other VII", 7);
    spell.duration = 60.0;
    spell.difficulty = 350;
    spell.generation = 7;
    spell.is_debuff = true;
    spell.is_offensive = true;
    spell.target_mask = 0x10;
    spell
}

fn with_spells(spells: Vec<SpellMetadata>, learned: &[u32]) -> SessionState {
    let table = Arc::new(SpellTable::create(spells).expect("unique ids"));
    let mut state = in_world_with(Some(table));
    state.world.update_spellbook(|book| {
        for id in learned {
            book.on_spell_learned(*id, 1.0);
        }
    });
    state
}

// OpenAC `KnownSelfBuffsLeaveOutWhatTheCharacterCannotBeATargetOf`.
#[test]
fn known_self_buffs_leave_out_what_the_character_cannot_be_a_target_of() {
    let mut state = with_spells(
        vec![
            timed_beneficial(10, "Strength Self I", 0x0C, 0x10),
            timed_beneficial(11, "Assassin's Alchemy Kit", 0x06, 0x10),
            timed_beneficial(12, "Blessing of Someone", 0x06, 0x8107),
        ],
        &[10, 11, 12],
    );
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let mut buffs: Vec<u32> = call::<calls::SpellsKnown>(b, &SpellList::SelfBuffs)
            .iter()
            .map(|s| s.spell_id)
            .collect();
        buffs.sort_unstable();
        assert_eq!(buffs, [10, 12]);
        assert!(call::<calls::SpellsGet>(b, &11).is_some());
        assert!(call::<calls::SpellsIsKnown>(b, &11));
        assert!(!call::<calls::SpellsIsKnown>(b, &99));
    });
}

// OpenAC `ISpellCatalog.TryFindByName`: exact (ignoring case) first, then
// the first whose name contains the text, only when asked.
#[test]
fn spells_are_found_by_exact_then_partial_name() {
    let mut state = with_spells(
        vec![
            timed_beneficial(10, "Strength Self I", 0x0C, 0x10),
            timed_beneficial(11, "Strength Self II", 0x0C, 0x10),
            duration_spell(),
        ],
        &[42],
    );
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let find = |b: &mut _, name: &str, partial| {
            call::<calls::SpellsFind>(b, &(name.to_owned(), partial)).map(|s| s.spell_id)
        };
        assert_eq!(find(b, "strength self ii", false), Some(11));
        assert_eq!(find(b, "Self I", false), None);
        assert_eq!(find(b, "Self I", true), Some(10));
        assert_eq!(find(b, "  ", true), None);
        let fire = call::<calls::SpellsGet>(b, &42).expect("in the table");
        assert_eq!((fire.family, fire.difficulty, fire.tier), (7, 350, 7));
        assert!(fire.requires_turn_to);
        let combat: Vec<u32> = call::<calls::SpellsKnown>(b, &SpellList::Combat)
            .iter()
            .map(|s| s.spell_id)
            .collect();
        assert_eq!(combat, [42]);
        let page = call::<calls::SpellsPage>(b, &(0, 2));
        assert_eq!((page.total, page.spells.len()), (3, 2));
        assert_eq!(page.spells[0].spell_id, 10);
        let rest = call::<calls::SpellsPage>(b, &(2, 500));
        assert_eq!(
            rest.spells.iter().map(|s| s.spell_id).collect::<Vec<_>>(),
            [42]
        );
        assert!(call::<calls::SpellsPage>(b, &(3, 10)).spells.is_empty());
    });
}

// OpenAC `EvaluateGate` / `RequestCast`: an unknown spell, and a target
// that cannot be selected.
#[test]
fn the_cast_gate_and_cast_answer_like_openac() {
    let mut state = with_spells(vec![duration_spell()], &[42]);
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let at = |spell_id, target_id| CastRequest {
            spell_id,
            target_id,
        };
        assert_eq!(call::<calls::MagicGate>(b, &at(7, 0)), CastGate::NotKnown);
        assert_eq!(
            call::<calls::MagicGate>(b, &at(42, 0)),
            CastGate::NoTargetSelected
        );
        assert_eq!(
            call::<calls::MagicGate>(b, &at(42, 0x7000_0404)),
            CastGate::Refused
        );
        assert_eq!(
            call::<calls::MagicCast>(b, &at(7, 0)),
            CastResult::UnknownSpell
        );
        assert_eq!(
            call::<calls::MagicCast>(b, &at(42, 0x7000_0404)),
            CastResult::IncompatibleTarget
        );
        assert!(!call::<calls::MagicIsCasting>(b, &()));
    });
}

const CREATURE: u32 = 200;

/// A use-done from the server.
fn use_done(error: u32) -> Vec<u8> {
    let mut bytes = Vec::new();
    for word in [
        ac_proto::Opcode::GameEvent.value(),
        PLAYER,
        1,
        ac_proto::GameEventType::USE_DONE.0,
        error,
    ] {
        bytes.extend_from_slice(&word.to_le_bytes());
    }
    bytes
}

/// [`with_spells`] knowing the duration spell, with a creature to cast it
/// at and components switched off (the server's `@requirecomps off`).
fn caster() -> SessionState {
    let mut state = with_spells(vec![duration_spell()], &[42]);
    state
        .world
        .apply(&described(PLAYER, 1, "+Horan", 0x10, 0x8 | 0x10, None));
    state
        .world
        .apply(&described(CREATURE, 1, "Drudge", 0x10, 0x10, None));
    state
        .world
        .apply(&ac_proto::Inbound::PrivateUpdatePropertyBool(
            ac_proto::PrivateUpdatePropertyBool {
                sequence: 1,
                property: ac_core::properties::PropertyBool::SPELL_COMPONENTS_REQUIRED.0,
                value: false,
            },
        ));
    state.world.drain_changes().for_each(drop);
    state
}

// OpenAC `CampaignAutomationSurfaceTests.PendingCastReceiptDoesNotBlockAnotherCast`:
// a cast still waiting for its use-done neither blocks the gate nor a
// second cast, and its busy reference holds up no item request.
#[test]
fn a_pending_cast_receipt_does_not_block_another_cast() {
    let mut state = caster();
    let mut outbox = Outbox::new();
    let at = CastRequest {
        spell_id: 42,
        target_id: CREATURE,
    };
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(call::<calls::MagicGate>(b, &at), CastGate::Ready);
        assert_eq!(call::<calls::MagicCast>(b, &at), CastResult::Sent);
        assert!(!call::<calls::MagicIsCasting>(b, &()));
        assert_eq!(call::<calls::MagicGate>(b, &at), CastGate::Ready);
        assert_eq!(call::<calls::MagicCast>(b, &at), CastResult::Sent);
    });
    let transactions = state.inventory.transactions();
    assert_eq!(transactions.busy_count(), 2);
    assert_eq!(transactions.item_busy_count(), 0);
    assert!(transactions.can_begin_request());
    assert!(
        !state
            .drain_host_events()
            .iter()
            .any(|e| matches!(e, crate::session::HostEvent::Chat(line)
                if line.text.contains("cannot cast"))),
        "a re-issued cast is not refused"
    );
}

// OpenAC `LiveSessionRuntimeFactory` / `HeadlessSessionHost` use-done
// routing: the answer is a cast's only while no item action is out; with
// one out, it answers the item action and the cast keeps waiting.
#[test]
fn a_use_done_answers_a_cast_only_with_no_item_action_out() {
    let mut state = caster();
    let mut outbox = Outbox::new();
    let at = CastRequest {
        spell_id: 42,
        target_id: CREATURE,
    };
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(call::<calls::MagicCast>(b, &at), CastResult::Sent);
    });
    state.inventory.increment_busy();
    let done = use_done(0);
    let message = ac_proto::Inbound::parse(&done).expect("a use-done");
    state.apply(&message, &mut outbox, true, super::tests::CLOCKS);
    assert_eq!(state.gameplay.spell_cast().last_completion().revision, 0);
    assert_eq!(state.gameplay.spell_cast().pending_spell(), Some(42));
    assert_eq!(state.inventory.transactions().item_busy_count(), 0);

    state.apply(&message, &mut outbox, true, super::tests::CLOCKS);
    let completion = state.gameplay.spell_cast().last_completion();
    assert_eq!(
        (
            completion.revision,
            completion.spell_id,
            completion.target_id
        ),
        (1, 42, CREATURE)
    );
    assert_eq!(state.gameplay.spell_cast().pending_spell(), None);
    assert_eq!(state.inventory.transactions().busy_count(), 0);
}

// OpenAC `EnchantmentLedgerSharesReportedAndConfirmedLocalDurationCasts`:
// a reported cast and the client's own confirmed cast are both tracked,
// with the spell's family and quality and the seconds left.
#[test]
fn the_tracker_shares_reported_and_confirmed_local_casts() {
    let state = with_spells(vec![duration_spell()], &[42]);
    let book = state.world.spellbook();
    let now = 1_790_000_000_000;
    let mut tracker = EnchantmentTracker::default();
    assert!(tracker.report(
        book,
        &ReportedCast {
            target_id: 100,
            spell_id: 42,
            duration_seconds: 30.0,
        },
        now
    ));
    let reported = tracker.capture(100, now + 500);
    assert_eq!(reported.len(), 1);
    assert_eq!((reported[0].family, reported[0].quality), (7, 350));
    assert!((29.0..=30.0).contains(&reported[0].seconds_remaining));

    let confirmed = CastCompletion {
        revision: 1,
        spell_id: 42,
        target_id: 200,
        weenie_error: 0,
    };
    tracker.observe(book, confirmed.clone(), now);
    // Seen once only: forgetting consumes it.
    tracker.forget(200, 1);
    tracker.observe(book, confirmed, now);
    assert!(tracker.capture(200, now).is_empty());
    let second = CastCompletion {
        revision: 2,
        spell_id: 42,
        target_id: 200,
        weenie_error: 0,
    };
    tracker.observe(book, second, now);
    let local = tracker.capture(200, now + 1000);
    assert_eq!(local.len(), 1);
    assert_eq!(local[0].spell_id, 42);
    assert!((59.0..=60.0).contains(&local[0].seconds_remaining));
    // A refused report: no duration, unknown spell, no target.
    for bad in [
        (100, 42, 0.0),
        (100, 43, 5.0),
        (0, 42, 5.0),
        (100, 42, f64::NAN),
    ] {
        assert!(!tracker.report(
            book,
            &ReportedCast {
                target_id: bad.0,
                spell_id: bad.1,
                duration_seconds: bad.2,
            },
            now
        ));
    }
    // Expired entries are dropped.
    assert!(tracker.capture(100, now + 31_000).is_empty());
    tracker.forget(0, 2);
    assert!(tracker.capture(200, now).is_empty());
}

// ── Time left on an effect (OpenAC `RuntimeAutomationSurfaceEffectTimeTests`,
// as ported) ─────────────────────────────────────────────────────────────
//
// OpenAC stamps an effect with its runtime effect clock when it arrives and
// reads time left against the same clock, which keeps running in portal
// space. Here the session clock is that clock: the session stamps an effect
// with it (`gameplay::magic::apply_event`) and the plugin surface reads it;
// it is the connection's real time and never stands still.

mod effect_time {
    use std::time::Duration;

    use ac_plugin::abi::calls;
    use ac_proto::{GameEventType, Inbound, Opcode};

    use super::super::tests::{CLOCKS, PLAYER, call};
    use super::super::{CharacterInfo, Link, PluginBinding, SessionFacts};
    use super::{timed_beneficial, with_spells};
    use crate::session::{Clocks, Outbox, SessionState};

    const SPELL_ID: u16 = 0x0400;
    const COOLDOWN_ID: u32 = 0x0010;
    /// The spellbook's offset of a cooldown group's spell id.
    const COOLDOWN_SPELL_OFFSET: u32 = 0x8000;
    const MULTIPLICATIVE: u32 = 0x0000_4000;
    const COOLDOWN: u32 = 0x0100_0000;

    fn clocks(later: Duration) -> Clocks {
        Clocks {
            session: CLOCKS.session + later,
            ..CLOCKS
        }
    }

    /// The server's word that an effect of `duration` seconds now runs.
    fn arrive(state: &mut SessionState, spell: u16, stat_mod_type: u32, duration: f64) {
        let mut w = ac_bits::Writer::new();
        for word in [
            Opcode::GameEvent.value(),
            PLAYER,
            1,
            GameEventType::MAGIC_UPDATE_ENCHANTMENT.0,
        ] {
            w.u32(word);
        }
        w.u16(spell);
        w.u16(1);
        w.u16(3);
        w.u16(0);
        w.u32(8);
        w.f64(0.0);
        w.f64(duration);
        w.u32(0);
        w.f32(0.0);
        w.f32(0.0);
        w.f64(0.0);
        w.u32(stat_mod_type);
        w.u32(1);
        w.f32(1.1);
        let body = w.into_vec();
        let message = Inbound::parse(&body).expect("a game event");
        state.apply(&message, &mut Outbox::new(), true, clocks(Duration::ZERO));
    }

    /// Reads through a plugin binding `later` after the effect arrived;
    /// out of the world (`in_world` false) is the stretch in portal space.
    fn read<R>(
        state: &mut SessionState,
        later: Duration,
        in_world: bool,
        body: impl FnOnce(&mut PluginBinding<'_>) -> R,
    ) -> R {
        let (events, _host) = std::sync::mpsc::channel();
        let mut outbox = Outbox::new();
        let clocks = clocks(later);
        let mut binding = PluginBinding {
            state,
            events: &events,
            window_attached: false,
            character: Some(CharacterInfo {
                id: PLAYER,
                name: "+Horan".into(),
                world: "Local".into(),
                ..CharacterInfo::default()
            }),
            wall_unix_ms: clocks.wall_unix_ms,
            link: Some(Link {
                outbox: &mut outbox,
                active: true,
                clocks,
            }),
            facts: SessionFacts {
                in_world,
                ..SessionFacts::none()
            },
            deferred: Vec::new(),
        };
        body(&mut binding)
    }

    fn buffed() -> SessionState {
        let mut state = with_spells(
            vec![timed_beneficial(u32::from(SPELL_ID), "Buff", 0x0C, 0x10)],
            &[],
        );
        arrive(&mut state, SPELL_ID, MULTIPLICATIVE, 60.0);
        state
    }

    // `ATimedBuffReadsItsDurationLessTheTimeSinceItArrived`.
    #[test]
    fn a_timed_buff_reads_its_duration_less_the_time_since_it_arrived() {
        let mut state = buffed();
        let timed = read(&mut state, Duration::from_secs(10), true, |b| {
            call::<calls::CharacterEnchantments>(b, &true)
        });
        assert_eq!(timed.len(), 1);
        assert_eq!(timed[0].spell_id, u32::from(SPELL_ID));
        assert!((timed[0].seconds_remaining - 50.0).abs() < 1e-6);
    }

    // `ATimedBuffKeepsCountingDownInPortalSpace`.
    #[test]
    fn a_timed_buff_keeps_counting_down_in_portal_space() {
        let mut state = buffed();
        let timed = read(&mut state, Duration::from_secs(10), false, |b| {
            call::<calls::CharacterEnchantments>(b, &true)
        });
        assert_eq!(timed.len(), 1);
        assert!((timed[0].seconds_remaining - 50.0).abs() < 1e-6);
    }

    // `ACooldownKeepsCountingDownInPortalSpace`.
    #[test]
    fn a_cooldown_keeps_counting_down_in_portal_space() {
        let mut state = with_spells(Vec::new(), &[]);
        let spell = u16::try_from(COOLDOWN_ID + COOLDOWN_SPELL_OFFSET).unwrap();
        arrive(&mut state, spell, COOLDOWN, 30.0);
        let left = read(&mut state, Duration::from_secs(10), false, |b| {
            call::<calls::SpellsCooldown>(b, &COOLDOWN_ID)
        });
        assert!((left - 20.0).abs() < 1e-6, "{left}");
    }
}

// The known-spell lists' revision and their ids.
// OpenAC rebuilds its three lists on `SpellbookChanged`
// (`RuntimeAutomationSurface.RebuildSpellbook`) and hands out the same lists
// until the next rebuild; here the revision says whether a plugin's copy is
// still the lists, and the ids-only lists are the full lists' ids in order.
#[test]
fn the_known_spell_revision_moves_only_when_the_lists_can_change() {
    let spells = || {
        vec![
            timed_beneficial(10, "Strength Self I", 0x0C, 0x10),
            timed_beneficial(11, "Endurance Self I", 0x0C, 0x10),
            duration_spell(),
        ]
    };
    let mut state = with_spells(spells(), &[10, 42]);
    let mut outbox = Outbox::new();
    let read = |state: &mut SessionState, outbox: &mut Outbox| {
        with_binding(state, outbox, |b| {
            let revision = call::<calls::SpellsKnownRevision>(b, &());
            let lists: Vec<Vec<u32>> = SpellList::ALL
                .iter()
                .map(|list| {
                    let ids = call::<calls::SpellsKnownIds>(b, list);
                    let full: Vec<u32> = call::<calls::SpellsKnown>(b, list)
                        .iter()
                        .map(|s| s.spell_id)
                        .collect();
                    assert_eq!(ids, full, "{list:?}");
                    ids
                })
                .collect();
            // Reading the lists does not move the revision.
            assert_eq!(call::<calls::SpellsKnownRevision>(b, &()), revision);
            (revision, lists)
        })
    };

    let (first, lists) = read(&mut state, &mut outbox);
    assert_ne!(first, 0);
    assert_eq!(lists, [vec![10], vec![], vec![42]]);

    // A spell's power and a favorite leave the lists as they are.
    state.world.update_spellbook(|book| {
        book.on_spell_learned(10, 2.0);
        book.set_favorite(0, 0, 10);
    });
    assert_eq!(read(&mut state, &mut outbox), (first, lists));

    // Learning a spell, then forgetting one, each move it.
    state
        .world
        .update_spellbook(|book| book.on_spell_learned(11, 1.0));
    let (learned, lists) = read(&mut state, &mut outbox);
    assert!(learned > first);
    assert_eq!(lists[0], [10, 11]);
    state
        .world
        .update_spellbook(|book| book.on_spell_forgotten(10));
    let (forgot, lists) = read(&mut state, &mut outbox);
    assert!(forgot > learned);
    assert_eq!(lists[0], [11]);

    // A new spellbook (the rules installed again: a fresh, empty book)
    // moves it even with nothing learned.
    state.world.install_player_rules(
        Arc::new(SpellTable::create(spells()).expect("unique ids")),
        None,
    );
    let (fresh, lists) = read(&mut state, &mut outbox);
    assert!(fresh > forgot);
    assert_eq!(lists, [Vec::<u32>::new(), vec![], vec![]]);
}

/// Whether a targeted cast of `spell` at `target` was sent.
fn sent_cast_at(outbox: &mut Outbox, target: u32, spell: u32) -> bool {
    let mut tail = target.to_le_bytes().to_vec();
    tail.extend_from_slice(&spell.to_le_bytes());
    outbox.drain().any(|o| o.body.ends_with(&tail))
}

// OpenAC `CampaignAutomationSurfaceTests.CastWithoutSelectingSendsAtTheTargetAndKeepsTheSelection`:
// the gate and the cast take the named object without touching the
// selection, and the cast goes out at that object.
#[test]
fn a_cast_without_selecting_goes_out_at_the_target_and_keeps_the_selection() {
    let mut state = caster();
    let mut outbox = Outbox::new();
    let at = CastRequest {
        spell_id: 42,
        target_id: CREATURE,
    };
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(
            call::<calls::MagicGateWithoutSelecting>(b, &at),
            CastGate::Ready
        );
        assert_eq!(
            call::<calls::MagicCastWithoutSelecting>(b, &at),
            CastResult::Sent
        );
    });
    assert_eq!(state.gameplay.selection().selected(), None);
    assert!(sent_cast_at(&mut outbox, CREATURE, 42));
}

// OpenAC `CampaignAutomationSurfaceTests.CastWithoutSelectingRefusesAnObjectThatIsNotInTheWorld`
// and `CastWithoutSelectingAnswersNotKnownBeforeCheckingTheTarget`.
#[test]
fn a_cast_without_selecting_refuses_an_object_not_in_the_world() {
    let mut state = caster();
    let mut outbox = Outbox::new();
    let at = |spell_id, target_id| CastRequest {
        spell_id,
        target_id,
    };
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(
            call::<calls::MagicGateWithoutSelecting>(b, &at(42, 0x7000_0099)),
            CastGate::Refused
        );
        assert_eq!(
            call::<calls::MagicCastWithoutSelecting>(b, &at(42, 0x7000_0099)),
            CastResult::IncompatibleTarget
        );
        // An unknown spell answers before the target is looked at.
        assert_eq!(
            call::<calls::MagicGateWithoutSelecting>(b, &at(7, 0x7000_0099)),
            CastGate::NotKnown
        );
    });
    assert_eq!(state.gameplay.selection().selected(), None);
    assert!(!sent_cast_at(&mut outbox, 0x7000_0099, 42));
}

// OpenAC `CampaignAutomationSurfaceTests.TheSelectingCastOverloadStillSelects`.
#[test]
fn the_selecting_cast_still_selects() {
    let mut state = caster();
    let mut outbox = Outbox::new();
    let at = CastRequest {
        spell_id: 42,
        target_id: CREATURE,
    };
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(call::<calls::MagicCast>(b, &at), CastResult::Sent);
    });
    assert_eq!(state.gameplay.selection().selected(), Some(CREATURE));
}
