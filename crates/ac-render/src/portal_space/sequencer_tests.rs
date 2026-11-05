//! OpenAC `TeleportAnimSequencerTests` (AcDream.Core.Tests/World), case for
//! case; golden levels from the same file.

use super::*;
use TeleportAnimEvent as E;
use TeleportAnimState as S;

fn tick(seq: &mut TeleportAnimSequencer, dt: f32, ready: bool) -> (TeleportAnimSnapshot, Vec<E>) {
    seq.tick(dt, ready, 72, false)
}

/// Drive by `seconds` in steps of `step` (OpenAC's `DriveSeconds`).
fn drive(
    seq: &mut TeleportAnimSequencer,
    seconds: f32,
    ready: bool,
    step: f32,
    frame: i32,
) -> (TeleportAnimSnapshot, Vec<E>) {
    let mut all = Vec::new();
    let mut remaining = seconds;
    let mut last = TeleportAnimSnapshot::default();
    while remaining > 0.0 {
        let dt = step.min(remaining);
        let (snap, events) = seq.tick(dt, ready, frame, false);
        last = snap;
        all.extend(events);
        remaining -= dt;
    }
    (last, all)
}

fn drive_seconds(
    seq: &mut TeleportAnimSequencer,
    seconds: f32,
    ready: bool,
) -> (TeleportAnimSnapshot, Vec<E>) {
    drive(seq, seconds, ready, 0.016, 72)
}

fn begun(kind: TeleportEntryKind) -> TeleportAnimSequencer {
    let mut seq = TeleportAnimSequencer::new();
    seq.begin(kind);
    seq
}

#[test]
fn enums_have_expected_values() {
    assert_eq!(S::Off as i32, 0);
    assert_eq!(S::WorldFadeOut as i32, 1);
    assert_eq!(S::TunnelFadeIn as i32, 2);
    assert_eq!(S::Tunnel as i32, 3);
    assert_eq!(S::TunnelContinue as i32, 4);
    assert_eq!(S::TunnelFadeOut as i32, 5);
    assert_eq!(S::WorldFadeIn as i32, 6);
}

#[test]
fn snapshot_defaults_are_off_with_game_view_plane() {
    let snap = TeleportAnimSnapshot::default();
    assert_eq!(snap.state, S::Off);
    assert_eq!(snap.view_plane_blend, 0.0);
    assert!(!snap.show_tunnel && !snap.show_please_wait);
}

#[test]
fn reset_cancels_transition_and_discards_pending_edges() {
    let mut seq = begun(TeleportEntryKind::Portal);
    seq.reset();
    let (snap, events) = tick(&mut seq, 0.0, true);
    assert!(!seq.is_active());
    assert_eq!(snap.state, S::Off);
    assert_eq!(snap.view_plane_blend, 0.0);
    assert!(!snap.show_tunnel);
    assert!(events.is_empty());
}

#[test]
fn begin_sets_correct_start_state() {
    for (kind, start) in [
        (TeleportEntryKind::Portal, S::Tunnel),
        (TeleportEntryKind::Login, S::Tunnel),
        (TeleportEntryKind::Death, S::Tunnel),
        (TeleportEntryKind::Logout, S::WorldFadeOut),
    ] {
        let mut seq = TeleportAnimSequencer::new();
        assert!(!seq.is_active());
        seq.begin(kind);
        assert!(seq.is_active());
        assert_eq!(seq.state(), start, "{kind:?}");
    }
}

#[test]
fn begin_emits_play_enter_sound_on_first_tick() {
    let mut seq = begun(TeleportEntryKind::Portal);
    let (_, events) = tick(&mut seq, 0.0, false);
    assert!(events.contains(&E::PlayEnterSound));
}

#[test]
fn logout_after_fade_time_transitions_to_tunnel_fade_in() {
    let mut seq = begun(TeleportEntryKind::Logout);
    tick(&mut seq, 0.0, false);
    drive_seconds(&mut seq, FADE_TIME + 0.02, false);
    assert_eq!(seq.state(), S::TunnelFadeIn);
}

