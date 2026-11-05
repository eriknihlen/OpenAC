//! Ported from OpenAC
//! `tests/AcDream.Runtime.Tests/Gameplay/RuntimeAllegianceStateTests.cs`
//! and `RuntimeAllegianceLoginRequestTests.cs`, and the info lines of
//! `AcDream.Core.Net/Messages/ClientCommandResponses.FormatAllegianceInfoLines`.

use super::*;

const MONARCH: u32 = 0x5000_0001;
const PATRON: u32 = 0x5000_0002;
const SELF: u32 = 0x5000_0003;
const VASSAL_A: u32 = 0x5000_0004;
const VASSAL_B: u32 = 0x5000_0005;

fn record(id: u32, parent: u32, name: &str, online: bool) -> AllegianceMember {
    AllegianceMember {
        character_id: id,
        parent_guid: parent,
        is_logged_in: online,
        name: name.into(),
        ..AllegianceMember::default()
    }
}

fn profile() -> AllegianceProfile {
    AllegianceProfile {
        total_members: 5,
        total_vassals: 3,
        record_count: 5,
        allegiance_name: "The Order".into(),
        monarch: Some(record(MONARCH, 0, "Monarch", true)),
        records: vec![
            record(PATRON, MONARCH, "Patron", true),
            record(SELF, PATRON, "Self", true),
            record(VASSAL_A, SELF, "VassalA", false),
            record(VASSAL_B, SELF, "VassalB", true),
        ],
        ..AllegianceProfile::default()
    }
}

fn update(rank: u32) -> AllegianceUpdate {
    AllegianceUpdate {
        rank,
        profile: profile(),
    }
}

#[test]
fn an_update_seeds_the_profile() {
    let mut state = AllegianceState::default();
    assert!(!state.has_server_seed());
    state.apply_update(&update(7));
    let view = state.view(SELF);
    assert!(state.has_server_seed() && view.has_server_seed);
    assert_eq!(
        (
            view.rank,
            view.name.as_str(),
            view.monarch.as_ref().map(|m| m.guid)
        ),
        (7, "The Order", Some(MONARCH))
    );
    assert_eq!(state.profile().unwrap().records.len(), 4);
}

#[test]
fn monarch_patron_and_vassals_walk_the_flat_list() {
    let mut state = AllegianceState::default();
    state.apply_update(&update(3));
    let view = state.view(SELF);
    assert_eq!(view.monarch.unwrap().name, "Monarch");
    assert_eq!(view.patron.unwrap().guid, PATRON);
    assert!(state.view(MONARCH).patron.is_none());
    // Siblings come last on the wire first.
    let vassals: Vec<u32> = view.vassals.iter().map(|v| v.guid).collect();
    assert_eq!(vassals, [VASSAL_B, VASSAL_A]);
}

#[test]
fn the_revision_moves_on_every_event() {
    let mut state = AllegianceState::default();
    let r0 = state.revision();
    state.apply_update(&update(3));
    let r1 = state.revision();
    state.apply_login_notification();
    let r2 = state.revision();
    state.apply_update_done();
    let r3 = state.revision();
    state.apply_update_done();
    let r4 = state.revision();
    assert!(r0 < r1 && r1 < r2 && r2 < r3 && r3 < r4);
}

#[test]
fn a_session_reset_clears_the_profile_and_the_seed() {
    let mut state = AllegianceState::default();
    state.apply_update(&update(3));
    state.reset_session();
    let view = state.view(SELF);
    assert!(!state.has_server_seed() && !view.has_server_seed);
    assert_eq!((view.rank, view.name.as_str(), view.monarch), (0, "", None));
    assert!(state.profile().is_none());
}

#[test]
fn a_reset_of_an_unseeded_owner_keeps_the_revision() {
    let mut state = AllegianceState::default();
    let before = state.revision();
    state.reset_session();
    assert_eq!(state.revision(), before);
}

#[test]
fn entering_the_world_asks_once_a_session_and_again_after_a_reset() {
    let mut state = AllegianceState::default();
    assert!(state.note_entered_world());
    assert!(!state.note_entered_world());
    state.reset_session();
    assert!(state.note_entered_world());
}

#[test]
fn the_info_lines_mark_who_is_online() {
    assert_eq!(
        info_lines(SELF, &profile()),
        [
            "Note: An asterisk (*) indicates that the character is currently online.",
            "Allegiance information for Self *:",
            "   Patron: Patron *",
            "   Vassals: ",
            "      VassalB *",
            "      VassalA",
        ]
    );
    assert!(info_lines(0x9999_9999, &profile()).is_empty());
    assert_eq!(info_lines(VASSAL_A, &profile()).len(), 3);
}
