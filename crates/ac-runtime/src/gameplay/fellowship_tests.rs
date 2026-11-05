//! Ported from OpenAC
//! `tests/AcDream.Runtime.Tests/Gameplay/RuntimeFellowshipStateTests.cs`.

use ac_proto::fellowship::DepartedFellow;

use super::*;

const SELF: u32 = 0x5000_0001;
const LEADER: u32 = 0x5000_0002;
const OTHER: u32 = 0x5000_0003;
/// 2026-08-12 00:00 UTC, the test clock's start.
const T0: i64 = 1_786_492_800;

fn member(guid: u32, name: &str, health: u32) -> FellowMember {
    FellowMember {
        guid,
        cp_cache: 0,
        lum_cache: 0,
        level: 10,
        max_health: 100,
        max_stamina: 100,
        max_mana: 100,
        current_health: health,
        current_stamina: 100,
        current_mana: 100,
        share_loot: 0,
        name: name.into(),
    }
}

fn fellow(guid: u32) -> FellowMember {
    member(guid, "Fellow", 100)
}

fn full(members: Vec<FellowMember>) -> FellowshipFullUpdate {
    FellowshipFullUpdate {
        members,
        name: "The Fellows".into(),
        leader_guid: LEADER,
        share_xp: true,
        even_xp_split: false,
        open_fellow: true,
        locked: false,
        departed: Vec::new(),
    }
}

fn update(member: FellowMember, update_type: u32) -> FellowshipUpdateFellow {
    FellowshipUpdateFellow {
        member,
        update_type,
    }
}

fn view_member(state: &FellowshipState, guid: u32, now: f64) -> Option<FellowView> {
    state.view(now).members.into_iter().find(|m| m.guid == guid)
}

#[test]
fn vitals_age_counts_from_the_last_per_fellow_update() {
    let mut state = FellowshipState::default();
    state.apply_full_update(&full(vec![fellow(SELF), fellow(OTHER)]));
    assert_eq!(
        view_member(&state, OTHER, 0.0).unwrap().vitals_age_seconds,
        None
    );
    state.apply_update_fellow(&update(member(OTHER, "Fellow", 40), 3), T0, 10.0);
    assert_eq!(
        view_member(&state, OTHER, 14.0).unwrap().vitals_age_seconds,
        Some(4.0)
    );
    // A later full roster is not a vitals sample.
    state.apply_full_update(&full(vec![fellow(SELF), fellow(OTHER)]));
    assert_eq!(
        view_member(&state, OTHER, 17.0).unwrap().vitals_age_seconds,
        Some(7.0)
    );
    // A member the roster dropped comes back unknown.
    state.apply_full_update(&full(vec![fellow(SELF)]));
    state.apply_full_update(&full(vec![fellow(SELF), fellow(OTHER)]));
    assert_eq!(
        view_member(&state, OTHER, 17.0).unwrap().vitals_age_seconds,
        None
    );
}

#[test]
fn the_vitals_subscription_is_the_or_of_panel_and_request_sent_once_per_change() {
    let mut state = FellowshipState::default();
    assert_eq!(state.set_panel_visible(false), None);
    assert_eq!(state.set_panel_visible(true), Some(true));
    assert_eq!(state.set_vitals_requested(true), None);
    assert_eq!(state.set_panel_visible(false), None);
    assert_eq!(state.set_vitals_requested(false), Some(false));
    assert_eq!(state.set_vitals_requested(true), Some(true));
    state.reset_session();
    assert_eq!(state.set_vitals_requested(false), None);
    assert_eq!(state.set_panel_visible(true), Some(true));
}

#[test]
fn a_full_update_replaces_the_roster_and_flags() {
    let mut state = FellowshipState::default();
    state.apply_full_update(&full(vec![
        member(SELF, "Self", 100),
        member(LEADER, "Leader", 100),
        member(OTHER, "Other", 100),
    ]));
    let view = state.view(0.0);
    assert!(view.in_fellowship);
    assert_eq!(
        (view.name.as_str(), view.leader_guid, view.members.len()),
        ("The Fellows", LEADER, 3)
    );
    assert!(view.share_xp && !view.even_xp_split && view.open && !view.locked);
    assert_eq!(view_member(&state, OTHER, 0.0).unwrap().name, "Other");
    state.apply_full_update(&full(vec![member(SELF, "Self", 100)]));
    assert_eq!(state.member_count(), 1);
    assert!(state.member(OTHER).is_none());
}

