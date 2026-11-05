//! Ported from OpenAC `tests/AcDream.Runtime.Tests/Physics/RuntimeAuthoritativePositionRouteClassifierTests.cs`.
//! The generation and authority-version cases of
//! `StaleOrMalformedAuthorityIsRejectedWithoutRouting` test OpenAC's staging
//! tokens; the stale-teleport and zero-entity cases are kept.

use ac_core::physics::PositionDisposition;
use ac_proto::{Position, Quaternion, Vector3};

use super::*;

const CELL: u32 = 0x0101_FFFF;
const HIDDEN_STATE: u32 = 0x4000;
const STATIC_STATE: u32 = 0x1;
const NO_DRAW_STATE: u32 = 0x20;
const IGNORE_COLLISIONS_STATE: u32 = 0x10;

fn authority(previous: u16, accepted: u16, disposition: PositionDisposition) -> PositionAuthority {
    PositionAuthority {
        entity: ObjectKey {
            local_id: 0x7000_0001,
            incarnation: 3,
        },
        previous_teleport_sequence: previous,
        accepted_teleport_sequence: accepted,
        timestamp_disposition: disposition,
    }
}

fn apply() -> PositionAuthority {
    authority(10, 10, PositionDisposition::Apply)
}

fn position(cell: u32, x: f32, w: f32) -> Position {
    Position {
        cell_id: cell,
        origin: Vector3 {
            x,
            y: 20.0,
            z: 30.0,
        },
        orientation: Quaternion {
            w,
            x: 0.0,
            y: 0.0,
            z: 0.0,
        },
    }
}

fn good() -> Position {
    position(CELL, 10.0, 1.0)
}

fn create(
    kind: PositionEntityKind,
    residence: CreateResidenceKind,
    p: Option<Position>,
    state: u32,
) -> PositionRoute {
    classify_create(&CreatePositionRouteRequest {
        authority: apply(),
        entity_kind: kind,
        residence,
        accepted_position: p,
        physics_state: state,
    })
}

fn remote_request() -> AcceptedPositionRouteRequest {
    AcceptedPositionRouteRequest {
        authority: apply(),
        entity_kind: PositionEntityKind::Remote,
        source: AcceptedPositionSource::PositionEvent,
        accepted_position: good(),
        placement_frame: Some(0),
        committed_cell_id: Some(CELL),
        has_contact: true,
        player_distance: 10.0,
        use_position_from_server: false,
        has_animations: false,
        physics_state: 0,
    }
}

fn local(authority: PositionAuthority, use_server: bool, contact: bool) -> PositionRoute {
    classify_accepted_position(&AcceptedPositionRouteRequest {
        authority,
        entity_kind: PositionEntityKind::LocalPlayer,
        placement_frame: None,
        has_contact: contact,
        player_distance: 0.0,
        use_position_from_server: use_server,
        ..remote_request()
    })
}

#[test]
fn top_level_create_always_uses_the_enter_world_flags() {
    for (kind, op, hook) in [
        (
            PositionEntityKind::LocalPlayer,
            SetPositionOperationKind::InitialLogin,
            TeleportHookPhase::AfterEnterWorld,
        ),
        (
            PositionEntityKind::Remote,
            SetPositionOperationKind::RemoteAuthoritative,
            TeleportHookPhase::None,
        ),
        (
            PositionEntityKind::Projectile,
            SetPositionOperationKind::ProjectileAuthoritative,
            TeleportHookPhase::None,
        ),
    ] {
        let route = create(
            kind,
            CreateResidenceKind::TopLevel,
            Some(good()),
            STATIC_STATE | HIDDEN_STATE | NO_DRAW_STATE,
        );
        assert_eq!(route.disposition, PositionDispositionRoute::SetPosition);
        assert_eq!(route.operation_kind, op);
        assert_eq!(route.set_position_flags.0, 0x11);
        assert_eq!(route.teleport_hook_phase, hook);
        assert!(route.performs_set_position());
        assert!(!route.collision_batch_eligible);
    }
}

#[test]
fn a_new_parented_or_picked_up_create_awaits_a_position() {
    for residence in [CreateResidenceKind::Parented, CreateResidenceKind::PickedUp] {
        let route = create(PositionEntityKind::Remote, residence, None, 0);
        assert_eq!(
            route.disposition,
            PositionDispositionRoute::AwaitFreshPosition
        );
        assert!(!route.leave_world);
        assert!(!route.performs_set_position());
    }
}

