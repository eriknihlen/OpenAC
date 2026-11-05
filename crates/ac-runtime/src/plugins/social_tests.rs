//! Fellowship, allegiance, login, recalls, the selection, dialogs and
//! recovery against the runtime owners. Cases from OpenAC
//! `tests/AcDream.Runtime.Tests/Plugins/RuntimeAutomationSurfaceTests.cs`
//! and `RuntimeAutomationSurfaceAllegianceTests.cs`, named per test.

use std::sync::mpsc;

use ac_plugin::abi::calls;
use ac_plugin::abi::social::{
    AllegianceStatus, FellowshipCreate, FellowshipStatus, RecallKind, SelectionAction,
};

use super::social::RecallWatch;
use super::tests::{call, in_world, with_binding};
use super::{PluginBinding, RosterEntry};
use crate::session::{CharacterSelector, LifecycleCommand, Outbox, SessionCommand};

const STRANGER: u32 = 0x5000_0099;

// OpenAC `RecoveryClearsExactlyOneCanonicalBusyReference`.
#[test]
fn recovery_clears_exactly_one_busy_reference() {
    let mut state = in_world();
    state.inventory.increment_busy();
    state.inventory.increment_busy();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let first = call::<calls::RecoveryClearOneBusy>(b, &());
        let second = call::<calls::RecoveryClearOneBusy>(b, &());
        let already_clear = call::<calls::RecoveryClearOneBusy>(b, &());
        assert!(first.accepted);
        assert_eq!((first.previous_count, first.current_count), (2, 1));
        assert_eq!((second.previous_count, second.current_count), (1, 0));
        assert_eq!(
            (already_clear.previous_count, already_clear.current_count),
            (0, 0)
        );
        assert_eq!(
            already_clear.message,
            "The action busy count was already zero."
        );
        assert_eq!(call::<calls::RecoveryBusyState>(b, &()).busy_count, 0);
    });
}

// OpenAC `AllegianceSnapshotIsUnavailableBeforeAuthoritativeProfile`.
#[test]
fn the_allegiance_is_unknown_before_the_server_describes_it() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let allegiance = call::<calls::AllegianceState>(b, &());
        assert!(allegiance.is_available);
        assert!(!allegiance.is_known);
        assert_eq!(allegiance.revision, 0);
        assert_eq!(allegiance.monarch_id, 0);
    });
}

// OpenAC (origin/main) `AllegianceParityTests.
// EitherClientNamesTheMonarchThePatronAndTheVassals`: the monarch, the
// character's patron and its vassals by name, rank, level, heritage, gender
// and presence, as the server's statement gave them.
#[test]
fn the_allegiance_names_the_monarch_the_patron_and_the_vassals() {
    use ac_plugin::abi::social::AllegianceMember as Member;
    use ac_proto::allegiance::{AllegianceMember, AllegianceProfile, AllegianceUpdate};

    use super::tests::PLAYER;

    const MONARCH: u32 = 0x5000_0001;
    const PATRON: u32 = 0x5000_0002;
    const VASSAL: u32 = 0x5000_0077;
    let record = |id: u32,
                  parent: u32,
                  online: bool,
                  name: &str,
                  rank: u16,
                  level: u32,
                  gender: u8,
                  heritage: u8| {
        AllegianceMember {
            character_id: id,
            parent_guid: parent,
            is_logged_in: online,
            name: name.into(),
            rank,
            level,
            gender,
            heritage_group: heritage,
            ..AllegianceMember::default()
        }
    };
    let mut state = in_world();
    state
        .gameplay
        .allegiance_mut()
        .apply_update(&AllegianceUpdate {
            rank: 3,
            profile: AllegianceProfile {
                total_members: 4,
                total_vassals: 1,
                record_count: 4,
                allegiance_name: "The Order".into(),
                monarch: Some(record(MONARCH, 0, true, "Monarch", 6, 275, 2, 3)),
                records: vec![
                    record(PATRON, MONARCH, false, "Patron", 4, 180, 1, 1),
                    record(PLAYER, PATRON, true, "Parity", 3, 90, 1, 2),
                    record(VASSAL, PLAYER, true, "Squire", 1, 20, 2, 8),
                ],
                ..AllegianceProfile::default()
            },
        });
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let allegiance = call::<calls::AllegianceState>(b, &());
        let member =
            |object_id: u32, name: &str, rank, level, heritage_group, gender, is_online| Member {
                object_id,
                name: name.into(),
                rank,
                level,
                heritage_group,
                gender,
                is_online,
            };
        assert_eq!(
            allegiance.monarch,
            Some(member(MONARCH, "Monarch", 6, 275, 3, 2, true))
        );
        assert_eq!(
            allegiance.patron,
            Some(member(PATRON, "Patron", 4, 180, 1, 1, false))
        );
        assert_eq!(
            allegiance.vassals,
            [member(VASSAL, "Squire", 1, 20, 8, 2, true)]
        );
        assert_eq!((allegiance.vassal_count, allegiance.member_count), (1, 4));
        assert_eq!(allegiance.monarch_id, MONARCH);
    });
}