#[test]
fn an_update_upserts_exactly_one_member() {
    let mut state = FellowshipState::default();
    state.apply_full_update(&full(vec![
        member(SELF, "Self", 100),
        member(LEADER, "Leader", 100),
    ]));
    state.apply_update_fellow(&update(member(SELF, "Self", 42), 3), T0, 0.0);
    assert_eq!(state.member(SELF).unwrap().current_health, 42);
    assert_eq!(state.member(LEADER).unwrap().name, "Leader");
    assert_eq!(state.member_count(), 2);
}

#[test]
fn an_update_before_any_roster_is_ignored() {
    let mut state = FellowshipState::default();
    let before = state.revision();
    state.apply_update_fellow(&update(fellow(SELF), 1), T0, 0.0);
    assert_eq!(state.revision(), before);
    assert!(!state.is_in_fellowship());
}

#[test]
fn the_player_leaving_clears_everything() {
    for dismiss in [false, true] {
        let mut state = FellowshipState::default();
        state.apply_full_update(&full(vec![fellow(SELF), fellow(LEADER), fellow(OTHER)]));
        if dismiss {
            state.apply_dismiss(SELF, SELF);
        } else {
            state.apply_quit(SELF, SELF);
        }
        let view = state.view(0.0);
        assert!(!view.in_fellowship);
        assert_eq!(
            (view.members.len(), view.name.as_str(), view.leader_guid),
            (0, "", 0)
        );
    }
}

#[test]
fn another_member_leaving_removes_only_them() {
    for dismiss in [false, true] {
        let mut state = FellowshipState::default();
        state.apply_full_update(&full(vec![fellow(SELF), fellow(LEADER), fellow(OTHER)]));
        if dismiss {
            state.apply_dismiss(OTHER, SELF);
        } else {
            state.apply_quit(OTHER, SELF);
        }
        assert!(state.is_in_fellowship());
        assert_eq!(state.member_count(), 2);
        assert!(state.member(OTHER).is_none() && state.member(SELF).is_some());
    }
}

#[test]
fn a_disband_clears() {
    let mut state = FellowshipState::default();
    state.apply_full_update(&full(vec![fellow(SELF), fellow(LEADER)]));
    state.apply_disband();
    assert!(!state.is_in_fellowship());
    assert_eq!(state.member_count(), 0);
}

#[test]
fn only_a_leader_quitting_without_disbanding_hands_off_first() {
    let mut state = FellowshipState::default();
    state.apply_full_update(&full(vec![fellow(SELF), fellow(LEADER), fellow(OTHER)]));
    assert_eq!(state.requires_leader_handoff_before_quit(SELF, false), None);
    assert_eq!(
        state.requires_leader_handoff_before_quit(LEADER, true),
        None
    );
    let new_leader = state.requires_leader_handoff_before_quit(LEADER, false);
    assert!(matches!(new_leader, Some(SELF | OTHER)));
    let mut solo = FellowshipState::default();
    solo.apply_full_update(&FellowshipFullUpdate {
        name: "Solo".into(),
        share_xp: false,
        open_fellow: false,
        ..full(vec![fellow(LEADER)])
    });
    assert_eq!(
        solo.requires_leader_handoff_before_quit(LEADER, false),
        None
    );
}

#[test]
fn the_revision_moves_on_every_kind_of_change() {
    let mut state = FellowshipState::default();
    let r0 = state.revision();
    state.apply_full_update(&full(vec![fellow(SELF), fellow(LEADER)]));
    let r1 = state.revision();
    state.apply_update_fellow(&update(member(SELF, "Fellow", 1), 3), T0, 0.0);
    let r2 = state.revision();
    state.apply_quit(LEADER, SELF);
    let r3 = state.revision();
    state.apply_disband();
    let r4 = state.revision();
    assert!(r0 < r1 && r1 < r2 && r2 < r3 && r3 < r4);
}