#[test]
fn create_and_position_reject_malformed_frames() {
    for invalid in [
        position(0, 10.0, 1.0),
        position(CELL, f32::NAN, 1.0),
        position(CELL, f32::INFINITY, 1.0),
        position(CELL, 10.0, 0.0),
        position(CELL, 10.0, f32::NAN),
    ] {
        let c = create(
            PositionEntityKind::Remote,
            CreateResidenceKind::TopLevel,
            Some(invalid),
            0,
        );
        let u = classify_accepted_position(&AcceptedPositionRouteRequest {
            accepted_position: invalid,
            player_distance: 1.0,
            ..remote_request()
        });
        assert_eq!(c.disposition, PositionDispositionRoute::RejectedData);
        assert_eq!(u.disposition, PositionDispositionRoute::RejectedData);
    }
}

#[test]
fn an_accepted_position_unparents_and_applies_the_default_zero_placement() {
    for source in [
        AcceptedPositionSource::PositionEvent,
        AcceptedPositionSource::SameIncarnationCreate,
    ] {
        let route = classify_accepted_position(&AcceptedPositionRouteRequest {
            source,
            placement_frame: None,
            ..remote_request()
        });
        assert_eq!(route.disposition, PositionDispositionRoute::Interpolate);
        assert!(route.unparent_before_routing);
        assert!(route.apply_placement_frame_before_routing);
        assert_eq!(route.placement_frame, 0);
    }
}

#[test]
fn an_animated_object_unparents_but_keeps_its_placement_frame() {
    let route = classify_accepted_position(&AcceptedPositionRouteRequest {
        has_animations: true,
        ..remote_request()
    });
    assert_eq!(route.disposition, PositionDispositionRoute::Interpolate);
    assert!(route.unparent_before_routing);
    assert!(!route.apply_placement_frame_before_routing);
}

#[test]
fn a_same_incarnation_create_forces_the_contact_route() {
    for (distance, expected) in [
        (95.999, PositionDispositionRoute::Interpolate),
        (96.0, PositionDispositionRoute::SetPositionSimple),
    ] {
        let route = classify_accepted_position(&AcceptedPositionRouteRequest {
            source: AcceptedPositionSource::SameIncarnationCreate,
            has_contact: false,
            player_distance: distance,
            ..remote_request()
        });
        assert_eq!(route.disposition, expected);
        assert!(!route.constrain_before_routing());
        assert!(route.constrain_after_routing());
    }
}

#[test]
fn a_remote_fresh_teleport_uses_the_wrap_safe_counter_and_the_hook() {
    for (previous, accepted) in [(u16::MAX, 0), (0x8000, 0)] {
        let route = classify_accepted_position(&AcceptedPositionRouteRequest {
            authority: authority(previous, accepted, PositionDisposition::Apply),
            has_contact: false,
            player_distance: f32::NAN,
            ..remote_request()
        });
        assert_eq!(route.disposition, PositionDispositionRoute::SetPosition);
        assert_eq!(route.set_position_flags.0, 0x1012);
        assert_eq!(
            route.teleport_hook_phase,
            TeleportHookPhase::BeforePositionOperation
        );
        assert!(route.constrain_after_routing());
    }
}

#[test]
fn a_cellless_remote_is_placed() {
    let route = classify_accepted_position(&AcceptedPositionRouteRequest {
        accepted_position: position(CELL, 200.0, 1.0),
        committed_cell_id: None,
        player_distance: 1.0,
        ..remote_request()
    });
    assert_eq!(route.disposition, PositionDispositionRoute::SetPosition);
    assert_eq!(
        route.teleport_hook_phase,
        TeleportHookPhase::BeforePositionOperation
    );
}

#[test]
fn a_remote_without_contact_is_not_moved_or_leashed() {
    let route = classify_accepted_position(&AcceptedPositionRouteRequest {
        has_contact: false,
        player_distance: f32::NAN,
        ..remote_request()
    });
    assert_eq!(
        route.disposition,
        PositionDispositionRoute::NoPositionOperation
    );
    assert!(!route.constrain_after_routing());
    assert!(!route.performs_set_position());
}

#[test]
fn a_remote_in_contact_uses_the_exact_96_metre_boundary() {
    for (distance, expected, stop) in [
        (0.0, PositionDispositionRoute::Interpolate, false),
        (95.999, PositionDispositionRoute::Interpolate, false),
        (96.0, PositionDispositionRoute::SetPositionSimple, true),
        (500.0, PositionDispositionRoute::SetPositionSimple, true),
    ] {
        let route = classify_accepted_position(&AcceptedPositionRouteRequest {
            player_distance: distance,
            ..remote_request()
        });
        assert_eq!(route.disposition, expected);
        assert_eq!(route.stop_interpolating, stop);
        assert!(route.constrain_after_routing());
        assert_eq!(route.set_position_flags.0, if stop { 0x1012 } else { 0 });
    }
}

#[test]
fn a_remote_in_contact_rejects_a_malformed_distance() {
    for distance in [f32::NAN, f32::INFINITY, f32::NEG_INFINITY, -0.001] {
        let route = classify_accepted_position(&AcceptedPositionRouteRequest {
            player_distance: distance,
            ..remote_request()
        });
        assert_eq!(route.disposition, PositionDispositionRoute::RejectedData);
    }
}