#[test]
fn logout_after_2x_fade_time_transitions_to_tunnel() {
    let mut seq = begun(TeleportEntryKind::Logout);
    tick(&mut seq, 0.0, false);
    drive_seconds(&mut seq, 2.0 * FADE_TIME + 0.02, false);
    assert_eq!(seq.state(), S::Tunnel);
}

#[test]
fn portal_starts_in_tunnel_holds_while_not_ready() {
    let mut seq = begun(TeleportEntryKind::Portal);
    tick(&mut seq, 0.0, false);
    drive_seconds(&mut seq, 10.0, false);
    assert_eq!(seq.state(), S::Tunnel);
}

#[test]
fn portal_when_world_ready_transitions_to_tunnel_continue_emits_place() {
    let mut seq = begun(TeleportEntryKind::Portal);
    tick(&mut seq, 0.0, false);
    let (_, events) = tick(&mut seq, 0.016, true);
    assert_eq!(seq.state(), S::TunnelContinue);
    assert!(events.contains(&E::Place));
}

#[test]
fn diagnostic_hold_keeps_ready_portal_in_stable_tunnel_until_released() {
    let mut seq = begun(TeleportEntryKind::Portal);
    let (_, held) = seq.tick(30.0, true, 72, true);
    assert_eq!(seq.state(), S::Tunnel);
    assert!(!held.contains(&E::Place));
    let (_, released) = seq.tick(0.0, true, 72, false);
    assert_eq!(seq.state(), S::TunnelContinue);
    assert!(released.contains(&E::Place));
}

#[test]
fn tunnel_continue_does_not_advance_before_min_continue() {
    let mut seq = begun(TeleportEntryKind::Portal);
    tick(&mut seq, 0.0, false);
    tick(&mut seq, 0.016, true);
    assert_eq!(seq.state(), S::TunnelContinue);
    drive_seconds(&mut seq, MIN_CONTINUE - 0.1, true);
    assert_eq!(seq.state(), S::TunnelContinue);
}

#[test]
fn tunnel_continue_advances_to_tunnel_fade_out_after_min_continue() {
    let mut seq = begun(TeleportEntryKind::Portal);
    tick(&mut seq, 0.0, false);
    tick(&mut seq, 0.016, true);
    drive_seconds(&mut seq, MIN_CONTINUE + 0.05, true);
    assert_eq!(seq.state(), S::TunnelFadeOut);
}

#[test]
fn tunnel_continue_forces_advance_at_max_continue_even_if_not_ready() {
    let mut seq = begun(TeleportEntryKind::Portal);
    tick(&mut seq, 0.0, false);
    tick(&mut seq, 0.016, true);
    drive(&mut seq, MAX_CONTINUE + 0.05, false, 0.016, 0);
    assert_eq!(seq.state(), S::TunnelFadeOut);
}

#[test]
fn tunnel_continue_uses_retail_animation_exit_window() {
    for (frame, should_fade) in [
        (68, false),
        (69, true),
        (72, true),
        (75, true),
        (76, false),
        (77, false),
        (121, false),
    ] {
        let mut seq = begun(TeleportEntryKind::Portal);
        seq.tick(0.0, false, frame, false);
        seq.tick(0.016, true, frame, false);
        drive(&mut seq, MIN_CONTINUE + 0.01, true, 0.016, frame);
        let expected = if should_fade {
            S::TunnelFadeOut
        } else {
            S::TunnelContinue
        };
        assert_eq!(seq.state(), expected, "frame {frame}");
    }
}

#[test]
fn retail_animation_level_matches_golden_table() {
    for (t, expected) in [
        (0.00, 0),
        (0.01, 0),
        (0.25, 146),
        (0.50, 512),
        (0.75, 877),
        (0.99, 1024),
        (1.00, 1024),
    ] {
        assert_eq!(retail_animation_level(t), expected, "t {t}");
    }
}