#[test]
fn share_loot_is_any_non_zero_word() {
    let mut state = FellowshipState::default();
    let mut sharer = fellow(OTHER);
    sharer.share_loot = 0x10;
    state.apply_full_update(&full(vec![sharer]));
    assert!(view_member(&state, OTHER, 0.0).unwrap().share_loot);
}

#[test]
fn a_session_reset_clears_the_roster() {
    let mut state = FellowshipState::default();
    state.apply_full_update(&full(vec![fellow(SELF), fellow(LEADER)]));
    state.reset_session();
    assert!(!state.is_in_fellowship());
    assert_eq!(state.member_count(), 0);
}

fn even_split_case(member_level: u32, leader_level: u32) -> bool {
    let mut state = FellowshipState::default();
    state.apply_full_update(&FellowshipFullUpdate {
        even_xp_split: true,
        ..full(vec![fellow(LEADER)])
    });
    let mut leader = fellow(LEADER);
    leader.level = leader_level;
    state.apply_update_fellow(&update(leader, 3), T0, 0.0);
    let mut joining = fellow(SELF);
    joining.level = member_level;
    state.apply_update_fellow(&update(joining, 1), T0, 0.0);
    state.even_xp_split()
}

#[test]
fn an_update_recomputes_the_even_split_by_the_wide_spread_rule() {
    assert!(even_split_case(10, 10));
    assert!(!even_split_case(10, 20));
    assert!(even_split_case(60, 200));
}

#[test]
fn without_shared_experience_the_even_split_is_left_alone() {
    let mut state = FellowshipState::default();
    state.apply_full_update(&FellowshipFullUpdate {
        share_xp: false,
        even_xp_split: true,
        ..full(vec![fellow(LEADER)])
    });
    let mut joining = fellow(SELF);
    joining.level = 999;
    state.apply_update_fellow(&update(joining, 1), T0, 0.0);
    assert!(state.even_xp_split());
}

#[test]
fn a_full_update_keeps_the_wire_s_even_split() {
    let mut low = fellow(LEADER);
    low.level = 5;
    let mut high = fellow(SELF);
    high.level = 500;
    let mut state = FellowshipState::default();
    state.apply_full_update(&FellowshipFullUpdate {
        even_xp_split: true,
        ..full(vec![low, high])
    });
    assert!(state.even_xp_split());
}

fn locked(departed: Vec<DepartedFellow>) -> FellowshipFullUpdate {
    FellowshipFullUpdate {
        share_xp: false,
        open_fellow: false,
        locked: true,
        departed,
        ..full(vec![fellow(LEADER)])
    }
}

#[test]
fn a_locked_fellowship_refuses_a_new_member_who_never_left() {
    let mut state = FellowshipState::default();
    state.apply_full_update(&locked(Vec::new()));
    let before = state.revision();
    state.apply_update_fellow(&update(fellow(OTHER), 1), T0, 0.0);
    assert!(state.member(OTHER).is_none());
    assert_eq!((state.member_count(), state.revision()), (1, before));
}

#[test]
fn a_locked_fellowship_admits_a_member_who_left_within_the_grace() {
    let mut state = FellowshipState::default();
    let left = i32::try_from(T0).unwrap();
    state.apply_full_update(&locked(vec![DepartedFellow {
        guid: OTHER,
        departed_timestamp: left,
    }]));
    state.apply_update_fellow(&update(fellow(OTHER), 1), T0 + 899, 0.0);
    assert!(state.member(OTHER).is_some());
    assert_eq!(state.member_count(), 2);
}

#[test]
fn a_locked_fellowship_refuses_a_member_who_left_too_long_ago() {
    let mut state = FellowshipState::default();
    let left = i32::try_from(T0).unwrap();
    state.apply_full_update(&locked(vec![DepartedFellow {
        guid: OTHER,
        departed_timestamp: left,
    }]));
    state.apply_update_fellow(&update(fellow(OTHER), 1), T0 + 901, 0.0);
    assert!(state.member(OTHER).is_none());
}