#[test]
fn a_fresh_forced_position_keeps_the_heading_and_answers_at_once() {
    let route = local(
        authority(10, 10, PositionDisposition::ForcePosition),
        false,
        true,
    );
    assert_eq!(
        route.disposition,
        PositionDispositionRoute::SetPositionSimple
    );
    assert_eq!(route.set_position_flags.0, 0x1012);
    assert!(route.preserve_heading);
    assert!(route.send_position_immediately);
    assert!(!route.unparent_before_routing);
    assert!(!route.apply_placement_frame_before_routing);
    assert!(!route.constrain_after_routing());
}

#[test]
fn a_forced_position_is_the_local_players_only_and_never_regressed() {
    let force = authority(10, 10, PositionDisposition::ForcePosition);
    let remote = classify_accepted_position(&AcceptedPositionRouteRequest {
        authority: force,
        ..remote_request()
    });
    let newer = local(
        authority(10, 11, PositionDisposition::ForcePosition),
        false,
        true,
    );
    let regressed = local(
        authority(11, 10, PositionDisposition::ForcePosition),
        false,
        true,
    );
    assert_eq!(
        remote.disposition,
        PositionDispositionRoute::RejectedAuthority
    );
    assert_eq!(
        newer.disposition,
        PositionDispositionRoute::SetPositionSimple
    );
    assert!(!newer.zero_velocity);
    assert_eq!(newer.teleport_hook_phase, TeleportHookPhase::None);
    assert_eq!(
        regressed.disposition,
        PositionDispositionRoute::RejectedAuthority
    );
}

#[test]
fn a_local_fresh_teleport_places_leashes_and_stops() {
    let route = local(
        authority(u16::MAX, 0, PositionDisposition::Apply),
        false,
        true,
    );
    assert_eq!(
        route.disposition,
        PositionDispositionRoute::SetPositionSimple
    );
    assert_eq!(route.set_position_flags.0, 0x1012);
    assert!(route.unparent_before_routing);
    assert!(route.apply_placement_frame_before_routing);
    assert!(route.constrain_after_routing());
    assert!(route.zero_velocity);
    assert_eq!(
        route.teleport_hook_phase,
        TeleportHookPhase::AfterPositionOperation
    );
}

#[test]
fn a_local_ordinary_update_leashes_and_interpolates_only_by_option_and_contact() {
    for (use_server, contact, expected) in [
        (false, false, PositionDispositionRoute::NoPositionOperation),
        (false, true, PositionDispositionRoute::NoPositionOperation),
        (true, false, PositionDispositionRoute::NoPositionOperation),
        (true, true, PositionDispositionRoute::Interpolate),
    ] {
        let route = local(apply(), use_server, contact);
        assert_eq!(route.disposition, expected);
        assert!(route.constrain_before_routing());
        assert!(!route.constrain_after_routing());
    }
}

#[test]
fn hidden_and_no_draw_do_not_gate_placement_but_reporting_is_separate() {
    let route = |state| {
        create(
            PositionEntityKind::Remote,
            CreateResidenceKind::TopLevel,
            Some(good()),
            state,
        )
    };
    let routes = [
        route(0),
        route(NO_DRAW_STATE),
        route(HIDDEN_STATE),
        route(IGNORE_COLLISIONS_STATE),
    ];
    assert!(
        routes
            .iter()
            .all(|r| r.disposition == PositionDispositionRoute::SetPosition)
    );
    assert!(routes[0].collision_batch_eligible);
    assert!(routes[1].collision_batch_eligible);
    assert!(!routes[2].collision_batch_eligible);
    assert!(routes[3].collision_batch_eligible);
}

#[test]
fn a_projectile_uses_the_remote_classification() {
    let route = classify_accepted_position(&AcceptedPositionRouteRequest {
        entity_kind: PositionEntityKind::Projectile,
        player_distance: 96.0,
        ..remote_request()
    });
    assert_eq!(
        route.operation_kind,
        SetPositionOperationKind::ProjectileAuthoritative
    );
    assert_eq!(
        route.disposition,
        PositionDispositionRoute::SetPositionSimple
    );
}

#[test]
fn stale_or_malformed_authority_is_rejected_without_routing() {
    let stale = classify_accepted_position(&AcceptedPositionRouteRequest {
        authority: authority(0, u16::MAX, PositionDisposition::Apply),
        ..remote_request()
    });
    let no_entity = classify_accepted_position(&AcceptedPositionRouteRequest {
        authority: PositionAuthority {
            entity: ObjectKey {
                local_id: 0,
                incarnation: 0,
            },
            ..apply()
        },
        ..remote_request()
    });
    for route in [stale, no_entity] {
        assert_eq!(
            route.disposition,
            PositionDispositionRoute::RejectedAuthority
        );
    }
}