/// OpenAC's `AssertOutgoingViewportRetiresBeforeTerminal`.
fn assert_outgoing_viewport_retires_before_terminal(
    seq: &mut TeleportAnimSequencer,
    outgoing: TeleportAnimState,
    frames_per_second: f32,
) {
    let dt = 1.0 / frames_per_second;
    let mut transitioned = false;
    for _ in 0..(frames_per_second * 1.1).ceil() as i32 {
        let (snap, _) = tick(seq, dt, true);
        if snap.state == outgoing {
            assert!(
                snap.view_plane_blend <= f32::from(LAST_VISIBLE_OUTGOING_ANIMATION_LEVEL) / 1024.0,
                "published unsafe outgoing blend {}",
                snap.view_plane_blend
            );
            continue;
        }
        transitioned = true;
        break;
    }
    assert!(transitioned, "the outgoing viewport did not retire");
}

#[test]
fn world_fade_out_does_not_publish_terminal_projection_on_outgoing_world() {
    for fps in [240.0, 700.0, 2000.0] {
        let mut seq = begun(TeleportEntryKind::Logout);
        tick(&mut seq, 0.0, false);
        assert_outgoing_viewport_retires_before_terminal(&mut seq, S::WorldFadeOut, fps);
    }
}

#[test]
fn tunnel_fade_out_does_not_publish_terminal_projection_on_finite_tunnel() {
    for fps in [240.0f32, 700.0, 2000.0] {
        let mut seq = begun(TeleportEntryKind::Portal);
        tick(&mut seq, 0.0, false);
        tick(&mut seq, 0.016, true);
        drive(&mut seq, MIN_CONTINUE + 0.05, true, 1.0 / fps, 72);
        assert_eq!(seq.state(), S::TunnelFadeOut);
        assert_outgoing_viewport_retires_before_terminal(&mut seq, S::TunnelFadeOut, fps);
    }
}

/// The first of `t = i / 1000` whose level satisfies `pred`.
fn first_time(pred: impl Fn(i16) -> bool) -> f32 {
    (0..=1000)
        .map(|i| i as f32 / 1000.0)
        .find(|t| pred(retail_animation_level(*t)))
        .unwrap()
}

#[test]
fn tunnel_fade_out_swaps_after_last_full_viewport_capture_level() {
    assert_eq!(LAST_VISIBLE_OUTGOING_ANIMATION_LEVEL, 1001);
    let mut seq = begun(TeleportEntryKind::Portal);
    tick(&mut seq, 0.0, false);
    tick(&mut seq, 0.016, true);
    seq.tick(MIN_CONTINUE, true, 72, false);
    assert_eq!(seq.state(), S::TunnelFadeOut);
    let last_safe = first_time(|l| l == LAST_VISIBLE_OUTGOING_ANIMATION_LEVEL);
    let first_unsafe = first_time(|l| l > LAST_VISIBLE_OUTGOING_ANIMATION_LEVEL);
    let (safe, _) = tick(&mut seq, last_safe, true);
    assert_eq!(safe.state, S::TunnelFadeOut);
    assert_eq!(
        safe.view_plane_blend,
        f32::from(LAST_VISIBLE_OUTGOING_ANIMATION_LEVEL) / 1024.0
    );
    let (swapped, events) = tick(&mut seq, first_unsafe - last_safe, true);
    assert_eq!(swapped.state, S::WorldFadeIn);
    assert!(!swapped.show_tunnel);
    assert!(events.contains(&E::PlayExitSound));
}

#[test]
fn tunnel_fade_out_emits_play_exit_sound_on_transition_to_world_fade_in() {
    let mut seq = begun(TeleportEntryKind::Portal);
    tick(&mut seq, 0.0, false);
    tick(&mut seq, 0.016, true);
    drive_seconds(&mut seq, MIN_CONTINUE + 0.05, true);
    assert_eq!(seq.state(), S::TunnelFadeOut);
    let (_, events) = drive_seconds(&mut seq, FADE_TIME + 0.05, true);
    assert_eq!(seq.state(), S::WorldFadeIn);
    assert!(events.contains(&E::PlayExitSound));
}

