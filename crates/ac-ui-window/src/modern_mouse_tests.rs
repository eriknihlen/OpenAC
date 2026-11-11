//! Modern mouse turning through the window's gameplay input: the drag
//! begins only over the world, goes on across the interface, turns the
//! view at once while the session turns the character, finishes the turn
//! and hands the camera back without a jump; both buttons run forward.

use ac_runtime::movement::{MovementCommand, MovementKeys};
use ac_ui::Key;
use ac_ui::input::chord::mouse;
use glam::Vec3;

use super::modern_mouse::{SETTLE_GIVE_UP_SECONDS, YAW_RADIANS_PER_PIXEL};
use super::tests::Rig;
use super::*;
use crate::camera::{ChaseSubject, ChaseTuning};

const DT: f32 = 1.0 / 60.0;

fn modern(both: bool) -> Rig {
    let mut r = Rig::new();
    r.cameras.chase.tuning.modern_mouse_turning = true;
    r.cameras.chase.tuning.both_mouse_buttons_run_forward = both;
    r
}

/// The right button down over the world, and the first motion after the
/// cursor is taken (which is dropped).
fn start_drag(r: &mut Rig) {
    r.button(mouse::RIGHT, true);
    raw(r, 0.0, 0.0);
}

/// The character faces `yaw` this frame (the camera follows it).
fn face(r: &mut Rig, yaw: f32) {
    r.cameras
        .chase
        .update(&ChaseSubject::standing(Vec3::ZERO, yaw), DT, None);
}

fn raw(r: &mut Rig, dx: f32, dy: f32) {
    r.input.raw_motion(dx, dy, Some(&mut r.cameras));
}

fn commands(out: &[GameplayOutput]) -> Vec<MovementCommand> {
    out.iter()
        .filter_map(|o| match o {
            GameplayOutput::Movement(c) => Some(*c),
            _ => None,
        })
        .collect()
}

fn views(out: &[GameplayOutput]) -> Vec<f32> {
    commands(out)
        .into_iter()
        .filter_map(|c| match c {
            MovementCommand::MouseLookFacing(yaw) => Some(yaw),
            _ => None,
        })
        .collect()
}

fn ended(out: &[GameplayOutput]) -> bool {
    commands(out).contains(&MovementCommand::EndMouseLook)
}

fn keys_sent(out: &[GameplayOutput]) -> Vec<MovementKeys> {
    commands(out)
        .into_iter()
        .filter_map(|c| match c {
            MovementCommand::Keys(k) => Some(k),
            _ => None,
        })
        .collect()
}

fn look_yaw(r: &Rig) -> f32 {
    let f = r.cameras.chase.forward();
    f.y.atan2(f.x)
}

#[test]
fn off_by_default_the_right_button_orbits_as_before() {
    let mut r = Rig::new();
    assert!(!r.cameras.chase.tuning.modern_mouse_turning);
    r.button(mouse::RIGHT, true);
    r.tick(DT);
    assert!(r.input.rmb_orbit_held());
    assert!(!r.input.holds_cursor());
    let out = r.take();
    assert!(commands(&out).is_empty(), "{out:?}");
    assert!(!out.contains(&GameplayOutput::GrabCursor));
    raw(&mut r, 100.0, 0.0);
    assert_eq!(r.cameras.chase.held_view_yaw(), None);
}

#[test]
fn on_but_without_a_drag_the_camera_is_the_ordinary_camera() {
    let mut r = modern(false);
    r.key(Key::Keypad4, true);
    r.tick(0.1);
    assert!(r.cameras.chase.yaw_offset < 0.0, "the orbit keys swing it");
    r.key(Key::Keypad4, false);
    assert_eq!(r.cameras.chase.held_view_yaw(), None);
    assert!(
        commands(&r.take())
            .iter()
            .all(|c| matches!(c, MovementCommand::Keys(_)))
    );
}

#[test]
fn turning_the_option_on_or_off_puts_the_swing_back_behind() {
    let mut r = Rig::new();
    r.cameras.chase.yaw_offset = 0.7;
    let mut tuning = r.cameras.chase.tuning;
    r.cameras.chase.set_tuning(tuning);
    assert_eq!(r.cameras.chase.yaw_offset, 0.7, "unchanged option keeps it");
    tuning.modern_mouse_turning = true;
    r.cameras.chase.set_tuning(tuning);
    assert_eq!(r.cameras.chase.yaw_offset, 0.0);
    r.cameras.chase.yaw_offset = -0.4;
    r.cameras.chase.set_tuning(ChaseTuning {
        modern_mouse_turning: false,
        ..tuning
    });
    assert_eq!(r.cameras.chase.yaw_offset, 0.0);
}