#[test]
fn a_locked_fellowship_never_gates_a_member_s_own_refresh() {
    let mut state = FellowshipState::default();
    let mut update_full = locked(Vec::new());
    update_full.members.push(member(SELF, "Fellow", 100));
    state.apply_full_update(&update_full);
    state.apply_update_fellow(&update(member(SELF, "Fellow", 42), 3), T0, 0.0);
    assert_eq!(state.member(SELF).unwrap().current_health, 42);
}

#[test]
fn the_members_reflect_upserts_and_dismissals() {
    let mut state = FellowshipState::default();
    assert!(state.view(0.0).members.is_empty());
    state.apply_full_update(&full(vec![fellow(SELF), fellow(LEADER)]));
    state.apply_update_fellow(&update(member(OTHER, "Other", 100), 1), T0, 0.0);
    let view = state.view(0.0);
    assert_eq!(view.members.len(), 3);
    assert!(
        view.members
            .iter()
            .any(|m| m.guid == OTHER && m.name == "Other")
    );
    state.apply_dismiss(OTHER, SELF);
    let view = state.view(0.0);
    assert_eq!(view.members.len(), 2);
    assert!(view.members.iter().all(|m| m.guid != OTHER));
}

// OpenAC v0.1.25
// `tests/AcDream.Runtime.Tests/Gameplay/RuntimeFellowshipEventSinkTests.cs`:
// each departure is worded from the roster before it changes.
fn departure_roster(leader: u32) -> FellowshipState {
    let mut state = FellowshipState::default();
    let mut update = full(vec![
        member(SELF, "Self", 100),
        member(LEADER, "Jerry Seinfeld", 100),
        member(OTHER, "Another", 100),
    ]);
    update.name = "Test Fellows".into();
    update.leader_guid = leader;
    state.apply_full_update(&update);
    state
}

fn apply_departure(state: &mut FellowshipState, departure: Departure) {
    match departure {
        Departure::Quit(guid) => state.apply_quit(guid, SELF),
        Departure::Dismiss(guid) => state.apply_dismiss(guid, SELF),
        Departure::Disband => state.apply_disband(),
    }
}

#[test]
fn departures_are_worded_before_the_roster_changes() {
    let cases = [
        (
            Departure::Quit(OTHER),
            LEADER,
            "Another has left your Fellowship.",
        ),
        (
            Departure::Quit(SELF),
            LEADER,
            "You are no longer a member of the Test Fellows Fellowship.",
        ),
        (
            Departure::Dismiss(SELF),
            LEADER,
            "Jerry Seinfeld has dismissed you from the Fellowship.",
        ),
        (
            Departure::Dismiss(OTHER),
            LEADER,
            "Another has been dismissed from the Fellowship.",
        ),
        (
            Departure::Dismiss(OTHER),
            SELF,
            "You dismiss Another from your Fellowship.",
        ),
        (
            Departure::Disband,
            LEADER,
            "Jerry Seinfeld has disbanded your Fellowship.",
        ),
        (
            Departure::Disband,
            SELF,
            "You have disbanded your Fellowship.",
        ),
    ];
    for (departure, leader, expected) in cases {
        let mut state = departure_roster(leader);
        assert_eq!(
            state.departure_notice(departure, SELF).as_deref(),
            Some(expected)
        );
        apply_departure(&mut state, departure);
        match departure {
            Departure::Disband | Departure::Quit(SELF) | Departure::Dismiss(SELF) => {
                assert!(!state.is_in_fellowship());
            }
            Departure::Quit(guid) | Departure::Dismiss(guid) => {
                assert!(state.member(guid).is_none());
            }
        }
        // The same departure again says nothing more.
        assert_eq!(state.departure_notice(departure, SELF), None);
    }
}

#[test]
fn unknown_members_and_no_fellowship_produce_no_departure_notice() {
    let mut state = departure_roster(LEADER);
    assert_eq!(state.departure_notice(Departure::Quit(99), SELF), None);
    assert_eq!(state.departure_notice(Departure::Dismiss(99), SELF), None);
    state.apply_disband();
    for departure in [
        Departure::Quit(SELF),
        Departure::Dismiss(SELF),
        Departure::Disband,
    ] {
        assert_eq!(state.departure_notice(departure, SELF), None);
    }
}