// OpenAC `SwearingToNobodyIsRefusedAndNeverSent`,
// `SwearingToSomeoneTheClientHasNeverHeardOfIsRefusedAndNeverSent`,
// `BreakingWithNobodyIsRefusedAndNeverSent` and
// `BreakingWithSomeoneOutsideTheAllegianceIsRefusedAndNeverSent`.
#[test]
fn allegiance_commands_check_their_target_first_and_send_nothing() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        for target in [0, STRANGER] {
            let swear = call::<calls::AllegianceSwear>(b, &target);
            assert_eq!(swear.status, AllegianceStatus::InvalidTarget);
            let broken = call::<calls::AllegianceBreak>(b, &target);
            assert_eq!(broken.status, AllegianceStatus::InvalidTarget);
        }
    });
    assert!(outbox.is_empty());
}

// OpenAC `NeitherCommandIsSentBeforeTheCharacterIsInTheWorld` and
// `AnUnboundSurfaceAnswersUnavailableRatherThanThrowing`, with the
// fellowship's commands beside them.
#[test]
fn without_a_connection_the_commands_are_unavailable() {
    let mut state = in_world();
    let (events, _host) = mpsc::channel();
    let mut binding = PluginBinding::offline(&mut state, &events, 0);
    let b = &mut binding;
    assert_eq!(
        call::<calls::AllegianceSwear>(b, &STRANGER).status,
        AllegianceStatus::Unavailable
    );
    assert_eq!(
        call::<calls::AllegianceBreak>(b, &STRANGER).status,
        AllegianceStatus::Unavailable
    );
    let create = FellowshipCreate {
        name: "Band".into(),
        share_experience: true,
    };
    assert_eq!(
        call::<calls::FellowshipCreate>(b, &create),
        FellowshipStatus::Unavailable
    );
    assert_eq!(
        call::<calls::FellowshipQuit>(b, &false),
        FellowshipStatus::Unavailable
    );
    assert!(!call::<calls::LoginState>(b, &()).is_available);
    assert!(!call::<calls::LoginLogout>(b, &()));
    assert!(!call::<calls::RecallState>(b, &()).is_available);
    assert!(!call::<calls::RecoveryClearOneBusy>(b, &()).accepted);
}

// OpenAC `LiveSessionController.TrySetNextLogin`: only a character on the
// list that may enter; clearing always works while a session is there.
#[test]
fn the_next_login_is_a_character_that_may_enter() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        b.facts.roster = vec![
            RosterEntry {
                id: 0x5000_0001,
                name: "+Horan".into(),
                index: 0,
                pending_delete: false,
            },
            RosterEntry {
                id: 0x5000_0002,
                name: "+Gone".into(),
                index: 1,
                pending_delete: true,
            },
        ];
        assert!(!call::<calls::LoginSetNext>(b, &0));
        assert!(!call::<calls::LoginSetNext>(b, &0x5000_0002));
        assert!(!call::<calls::LoginSetNext>(b, &0x5000_0003));
        assert!(call::<calls::LoginSetNext>(b, &0x5000_0001));
        let login = call::<calls::LoginState>(b, &());
        assert_eq!(login.next_login_id, 0x5000_0001);
        assert_eq!(login.roster.len(), 2);
        assert!(login.roster[1].is_pending_delete);
        assert!(call::<calls::LoginClearNext>(b, &()));
        assert!(matches!(
            b.deferred.as_slice(),
            [
                SessionCommand::Lifecycle(LifecycleCommand::SetNextLogin(Some(
                    CharacterSelector::Id(0x5000_0001)
                ))),
                SessionCommand::Lifecycle(LifecycleCommand::SetNextLogin(None)),
            ]
        ));
    });
}