#[test]
fn a_right_press_over_the_world_begins_a_modern_mouse_look_holding_the_cursor() {
    let mut r = modern(false);
    r.cameras.chase.yaw_offset = 0.25;
    r.button(mouse::RIGHT, true);
    assert!(r.input.modern_drag_active());
    assert!(r.input.holds_cursor(), "the interface gets no pointer");
    assert!(!r.input.rmb_orbit_held(), "the orbit does not take it");
    let out = r.take();
    assert!(out.contains(&GameplayOutput::GrabCursor));
    // The view starts at the character's facing plus the swing.
    assert_eq!(
        commands(&out),
        [
            MovementCommand::BeginModernMouseLook,
            MovementCommand::MouseLookFacing(0.25)
        ]
    );
}

#[test]
fn a_right_press_over_the_interface_starts_nothing() {
    let mut r = modern(false);
    r.want_mouse = true;
    r.button(mouse::RIGHT, true);
    r.tick(DT);
    assert!(!r.input.modern_drag_active());
    assert!(!r.input.holds_cursor());
    assert_eq!(r.cameras.chase.held_view_yaw(), None);
    assert!(commands(&r.take()).is_empty());
}

#[test]
fn the_first_motion_after_taking_the_cursor_is_dropped() {
    let mut r = modern(false);
    r.button(mouse::RIGHT, true);
    raw(&mut r, 500.0, 0.0);
    assert_eq!(r.cameras.chase.held_view_yaw(), Some(0.0));
    raw(&mut r, 100.0, 0.0);
    let turned = -r.cameras.chase.held_view_yaw().unwrap();
    let expected = 100.0 * YAW_RADIANS_PER_PIXEL * r.cameras.chase.tuning.mouse_look_sensitivity;
    assert!((turned - expected).abs() < 1e-6, "{turned} {expected}");
}

#[test]
fn the_drag_goes_on_across_the_interface() {
    let mut r = modern(false);
    start_drag(&mut r);
    r.tick(DT);
    r.take();
    // The pointer would be over a panel now.
    r.want_mouse = true;
    let state = r.state();
    r.input
        .pointer_moved(40.0, 0.0, &state, Some(&mut r.cameras), r.now);
    r.tick(DT);
    assert!(r.input.modern_drag_active(), "still dragging");
    raw(&mut r, 100.0, 0.0);
    let view = r.cameras.chase.held_view_yaw().unwrap();
    assert!(view < 0.0, "moving right turns the view right");
    r.tick(DT);
    assert_eq!(views(&r.take()), [view], "the new view goes to the session");
}

/// The camera looks where the mouse turned it at once, and stays there
/// while the character turns toward it: it never waits on the character.
#[test]
fn the_view_turns_with_the_mouse_at_once_and_holds_while_the_character_turns() {
    let mut r = modern(false);
    start_drag(&mut r);
    raw(&mut r, 300.0, 0.0);
    let view = r.cameras.chase.held_view_yaw().unwrap();
    assert!(view < -0.3, "a real turn: {view}");
    face(&mut r, 0.0);
    assert!(
        (look_yaw(&r) - view).abs() < 1e-3,
        "the camera looks along the view this very frame: {} {view}",
        look_yaw(&r)
    );
    for step in 1..=5 {
        face(&mut r, view * step as f32 / 5.0);
        assert!((look_yaw(&r) - view).abs() < 1e-3);
    }
    // Vertical motion raises the boom; the orbit keys leave the view alone.
    let pitch = r.cameras.chase.pitch;
    raw(&mut r, 0.0, 20.0);
    assert!(r.cameras.chase.pitch > pitch);
    r.cameras.chase.adjust_yaw(3.0);
    assert_eq!(r.cameras.chase.held_view_yaw(), Some(view));
}