#[test]
fn world_fade_in_emits_fire_login_complete_on_transition_to_off() {
    let mut seq = begun(TeleportEntryKind::Portal);
    tick(&mut seq, 0.0, false);
    tick(&mut seq, 0.016, true);
    drive_seconds(&mut seq, MIN_CONTINUE + 0.05, true);
    drive_seconds(&mut seq, FADE_TIME + 0.05, true);
    let (_, events) = drive_seconds(&mut seq, FADE_TIME + 0.05, true);
    assert_eq!(seq.state(), S::Off);
    assert!(!seq.is_active());
    assert!(events.contains(&E::FireLoginComplete));
}

#[test]
fn view_plane_blend_is_zero_at_start_of_world_fade_out() {
    let mut seq = begun(TeleportEntryKind::Logout);
    let (snap, _) = tick(&mut seq, 0.0, false);
    assert!(snap.view_plane_blend.abs() < 1e-4);
}

#[test]
fn view_plane_blend_reaches_last_visible_level_before_world_fade_out_retires() {
    let mut seq = begun(TeleportEntryKind::Logout);
    tick(&mut seq, 0.0, false);
    let last_safe = first_time(|l| l == LAST_VISIBLE_OUTGOING_ANIMATION_LEVEL);
    tick(&mut seq, last_safe, false);
    assert_eq!(seq.state(), S::WorldFadeOut);
    let (snap, _) = tick(&mut seq, 0.0, false);
    assert!(snap.view_plane_blend > 0.95, "{}", snap.view_plane_blend);
    assert!(snap.view_plane_blend <= f32::from(LAST_VISIBLE_OUTGOING_ANIMATION_LEVEL) / 1024.0);
}

#[test]
fn view_plane_blend_is_monotonically_increasing_during_world_fade_out() {
    let mut seq = begun(TeleportEntryKind::Logout);
    tick(&mut seq, 0.0, false);
    let mut previous = -1.0;
    let step = FADE_TIME / 50.0;
    for i in 0..48 {
        let (snap, _) = tick(&mut seq, step, false);
        if seq.state() != S::WorldFadeOut {
            break;
        }
        assert!(snap.view_plane_blend >= previous - 0.001, "step {i}");
        previous = snap.view_plane_blend;
    }
}

#[test]
fn view_plane_blend_is_zero_during_stable_tunnel_states() {
    let mut seq = begun(TeleportEntryKind::Portal);
    tick(&mut seq, 0.0, false);
    let (snap, _) = tick(&mut seq, 0.016, false);
    assert_eq!(seq.state(), S::Tunnel);
    assert_eq!(snap.view_plane_blend, 0.0);
}

#[test]
fn show_tunnel_true_in_tunnel_family_states_false_otherwise() {
    let mut seq = begun(TeleportEntryKind::Logout);
    tick(&mut seq, 0.0, false);
    let (snap, _) = tick(&mut seq, 0.016, false);
    assert_eq!(seq.state(), S::WorldFadeOut);
    assert!(!snap.show_tunnel);
    drive_seconds(&mut seq, FADE_TIME, false);
    assert_eq!(seq.state(), S::TunnelFadeIn);
    assert!(tick(&mut seq, 0.0, false).0.show_tunnel);
    drive_seconds(&mut seq, FADE_TIME, false);
    assert_eq!(seq.state(), S::Tunnel);
    assert!(tick(&mut seq, 0.0, false).0.show_tunnel);
}