// OpenAC `RetailUiRuntime.TryAnswerConfirmation`: only the open question's
// context is answered.
#[test]
fn a_dialog_is_answered_only_while_its_question_is_open() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert!(!call::<calls::DialogAnswer>(b, &(7, true)));
    });
}

// OpenAC `SelectionState.Set`: a selection that does not change answers
// false.
#[test]
fn a_selection_that_changes_nothing_answers_false() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert!(!call::<calls::SelectionClear>(b, &()));
        assert!(!call::<calls::SelectionExecute>(
            b,
            &SelectionAction::PreviousSelection
        ));
        assert_eq!(call::<calls::SelectionState>(b, &()).selected, None);
    });
}

// OpenAC `CaptureRecallLocation`: a completed recall teaches its place,
// once, and only for the request it answered.
#[test]
fn a_completed_recall_teaches_its_place() {
    let mut watch = RecallWatch::default();
    // Nothing asked: nothing pending, nothing learned.
    assert_eq!(watch.take_pending(), 0);
    watch.learn(0x0123_0001, None);
    let mut state = in_world();
    state.plugin_services.recalls = watch;
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let recall = call::<calls::RecallGo>(b, &RecallKind::Marketplace);
        let after = call::<calls::RecallState>(b, &());
        assert_eq!(after.last_request.revision, 1);
        assert_eq!(after.last_request.status, recall.status);
        assert!(after.locations.is_empty());
    });
    let watch = &mut state.plugin_services.recalls;
    assert_eq!(watch.take_pending(), 1);
    watch.learn(0x0166_0001, None);
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let after = call::<calls::RecallState>(b, &());
        let [market] = after.locations.as_slice() else {
            panic!("one place, got {:?}", after.locations);
        };
        assert_eq!(market.kind, RecallKind::Marketplace);
        assert_eq!(market.position.cell_id, 0x0166);
        assert_eq!(market.name, "Marketplace");
        assert_eq!(market.revision, 1);
    });
}

// OpenAC `FellowshipParityTests.AFellowshipsTermsAndLevelsReachAPluginOnBothClients`
// (`tests/AcDream.HostParity.Tests/Scenarios/FellowshipParityTests.cs`):
// whether the fellowship shares experience, whether the split is even, and
// each member's level, as the server's roster states them (own level 30,
// the other fellow's 42); a restatement with an even split and no sharing
// reads back as exactly that.
#[test]
fn a_fellowships_terms_and_levels_reach_a_plugin() {
    use ac_proto::fellowship::{FellowMember, FellowshipFullUpdate};

    use super::events_tests::described;
    use super::tests::PLAYER;

    const OTHER: u32 = 0x5000_0011;
    const CELL: u32 = 0xA9B4_0031;
    const OWN_LEVEL: u32 = 30;
    const FELLOW_LEVEL: u32 = 42;
    let member = |guid: u32, name: &str, level: u32| FellowMember {
        guid,
        cp_cache: 0,
        lum_cache: 0,
        level,
        max_health: 100,
        max_stamina: 100,
        max_mana: 100,
        current_health: 100,
        current_stamina: 100,
        current_mana: 100,
        share_loot: 0,
        name: name.into(),
    };
    let roster = |share_xp: bool, even_xp_split: bool| FellowshipFullUpdate {
        members: vec![
            member(PLAYER, "Parity", OWN_LEVEL),
            member(OTHER, "Another", FELLOW_LEVEL),
        ],
        name: "Parity Fellows".into(),
        leader_guid: PLAYER,
        share_xp,
        even_xp_split,
        open_fellow: false,
        locked: false,
        departed: Vec::new(),
    };

    let mut state = in_world();
    state.world.apply(&described(
        PLAYER,
        1,
        "+Horan",
        0x10,
        0x8 | 0x10,
        Some(CELL),
    ));
    state.world.apply(&described(
        OTHER,
        1,
        "Another",
        0x10,
        0x8 | 0x10,
        Some(CELL),
    ));
    state.world.drain_changes().for_each(drop);

    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let none = call::<calls::FellowshipState>(b, &());
        assert!(!none.is_in_fellowship);
        assert!(!none.shares_experience);
        assert!(!none.splits_experience_evenly);
    });

    state
        .gameplay
        .fellowship_mut()
        .apply_full_update(&roster(true, false));
    with_binding(&mut state, &mut outbox, |b| {
        let terms = call::<calls::FellowshipState>(b, &());
        assert!(terms.is_in_fellowship);
        assert!(terms.shares_experience);
        assert!(!terms.splits_experience_evenly);
        let levels: Vec<(u32, u32)> = call::<calls::FellowshipRoster>(b, &())
            .iter()
            .map(|m| (m.object_id, m.level))
            .collect();
        assert_eq!(levels, [(PLAYER, OWN_LEVEL), (OTHER, FELLOW_LEVEL)]);
        let others: Vec<(u32, u32)> = call::<calls::FellowshipMembers>(b, &())
            .iter()
            .map(|m| (m.object_id, m.level))
            .collect();
        assert_eq!(others, [(OTHER, FELLOW_LEVEL)]);
    });

    state
        .gameplay
        .fellowship_mut()
        .apply_full_update(&roster(false, true));
    with_binding(&mut state, &mut outbox, |b| {
        let terms = call::<calls::FellowshipState>(b, &());
        assert!(!terms.shares_experience);
        assert!(terms.splits_experience_evenly);
    });
}