#[test]
fn letting_go_finishes_the_turn_then_the_camera_follows_without_a_jump() {
    let mut r = modern(false);
    start_drag(&mut r);
    raw(&mut r, 200.0, 0.0);
    r.tick(DT);
    r.take();
    let view = r.cameras.chase.held_view_yaw().unwrap();
    r.button(mouse::RIGHT, false);
    assert!(!r.input.holds_cursor(), "the cursor comes back");
    assert!(r.take().contains(&GameplayOutput::ReleaseCursor));
    // The character is still turning: the view stays where it points.
    for i in 0..10 {
        face(&mut r, view * i as f32 / 10.0);
        r.tick(DT);
        assert_eq!(r.cameras.chase.held_view_yaw(), Some(view));
        assert!(!ended(&r.take()));
    }
    // It lands on the view: the modern mouse look ends, the camera is the
    // ordinary camera with no swing left.
    face(&mut r, view);
    r.tick(DT);
    assert!(ended(&r.take()));
    assert_eq!(r.cameras.chase.held_view_yaw(), None);
    assert!(r.cameras.chase.yaw_offset.abs() < 1e-4, "no jump");
    face(&mut r, view + 0.3);
    assert!(
        (look_yaw(&r) - view).abs() > 0.01,
        "the camera follows the character again"
    );
}

#[test]
fn a_character_that_cannot_turn_does_not_keep_the_view_forever() {
    let mut r = modern(false);
    start_drag(&mut r);
    raw(&mut r, 200.0, 0.0);
    r.tick(DT);
    r.button(mouse::RIGHT, false);
    r.take();
    let frames = (SETTLE_GIVE_UP_SECONDS / DT) as usize + 2;
    let mut done = false;
    for _ in 0..frames {
        face(&mut r, 0.0);
        r.tick(DT);
        done |= ended(&r.take());
    }
    assert!(done);
    assert_eq!(r.cameras.chase.held_view_yaw(), None);
}

#[test]
fn a_new_press_during_the_finishing_turn_takes_the_view_up_where_it_is() {
    let mut r = modern(false);
    start_drag(&mut r);
    raw(&mut r, 200.0, 0.0);
    r.tick(DT);
    r.button(mouse::RIGHT, false);
    face(&mut r, -0.1);
    r.tick(DT);
    r.take();
    let view = r.cameras.chase.held_view_yaw().unwrap();
    r.button(mouse::RIGHT, true);
    let out = r.take();
    assert!(
        !commands(&out).contains(&MovementCommand::BeginModernMouseLook),
        "the turn carries on: {out:?}"
    );
    assert!(r.input.modern_drag_active());
    assert_eq!(r.cameras.chase.held_view_yaw(), Some(view));
}

#[test]
fn a_turn_key_ends_the_finishing_turn_at_once() {
    let mut r = modern(false);
    start_drag(&mut r);
    raw(&mut r, 100.0, 0.0);
    r.tick(DT);
    r.button(mouse::RIGHT, false);
    r.take();
    r.key(Key::A, true);
    r.tick(DT);
    assert!(ended(&r.take()));
    assert_eq!(r.cameras.chase.held_view_yaw(), None);
}

#[test]
fn a_turn_key_during_the_drag_turns_the_character_as_usual() {
    let mut r = modern(false);
    start_drag(&mut r);
    r.tick(DT);
    r.take();
    r.key(Key::D, true);
    r.tick(DT);
    let out = r.take();
    assert!(
        keys_sent(&out).iter().any(|k| k.turn_right && !k.turn_left),
        "{out:?}"
    );
    assert!(!ended(&out));
    assert!(r.input.modern_drag_active(), "the drag goes on");
}

#[test]
fn turning_the_option_off_mid_drag_ends_it() {
    let mut r = modern(false);
    start_drag(&mut r);
    r.tick(DT);
    r.take();
    r.cameras.chase.tuning.modern_mouse_turning = false;
    r.tick(DT);
    let out = r.take();
    assert!(ended(&out));
    assert!(out.contains(&GameplayOutput::ReleaseCursor));
    assert!(!r.input.holds_cursor());
    assert_eq!(r.cameras.chase.held_view_yaw(), None);
}

#[test]
fn leaving_the_chase_camera_ends_it() {
    let mut r = modern(false);
    start_drag(&mut r);
    r.tick(DT);
    r.take();
    r.cameras.toggle_fly();
    r.tick(DT);
    assert!(ended(&r.take()));
    assert!(!r.input.modern_drag_active());
}