#[test]
fn logout_enters_portal_viewport_on_first_tunnel_fade_in_tick() {
    let mut seq = begun(TeleportEntryKind::Logout);
    let (crossing, crossing_events) = tick(&mut seq, FADE_TIME, false);
    assert_eq!(crossing.state, S::TunnelFadeIn);
    assert!(crossing.show_tunnel);
    assert!(!crossing_events.contains(&E::EnterTunnel));
    let (first, first_events) = tick(&mut seq, 0.0, false);
    assert_eq!(first.state, S::TunnelFadeIn);
    assert!(first_events.contains(&E::EnterTunnel));
    let (_, tunnel_events) = tick(&mut seq, FADE_TIME, false);
    assert_eq!(seq.state(), S::Tunnel);
    assert!(!tunnel_events.contains(&E::EnterTunnel));
}

#[test]
fn show_please_wait_true_only_in_tunnel_continue() {
    let mut seq = begun(TeleportEntryKind::Portal);
    tick(&mut seq, 0.0, false);
    let (snap1, _) = tick(&mut seq, 0.016, false);
    assert_eq!(seq.state(), S::Tunnel);
    assert!(!snap1.show_please_wait);
    let (snap2, _) = tick(&mut seq, 0.016, true);
    assert_eq!(seq.state(), S::TunnelContinue);
    assert!(snap2.show_please_wait);
}

/// OpenAC's `RunToOff`: ready on each tick in the tunnel.
fn run_to_off(kind: TeleportEntryKind) -> Vec<E> {
    let mut seq = begun(kind);
    let mut all = Vec::new();
    let mut safety = 5000;
    while seq.is_active() && safety > 0 {
        safety -= 1;
        let ready = seq.state() == S::Tunnel;
        let (_, events) = tick(&mut seq, 0.05, ready);
        all.extend(events);
    }
    all
}

fn index_of(events: &[E], event: E) -> usize {
    events.iter().position(|e| *e == event).unwrap()
}

#[test]
fn portal_full_sequence_events_in_order() {
    let events = run_to_off(TeleportEntryKind::Portal);
    let enter = index_of(&events, E::PlayEnterSound);
    let place = index_of(&events, E::Place);
    let exit = index_of(&events, E::PlayExitSound);
    let login = index_of(&events, E::FireLoginComplete);
    assert!(enter < place && place < exit && exit < login);
}

#[test]
fn logout_full_sequence_events_in_order_with_enter_tunnel_after_fades() {
    let events = run_to_off(TeleportEntryKind::Logout);
    let enter = index_of(&events, E::PlayEnterSound);
    let tunnel = index_of(&events, E::EnterTunnel);
    let place = index_of(&events, E::Place);
    let exit = index_of(&events, E::PlayExitSound);
    let login = index_of(&events, E::FireLoginComplete);
    assert!(enter < tunnel && tunnel < place && place < exit && exit < login);
}

#[test]
fn portal_no_events_fired_twice() {
    let events = run_to_off(TeleportEntryKind::Portal);
    for event in [
        E::PlayEnterSound,
        E::Place,
        E::PlayExitSound,
        E::FireLoginComplete,
    ] {
        assert_eq!(
            events.iter().filter(|e| **e == event).count(),
            1,
            "{event:?}"
        );
    }
}

#[test]
fn death_and_login_behave_identically_to_portal() {
    let portal = run_to_off(TeleportEntryKind::Portal);
    assert_eq!(run_to_off(TeleportEntryKind::Death), portal);
    assert_eq!(run_to_off(TeleportEntryKind::Login), portal);
}

#[test]
fn after_off_is_active_is_false_and_state_is_off() {
    let mut seq = begun(TeleportEntryKind::Portal);
    for _ in 0..5000 {
        if !seq.is_active() {
            break;
        }
        let ready = seq.state() == S::Tunnel;
        tick(&mut seq, 0.05, ready);
    }
    assert!(!seq.is_active());
    assert_eq!(seq.state(), S::Off);
}

#[test]
fn portal_emits_enter_tunnel_on_first_tick() {
    let mut seq = begun(TeleportEntryKind::Portal);
    let (_, events) = tick(&mut seq, 0.0, false);
    assert!(events.contains(&E::PlayEnterSound));
    assert!(events.contains(&E::EnterTunnel));
}