// The roster names every fellow the server lists, whether or not the
// client can see them: one out of range (not in the world), one hidden and
// one not drawn read back at an infinite distance. The other fellows the
// client can see stay as they were: only the one in sight.
#[test]
fn the_roster_keeps_fellows_the_client_cannot_see() {
    use ac_proto::fellowship::{FellowMember, FellowshipFullUpdate};

    use super::events_tests::described;
    use super::tests::PLAYER;

    const NEAR: u32 = 0x5000_0011;
    const AWAY: u32 = 0x5000_0012;
    const HIDDEN: u32 = 0x5000_0013;
    const UNDRAWN: u32 = 0x5000_0014;
    const CELL: u32 = 0xA9B4_0031;
    let member = |guid: u32, name: &str| FellowMember {
        guid,
        cp_cache: 0,
        lum_cache: 0,
        level: 10,
        max_health: 100,
        max_stamina: 100,
        max_mana: 100,
        current_health: 100,
        current_stamina: 100,
        current_mana: 100,
        share_loot: 0,
        name: name.into(),
    };

    let mut state = in_world();
    for (guid, name) in [
        (PLAYER, "+Horan"),
        (NEAR, "Near"),
        (HIDDEN, "Hidden"),
        (UNDRAWN, "Undrawn"),
    ] {
        state
            .world
            .apply(&described(guid, 1, name, 0x10, 0x8 | 0x10, Some(CELL)));
    }
    for (guid, bits) in [
        (HIDDEN, ac_core::physics::PhysicsState::HIDDEN.bits()),
        (UNDRAWN, ac_core::physics::PhysicsState::NO_DRAW.bits()),
    ] {
        state
            .world
            .apply(&ac_proto::Inbound::SetState(ac_proto::SetState {
                guid,
                physics_state: bits,
                instance_sequence: 1,
                state_sequence: 2,
            }));
    }
    state.world.drain_changes().for_each(drop);
    state
        .gameplay
        .fellowship_mut()
        .apply_full_update(&FellowshipFullUpdate {
            members: vec![
                member(PLAYER, "+Horan"),
                member(NEAR, "Near"),
                member(AWAY, "Away"),
                member(HIDDEN, "Hidden"),
                member(UNDRAWN, "Undrawn"),
            ],
            name: "Parity Fellows".into(),
            leader_guid: PLAYER,
            share_xp: true,
            even_xp_split: true,
            open_fellow: false,
            locked: false,
            departed: Vec::new(),
        });

    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let mut roster: Vec<(u32, f32)> = call::<calls::FellowshipRoster>(b, &())
            .iter()
            .map(|m| (m.object_id, m.distance))
            .collect();
        roster.sort_by_key(|(id, _)| *id);
        let ids: Vec<u32> = roster.iter().map(|(id, _)| *id).collect();
        assert_eq!(ids, [PLAYER, NEAR, AWAY, HIDDEN, UNDRAWN]);
        assert_eq!(roster[0].1, 0.0);
        assert!(roster[1].1.is_finite(), "{roster:?}");
        for (id, distance) in &roster[2..] {
            assert_eq!(*distance, f32::INFINITY, "{id:08X}");
        }

        let others: Vec<u32> = call::<calls::FellowshipMembers>(b, &())
            .iter()
            .map(|m| m.object_id)
            .collect();
        assert_eq!(others, [NEAR]);
    });
}