#[test]
fn losing_the_focus_ends_the_drag() {
    let mut r = modern(false);
    start_drag(&mut r);
    r.tick(DT);
    r.take();
    r.input.focus_lost(&mut r.out);
    let out = r.take();
    assert!(ended(&out));
    assert!(out.contains(&GameplayOutput::ReleaseCursor));
    r.buttons = 0;
    r.tick(DT);
    assert_eq!(r.cameras.chase.held_view_yaw(), None);
}

#[test]
fn mouse_look_and_the_drag_do_not_share_the_mouse() {
    let mut r = modern(false);
    r.button(mouse::RIGHT, true);
    r.button(mouse::MIDDLE, true);
    assert!(!r.input.mouse_look_active(), "the drag keeps the mouse");
    let mut r = modern(false);
    r.button(mouse::MIDDLE, true);
    assert!(r.input.mouse_look_active());
    r.button(mouse::RIGHT, true);
    assert!(!r.input.modern_drag_active(), "mouse look keeps the mouse");
}

#[test]
fn a_drag_selects_nothing_but_a_still_right_click_still_examines() {
    use crate::world_click::WorldClick;
    let clicks = |out: Vec<GameplayOutput>| -> Vec<WorldClick> {
        out.into_iter()
            .filter_map(|o| match o {
                GameplayOutput::WorldClick(c) => Some(c),
                _ => None,
            })
            .collect()
    };
    let mut r = modern(false);
    start_drag(&mut r);
    raw(&mut r, 30.0, 0.0);
    r.button(mouse::RIGHT, false);
    assert!(clicks(r.take()).is_empty(), "the drag examined nothing");
    r.tick(DT);
    r.now += 2.0;
    r.button(mouse::RIGHT, true);
    r.button(mouse::RIGHT, false);
    assert_eq!(clicks(r.take()), [WorldClick::Examine]);
}

#[test]
fn both_buttons_run_forward_and_letting_go_of_either_stops() {
    let mut r = modern(true);
    r.button(mouse::RIGHT, true);
    r.button(mouse::LEFT, true);
    r.tick(DT);
    assert!(keys_sent(&r.take()).last().is_some_and(|k| k.forward));
    r.button(mouse::LEFT, false);
    r.tick(DT);
    assert!(keys_sent(&r.take()).last().is_some_and(|k| !k.forward));
    r.button(mouse::LEFT, true);
    r.tick(DT);
    assert!(keys_sent(&r.take()).last().is_some_and(|k| k.forward));
    r.button(mouse::RIGHT, false);
    r.tick(DT);
    assert!(keys_sent(&r.take()).last().is_some_and(|k| !k.forward));
}

#[test]
fn the_back_up_key_cancels_both_buttons_running_until_they_are_pressed_again() {
    let mut r = modern(true);
    r.button(mouse::RIGHT, true);
    r.button(mouse::LEFT, true);
    r.tick(DT);
    r.take();
    r.key(Key::X, true);
    r.tick(DT);
    let k = *keys_sent(&r.take()).last().unwrap();
    assert!(!k.forward && k.backward, "backs up instead: {k:?}");
    r.key(Key::X, false);
    r.tick(DT);
    let k = *keys_sent(&r.take()).last().unwrap();
    assert!(!k.forward, "stays cancelled while both are held: {k:?}");
    r.button(mouse::LEFT, false);
    r.tick(DT);
    r.button(mouse::LEFT, true);
    r.tick(DT);
    assert!(keys_sent(&r.take()).last().is_some_and(|k| k.forward));
}

#[test]
fn both_buttons_run_only_with_both_options_on_and_a_drag() {
    // The run option off: both buttons do not run.
    let mut r = modern(false);
    r.button(mouse::RIGHT, true);
    r.button(mouse::LEFT, true);
    r.tick(DT);
    assert!(keys_sent(&r.take()).iter().all(|k| !k.forward));
    // Modern mouse turning off: the run option alone does nothing.
    let mut r = Rig::new();
    r.cameras.chase.tuning.both_mouse_buttons_run_forward = true;
    r.button(mouse::RIGHT, true);
    r.button(mouse::LEFT, true);
    r.tick(DT);
    assert!(keys_sent(&r.take()).iter().all(|k| !k.forward));
    // The right button pressed over the interface: no drag, no run.
    let mut r = modern(true);
    r.want_mouse = true;
    r.button(mouse::RIGHT, true);
    r.want_mouse = false;
    r.button(mouse::LEFT, true);
    r.tick(DT);
    assert!(keys_sent(&r.take()).iter().all(|k| !k.forward));
}
