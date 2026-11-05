//! Input routing of the UI root.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiRootInputTests.cs`,
//! `UiWindowBorderRegionTests.cs`, `UiRootFixedCanvasTests.cs`,
//! `UiElementChildOrderTests.cs`, `UiLayoutPolicyTests.cs` (element cases),
//! `UiAncestorClipTests.cs` (hit and clip cases) and the root-level cases of
//! `DragDropSpineTests.cs`. OpenAC's `UiPanel` is [`Panel`]; the routing
//! cases use the minimal `CharField` test widget where OpenAC uses its
//! `UiField` (the field's own cases are in `field.rs`); callbacks are
//! widget messages read from the root's output.

mod common;

use ac_ui::draw::{DrawCmd, NoSprites, TextureRef};
use ac_ui::event::{Key, Modifiers, MouseButton};
use ac_ui::geometry::{AnchorEdges, PixelRect, ResizeEdges};
use ac_ui::layout_policy::LayoutPolicy;
use ac_ui::root::{ElementMessage, UiOutput, UiRoot, hit_edges};
use ac_ui::tree::{DragGhost, Element};
use ac_ui::widgets::{Button, Panel};
use common::*;

use MouseButton::{Left, Right};

const NONE: Modifiers = Modifiers::NONE;

fn click(ui: &mut UiRoot, x: i32, y: i32) {
    ui.on_mouse_down(Left, x, y, 0);
    ui.on_mouse_up(Left, x, y, 0);
}

fn rect(ui: &UiRoot, id: ac_ui::ElementId) -> (f32, f32, f32, f32) {
    let e = ui.element(id).unwrap();
    (e.left, e.top, e.width, e.height)
}

// ── Keyboard: semantic activation and its char tail ─────────────────────

#[test]
fn keypad_enter_does_not_use_the_default_text_input_fallback() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let field = ui.add(r, CharField::element(100.0, 20.0), CharField::default());
    ui.default_text_input = Some(field);
    ui.on_key_down(Key::KeypadEnter, NONE);
    assert_eq!(ui.keyboard_focus(), None);
    ui.on_key_down(Key::Enter, NONE);
    assert_eq!(ui.keyboard_focus(), Some(field));
}

#[test]
fn activation_on_a_printable_key_swallows_only_its_own_char() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let field = ui.add(
        r,
        CharField::element(100.0, 20.0),
        CharField {
            text: "hello".into(),
            ..CharField::default()
        },
    );
    ui.set_keyboard_focus(Some(field));
    ui.suppress_physical_key_until_release(Key::A);

    ui.on_key_down(Key::A, NONE);
    ui.on_char('a');
    assert_eq!(ui.widget::<CharField>(field).unwrap().text, "hello");

    ui.on_key_down(Key::Slash, NONE);
    ui.on_char('/');
    assert_eq!(ui.widget::<CharField>(field).unwrap().text, "hello/");

    ui.on_key_up(Key::A, NONE);
    ui.on_key_down(Key::A, NONE);
    ui.on_char('a');
    assert_eq!(ui.widget::<CharField>(field).unwrap().text, "hello/a");
}

#[test]
fn activation_on_a_key_without_a_char_does_not_eat_the_next_char() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let field = ui.add(r, CharField::element(100.0, 20.0), CharField::default());
    ui.set_keyboard_focus(Some(field));
    ui.suppress_physical_key_until_release(Key::F1);
    ui.on_key_down(Key::F1, NONE);
    ui.on_key_down(Key::A, NONE);
    ui.on_char('a');
    assert_eq!(ui.widget::<CharField>(field).unwrap().text, "a");
}

#[test]
fn activation_suppresses_the_enter_tail_but_not_fast_typing() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let field = ui.add(
        r,
        CharField::element(100.0, 20.0),
        CharField {
            text: "hello".into(),
            ..CharField::default()
        },
    );
    ui.default_text_input = Some(field);
    ui.set_keyboard_focus(Some(field));
    ui.suppress_physical_key_until_release(Key::Enter);

    ui.on_key_down(Key::Enter, NONE);
    ui.on_char('\r');
    let f = ui.widget::<CharField>(field).unwrap();
    assert_eq!((f.submissions, f.text.as_str()), (0, "hello"));
    assert_eq!(ui.keyboard_focus(), Some(field));

    ui.on_char('/');
    assert_eq!(ui.widget::<CharField>(field).unwrap().text, "hello/");
    ui.on_key_up(Key::Enter, NONE);
    ui.on_char('x');
    assert_eq!(ui.widget::<CharField>(field).unwrap().text, "hello/x");
}

#[test]
fn unhandled_keys_fall_through_to_the_world() {
    let mut ui = root(800.0, 600.0);
    ui.on_key_down(Key::W, Modifiers::SHIFT);
    assert!(matches!(
        ui.drain_output().as_slice(),
        [UiOutput::WorldKey {
            key: Key::W,
            modifiers: Modifiers::SHIFT
        }]
    ));
}

// ── Clicks ──────────────────────────────────────────────────────────────

#[test]
fn a_clicking_widget_inside_a_draggable_window_still_gets_the_click() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let frame = ui.add(r, window(10.0, 300.0, 200.0, 60.0), Panel::default());
    let btn = ui.add(
        frame,
        at(5.0, 5.0, 120.0, 14.0),
        ClickRecorder {
            handles_click: true,
            ..ClickRecorder::default()
        },
    );
    click(&mut ui, 20, 310);
    assert_eq!(ui.widget::<ClickRecorder>(btn).unwrap().clicks, 1);
}

#[test]
fn a_plain_widget_inside_a_draggable_window_gets_no_click() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let frame = ui.add(r, window(10.0, 300.0, 200.0, 60.0), Panel::default());
    let plain = ui.add(frame, at(5.0, 5.0, 120.0, 14.0), ClickRecorder::default());
    click(&mut ui, 20, 310);
    assert_eq!(ui.widget::<ClickRecorder>(plain).unwrap().clicks, 0);
}

#[test]
fn a_second_click_within_the_delay_is_a_double_click() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let btn = ui.add(
        r,
        at(5.0, 5.0, 120.0, 14.0),
        ClickRecorder {
            handles_click: true,
            ..ClickRecorder::default()
        },
    );
    ui.tick(0.0, 1_000);
    click(&mut ui, 20, 10);
    ui.tick(0.0, 1_300);
    click(&mut ui, 20, 10);
    let b = ui.widget::<ClickRecorder>(btn).unwrap();
    assert_eq!((b.clicks, b.double_clicks), (2, 1));
    ui.tick(0.0, 2_000);
    click(&mut ui, 20, 10);
    assert_eq!(ui.widget::<ClickRecorder>(btn).unwrap().double_clicks, 1);
}

#[test]
fn a_double_click_survives_the_click_hiding_the_window() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let win = ui.add(r, at(0.0, 0.0, 200.0, 100.0), Panel::default());
    let btn = ui.add(
        win,
        at(0.0, 0.0, 120.0, 20.0),
        ClickRecorder {
            handles_click: true,
            ..ClickRecorder::default()
        },
    );
    ui.register_window("test", win, None, 0).unwrap();
    ui.tick(0.0, 1_000);
    click(&mut ui, 10, 10);
    ui.widget_mut::<ClickRecorder>(btn).unwrap().action = Some(Box::new(|ui: &mut UiRoot| {
        ui.hide_window("test");
    }));
    ui.tick(0.0, 1_300);
    click(&mut ui, 10, 10);
    assert!(!ui.element(win).unwrap().visible());
    assert_eq!(ui.captured(), None);
    let b = ui.widget::<ClickRecorder>(btn).unwrap();
    assert_eq!((b.clicks, b.double_clicks), (2, 1));
}

#[test]
fn a_click_that_moves_capture_keeps_the_new_owner() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let btn = ui.add(
        r,
        at(0.0, 0.0, 120.0, 20.0),
        ClickRecorder {
            handles_click: true,
            ..ClickRecorder::default()
        },
    );
    let modal = ui.add(r, at(200.0, 0.0, 100.0, 100.0), Panel::default());
    ui.widget_mut::<ClickRecorder>(btn).unwrap().action =
        Some(Box::new(move |ui: &mut UiRoot| ui.set_capture(modal)));
    click(&mut ui, 10, 10);
    assert_eq!(ui.captured(), Some(modal));
}

#[test]
fn press_and_move_deliver_the_same_target_local_frame() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let p = ui.add(r, at(50.0, 60.0, 200.0, 100.0), Panel::default());
    let child = ui.add(
        p,
        Element {
            captures_pointer_drag: true,
            ..at(8.0, 8.0, 150.0, 80.0)
        },
        CoordRecorder::default(),
    );
    ui.on_mouse_down(Left, 100, 100, 0);
    assert_eq!(
        ui.widget::<CoordRecorder>(child).unwrap().down,
        Some((42, 32))
    );
    ui.on_mouse_move(120, 110);
    assert_eq!(
        ui.widget::<CoordRecorder>(child).unwrap().moved,
        Some((62, 42))
    );
}

#[test]
fn presses_over_nothing_fall_through_to_the_world() {
    let mut ui = root(800.0, 600.0);
    ui.on_mouse_down(Right, 400, 300, 7);
    ui.on_mouse_up(Right, 400, 300, 7);
    let out = ui.drain_output();
    assert!(matches!(
        out.as_slice(),
        [
            UiOutput::WorldMouse {
                button: Right,
                down: true,
                x: 400,
                y: 300,
                flags: 7
            },
            UiOutput::WorldMouse {
                button: Right,
                down: false,
                ..
            }
        ]
    ));
}

#[test]
fn wants_mouse_over_a_widget_but_not_over_empty_space() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    panel(&mut ui, r, at(10.0, 10.0, 100.0, 50.0));
    ui.on_mouse_move(50, 30);
    assert!(ui.wants_mouse());
    ui.on_mouse_move(500, 400);
    assert!(!ui.wants_mouse());
}

// ── Window move ─────────────────────────────────────────────────────────

#[test]
fn a_draggable_window_follows_the_pointer_until_release() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let p = ui.add(r, window(10.0, 10.0, 100.0, 50.0), Panel::default());
    ui.on_mouse_down(Left, 20, 20, 0);
    ui.on_mouse_move(120, 90);
    assert_eq!(rect(&ui, p).0, 110.0);
    assert_eq!(rect(&ui, p).1, 80.0);
    ui.on_mouse_up(Left, 120, 90, 0);
    ui.on_mouse_move(300, 300);
    assert_eq!((rect(&ui, p).0, rect(&ui, p).1), (110.0, 80.0));
}

fn window_with_handle(ui: &mut UiRoot, el: Element) -> ac_ui::ElementId {
    let r = ui.root();
    let w = ui.add(r, el, Panel::default());
    ui.add(
        w,
        Element {
            window_move_handle: true,
            ..at(5.0, 0.0, 600.0, 5.0)
        },
        Panel::default(),
    );
    w
}

#[test]
fn a_drag_handle_moves_a_non_draggable_window() {
    let mut ui = root(800.0, 600.0);
    let w = window_with_handle(&mut ui, at(100.0, 500.0, 610.0, 90.0));
    ui.on_mouse_down(Left, 110, 502, 0);
    assert!(ui.is_window_move_active());
    ui.on_mouse_move(160, 452);
    assert_eq!((rect(&ui, w).0, rect(&ui, w).1), (150.0, 450.0));
    ui.on_mouse_up(Left, 160, 452, 0);
    ui.on_mouse_move(300, 300);
    assert_eq!((rect(&ui, w).0, rect(&ui, w).1), (150.0, 450.0));
}

#[test]
fn a_drag_keeps_every_window_inside_its_parent() {
    for (dx, dy, left, top) in [
        (-500, 0, 0.0, 100.0),
        (2000, 0, 500.0, 100.0),
        (0, -500, 100.0, 0.0),
        (0, 2000, 100.0, 450.0),
    ] {
        let mut ui = root(800.0, 600.0);
        let r = ui.root();
        let w = ui.add(r, window(100.0, 100.0, 300.0, 150.0), Panel::default());
        ui.on_mouse_down(Left, 110, 110, 0);
        ui.on_mouse_move(110 + dx, 110 + dy);
        assert_eq!((rect(&ui, w).0, rect(&ui, w).1), (left, top));
    }
}

#[test]
fn dragging_a_window_larger_than_its_parent_pins_it_to_the_origin() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let w = ui.add(r, window(0.0, 0.0, 1000.0, 700.0), Panel::default());
    ui.on_mouse_down(Left, 10, 10, 0);
    ui.on_mouse_move(300, 200);
    assert_eq!((rect(&ui, w).0, rect(&ui, w).1), (0.0, 0.0));
}

#[test]
fn a_moved_anchored_window_survives_the_next_layout_pass() {
    let mut ui = root(900.0, 600.0);
    let w = window_with_handle(
        &mut ui,
        at(0.0, 510.0, 610.0, 90.0).with_anchors(AnchorEdges::LEFT | AnchorEdges::BOTTOM),
    );
    ui.apply_anchor(w, 900.0, 600.0);
    ui.on_mouse_down(Left, 10, 512, 0);
    ui.on_mouse_move(210, 312);
    ui.apply_anchor(w, 900.0, 600.0);
    assert_eq!((rect(&ui, w).0, rect(&ui, w).1), (200.0, 310.0));
}

#[test]
fn hover_over_a_drag_handle_shows_move_the_body_does_not() {
    let mut ui = root(800.0, 600.0);
    window_with_handle(&mut ui, at(100.0, 500.0, 610.0, 90.0));
    ui.on_mouse_move(110, 502);
    assert!(ui.hover_window_move());
    ui.on_mouse_move(110, 550);
    assert!(!ui.hover_window_move());
}

#[test]
fn a_locked_ui_neither_moves_nor_shows_the_move_cursor() {
    let mut ui = root(800.0, 600.0);
    let w = window_with_handle(&mut ui, at(100.0, 500.0, 610.0, 90.0));
    ui.set_ui_locked(true);
    ui.on_mouse_move(110, 502);
    assert!(!ui.hover_window_move());
    ui.on_mouse_down(Left, 110, 502, 0);
    ui.on_mouse_move(160, 452);
    assert_eq!((rect(&ui, w).0, rect(&ui, w).1), (100.0, 500.0));
}

#[test]
fn a_dragged_window_stays_inside_and_reports_the_move_once_released() {
    let mut ui = root(200.0, 150.0);
    let r = ui.root();
    let p = ui.add(r, window(10.0, 10.0, 120.0, 100.0), Panel::default());
    ui.register_window("radar", p, None, 0).unwrap();
    ui.drain_output();
    ui.on_mouse_down(Left, 20, 20, 0);
    ui.on_mouse_move(500, 500);
    assert_eq!((rect(&ui, p).0, rect(&ui, p).1), (80.0, 50.0));
    ui.on_mouse_move(-500, -500);
    assert_eq!((rect(&ui, p).0, rect(&ui, p).1), (0.0, 0.0));
    ui.on_mouse_up(Left, -500, -500, 0);
    let moved: Vec<_> = ui
        .drain_output()
        .into_iter()
        .filter(|o| {
            matches!(
                o,
                UiOutput::Window {
                    event: ac_ui::WindowEvent::Moved,
                    ..
                }
            )
        })
        .collect();
    assert_eq!(moved.len(), 1);
}

#[test]
fn a_locked_ui_blocks_window_moves_without_disabling_windows() {
    let mut ui = root(200.0, 150.0);
    ui.set_ui_locked(true);
    let r = ui.root();
    let p = ui.add(r, window(10.0, 10.0, 100.0, 60.0), Panel::default());
    ui.on_mouse_down(Left, 20, 20, 0);
    ui.on_mouse_move(100, 100);
    assert_eq!((rect(&ui, p).0, rect(&ui, p).1), (10.0, 10.0));
    assert!(ui.element(p).unwrap().enabled());
}

#[test]
fn a_non_draggable_panel_does_not_move() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let p = ui.add(r, at(10.0, 10.0, 100.0, 50.0), Panel::default());
    ui.on_mouse_down(Left, 20, 20, 0);
    ui.on_mouse_move(120, 90);
    assert_eq!((rect(&ui, p).0, rect(&ui, p).1), (10.0, 10.0));
}

#[test]
fn a_child_that_owns_its_drags_does_not_move_its_window() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let w = ui.add(r, window(10.0, 10.0, 200.0, 100.0), Panel::default());
    let child = ui.add(
        w,
        Element {
            captures_pointer_drag: true,
            ..at(20.0, 20.0, 120.0, 60.0)
        },
        Panel::default(),
    );
    ui.on_mouse_down(Left, 60, 60, 0);
    ui.on_mouse_move(160, 160);
    assert_eq!((rect(&ui, w).0, rect(&ui, w).1), (10.0, 10.0));
    assert_eq!(ui.captured(), Some(child));
    ui.on_mouse_up(Left, 160, 160, 0);
    assert_eq!((rect(&ui, w).0, rect(&ui, w).1), (10.0, 10.0));
}

// ── Window resize ───────────────────────────────────────────────────────

fn resizable(left: f32, top: f32, width: f32, height: f32) -> Element {
    Element {
        draggable: true,
        resizable: true,
        min_width: 40.0,
        min_height: 40.0,
        ..at(left, top, width, height)
    }
}

#[test]
fn a_drag_owning_child_still_allows_edge_resize() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let w = ui.add(r, resizable(100.0, 100.0, 200.0, 100.0), Panel::default());
    ui.add(
        w,
        Element {
            captures_pointer_drag: true,
            ..at(0.0, 0.0, 200.0, 100.0).with_anchors(AnchorEdges::ALL)
        },
        Panel::default(),
    );
    ui.on_mouse_down(Left, 298, 150, 0);
    ui.on_mouse_move(338, 150);
    assert_eq!((rect(&ui, w).2, rect(&ui, w).0), (240.0, 100.0));
    ui.on_mouse_up(Left, 338, 150, 0);
}

#[test]
fn a_bottom_resize_constrained_to_the_parent_reaches_the_screen_edge() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let w = ui.add(
        r,
        Element {
            resize_x: false,
            resize_y: true,
            resizable_edges: ResizeEdges::BOTTOM,
            constrain_resize_to_parent: true,
            min_height: 100.0,
            ..resizable(100.0, 80.0, 300.0, 200.0)
        },
        Panel::default(),
    );
    ui.on_mouse_down(Left, 250, 279, 0);
    ui.on_mouse_move(250, 2_000);
    let (_, top, _, height) = rect(&ui, w);
    assert_eq!(height, 520.0);
    assert_eq!(top + height, 600.0);
    ui.on_mouse_up(Left, 250, 2_000, 0);
}

#[test]
fn a_top_edge_that_is_not_resizable_is_a_move_affordance() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    ui.add(
        r,
        Element {
            resizable_edges: ResizeEdges::LEFT | ResizeEdges::RIGHT | ResizeEdges::BOTTOM,
            ..resizable(100.0, 80.0, 300.0, 200.0)
        },
        Panel::default(),
    );
    ui.on_mouse_move(250, 80);
    assert_eq!(ui.hover_resize_edges(), ResizeEdges::NONE);
    assert!(ui.hover_window_move());
}

#[test]
fn hit_edges_detects_corners_and_respects_axis_locks() {
    let p = at(100.0, 100.0, 200.0, 100.0);
    assert_eq!(
        hit_edges(&p, 300, 200, 5),
        ResizeEdges::RIGHT | ResizeEdges::BOTTOM
    );
    assert_eq!(hit_edges(&p, 200, 150, 5), ResizeEdges::NONE);
    let locked = Element {
        resize_y: false,
        ..at(100.0, 100.0, 200.0, 100.0)
    };
    assert!(hit_edges(&locked, 300, 150, 5).contains(ResizeEdges::RIGHT));
    assert!(!hit_edges(&locked, 200, 200, 5).contains(ResizeEdges::BOTTOM));
}

#[test]
fn an_edge_drag_resizes_an_interior_drag_moves() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let p = ui.add(r, resizable(100.0, 100.0, 200.0, 100.0), Panel::default());
    ui.on_mouse_down(Left, 298, 150, 0);
    ui.on_mouse_move(338, 150);
    assert_eq!((rect(&ui, p).2, rect(&ui, p).0), (240.0, 100.0));
    ui.on_mouse_up(Left, 338, 150, 0);
    ui.on_mouse_down(Left, 200, 150, 0);
    ui.on_mouse_move(220, 170);
    assert_eq!((rect(&ui, p).0, rect(&ui, p).1), (120.0, 120.0));
    ui.on_mouse_up(Left, 220, 170, 0);
}

#[test]
fn corner_grips_grow_and_shrink_both_axes() {
    for (i, grows_left, grows_up) in [
        (0, true, true),
        (1, false, true),
        (2, true, false),
        (3, false, false),
    ] {
        for grow in [true, false] {
            let mut ui = root(800.0, 600.0);
            let (w, grips) = window_with_corner_grips(&mut ui);
            let (gx, gy) = ui.screen_position(grips[i]);
            let (px, py) = (gx as i32 + 2, gy as i32 + 2);
            let sign = if grow { 1 } else { -1 };
            let dx = if grows_left { -30 } else { 30 } * sign;
            let dy = if grows_up { -30 } else { 30 } * sign;
            ui.on_mouse_down(Left, px, py, 0);
            assert_ne!(ui.active_resize_edges(), ResizeEdges::NONE);
            ui.on_mouse_move(px + dx, py + dy);
            ui.on_mouse_up(Left, px + dx, py + dy, 0);
            let (left, top, width, height) = rect(&ui, w);
            if grow {
                assert_eq!((width, height), (230.0, 130.0));
                assert_eq!(left, if grows_left { 70.0 } else { 100.0 });
                assert_eq!(top, if grows_up { 70.0 } else { 100.0 });
            } else {
                assert_eq!((width, height), (170.0, 70.0));
                assert_eq!(left, if grows_left { 130.0 } else { 100.0 });
                assert_eq!(top, if grows_up { 130.0 } else { 100.0 });
            }
        }
    }
}

#[test]
fn grip_edges_are_masked_by_the_windows_resizable_edges() {
    let mut ui = root(800.0, 600.0);
    let (w, grips) = window_with_corner_grips(&mut ui);
    let (gx, gy) = ui.screen_position(grips[0]);
    ui.on_mouse_down(Left, gx as i32 + 2, gy as i32 + 2, 0);
    assert_eq!(
        ui.active_resize_edges(),
        ResizeEdges::LEFT | ResizeEdges::TOP
    );
    ui.on_mouse_up(Left, gx as i32 + 2, gy as i32 + 2, 0);

    ui.element_mut(w).unwrap().resizable_edges = ResizeEdges::LEFT | ResizeEdges::BOTTOM;
    let (gx, gy) = ui.screen_position(grips[1]);
    ui.on_mouse_down(Left, gx as i32 + 2, gy as i32 + 2, 0);
    assert_eq!(ui.active_resize_edges(), ResizeEdges::NONE);
}

#[test]
fn a_move_handle_wins_over_resize_proximity() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let w = ui.add(r, resizable(100.0, 100.0, 200.0, 100.0), Panel::default());
    ui.add(
        w,
        Element {
            window_move_handle: true,
            ..at(5.0, 0.0, 190.0, 5.0)
        },
        Panel::default(),
    );
    ui.on_mouse_move(150, 102);
    assert_eq!(ui.hover_resize_edges(), ResizeEdges::NONE);
    assert!(ui.hover_window_move());
    ui.on_mouse_down(Left, 150, 102, 0);
    assert_eq!(ui.active_resize_edges(), ResizeEdges::NONE);
    assert!(ui.is_window_move_active());
    ui.on_mouse_move(200, 152);
    assert_eq!((rect(&ui, w).0, rect(&ui, w).1), (150.0, 150.0));
}

#[test]
fn hover_over_a_grip_reports_its_own_edges() {
    let mut ui = root(800.0, 600.0);
    let (_, grips) = window_with_corner_grips(&mut ui);
    let (gx, gy) = ui.screen_position(grips[3]);
    ui.on_mouse_move(gx as i32 + 2, gy as i32 + 2);
    assert_eq!(
        ui.hover_resize_edges(),
        ResizeEdges::RIGHT | ResizeEdges::BOTTOM
    );
}

fn height_only_window(ui: &mut UiRoot) -> ac_ui::ElementId {
    let r = ui.root();
    ui.add(
        r,
        Element {
            resize_x: false,
            resize_y: true,
            resizable_edges: ResizeEdges::BOTTOM,
            max_width: 2000.0,
            max_height: 2000.0,
            ..resizable(100.0, 100.0, 300.0, 200.0)
        },
        Panel::default(),
    )
}

#[test]
fn corners_of_a_height_only_window_offer_the_vertical_resize() {
    for (x, y, expected) in [
        (102, 102, ResizeEdges::TOP),
        (398, 102, ResizeEdges::TOP),
        (102, 298, ResizeEdges::BOTTOM),
        (398, 298, ResizeEdges::BOTTOM),
    ] {
        let mut ui = root(800.0, 600.0);
        height_only_window(&mut ui);
        ui.on_mouse_move(x, y);
        assert_eq!(ui.hover_resize_edges(), expected);
        assert!(!ui.hover_window_move());
    }
}

#[test]
fn flat_runs_outside_the_resizable_edges_stay_move_handles() {
    for (x, y) in [(250, 102), (102, 200), (398, 200)] {
        let mut ui = root(800.0, 600.0);
        height_only_window(&mut ui);
        ui.on_mouse_move(x, y);
        assert_eq!(ui.hover_resize_edges(), ResizeEdges::NONE);
        assert!(ui.hover_window_move());
    }
}

#[test]
fn a_top_left_corner_drag_on_a_height_only_window_grows_height_only() {
    let mut ui = root(800.0, 600.0);
    let w = height_only_window(&mut ui);
    ui.on_mouse_down(Left, 102, 102, 0);
    assert_eq!(ui.active_resize_edges(), ResizeEdges::TOP);
    ui.on_mouse_move(62, 62);
    ui.on_mouse_up(Left, 62, 62, 0);
    assert_eq!(rect(&ui, w), (100.0, 60.0, 300.0, 240.0));
}

#[test]
fn corners_offer_the_diagonal_unless_both_axes_are_locked() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    ui.add(
        r,
        Element {
            max_width: 2000.0,
            max_height: 2000.0,
            ..resizable(100.0, 100.0, 300.0, 200.0)
        },
        Panel::default(),
    );
    ui.on_mouse_move(398, 298);
    assert_eq!(
        ui.hover_resize_edges(),
        ResizeEdges::RIGHT | ResizeEdges::BOTTOM
    );
    assert!(!ui.hover_window_move());

    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    ui.add(
        r,
        Element {
            resize_x: false,
            resize_y: false,
            ..resizable(100.0, 100.0, 300.0, 200.0)
        },
        Panel::default(),
    );
    ui.on_mouse_move(102, 102);
    assert_eq!(ui.hover_resize_edges(), ResizeEdges::NONE);
    assert!(ui.hover_window_move());
}

// ── Anchors and layout policies ─────────────────────────────────────────

#[test]
fn no_anchors_is_a_no_op() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let p = ui.add(
        r,
        at(50.0, 60.0, 100.0, 40.0).with_anchors(AnchorEdges::NONE),
        Panel::default(),
    );
    ui.apply_anchor(p, 800.0, 600.0);
    assert_eq!(rect(&ui, p), (50.0, 60.0, 100.0, 40.0));
}

const PARENT: PixelRect = PixelRect::from_position_and_size(0, 0, 800, 600);
const CHILD: PixelRect = PixelRect::from_position_and_size(100, 50, 200, 100);

#[test]
fn setting_anchors_drops_the_imported_policy() {
    let mut e = at(0.0, 0.0, 0.0, 0.0);
    e.layout_policy = Some(LayoutPolicy::new(1, 1, 1, 1, CHILD, PARENT).unwrap());
    e.set_anchors(AnchorEdges::TOP_LEFT);
    assert!(e.layout_policy.is_none());
}

#[test]
fn apply_anchor_uses_the_imported_policy() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let mut e = at(100.0, 50.0, 200.0, 100.0);
    e.layout_policy = Some(LayoutPolicy::new(1, 1, 1, 1, CHILD, PARENT).unwrap());
    let p = ui.add(r, e, Panel::default());
    ui.apply_anchor(p, 1000.0, 700.0);
    assert_eq!(rect(&ui, p), (100.0, 50.0, 400.0, 200.0));
}

#[test]
fn reset_anchor_capture_rebases_the_policy_after_a_controller_resize() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let parent = ui.add(r, at(0.0, 0.0, 490.0, 17.0), Panel::default());
    let mut e = at(0.0, 0.0, 46.0, 17.0);
    e.layout_policy = Some(
        LayoutPolicy::new(
            1,
            1,
            2,
            1,
            PixelRect::from_position_and_size(0, 0, 46, 17),
            PixelRect::from_position_and_size(0, 0, 490, 17),
        )
        .unwrap(),
    );
    let child = ui.add(parent, e, Panel::default());
    ui.element_mut(child).unwrap().width = 72.0;
    ui.reset_anchor_capture(child);
    ui.apply_anchor(child, 490.0, 17.0);
    assert_eq!(rect(&ui, child).2, 72.0);
}

#[test]
fn rebased_children_grow_with_their_frame() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let frame = ui.add(r, at(0.0, 0.0, 490.0, 100.0), Panel::default());
    let mut e = at(0.0, 0.0, 490.0, 83.0);
    e.layout_policy = Some(
        LayoutPolicy::new(
            1,
            1,
            1,
            1,
            PixelRect::from_position_and_size(0, 0, 490, 83),
            PixelRect::from_position_and_size(0, 0, 800, 100),
        )
        .unwrap(),
    );
    let transcript = ui.add(frame, e, Panel::default());
    ui.rebase_child_layout_baselines(frame);
    ui.element_mut(frame).unwrap().width = 615.0;
    ui.apply_anchor(transcript, 615.0, 100.0);
    assert_eq!(rect(&ui, transcript).2, 615.0);
}

// ── Windows, z-order, ids ───────────────────────────────────────────────

#[test]
fn toggle_show_hide_and_unknown_names() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let w = ui.add(
        r,
        at(0.0, 0.0, 100.0, 100.0).with_visible(false),
        Panel::default(),
    );
    ui.register_window("inventory", w, None, 0).unwrap();
    assert!(ui.toggle_window("inventory"));
    assert!(ui.element(w).unwrap().visible());
    assert!(!ui.toggle_window("inventory"));
    assert!(!ui.element(w).unwrap().visible());
    assert!(!ui.is_window_visible("inventory"));
    assert!(ui.show_window("inventory"));
    assert!(ui.is_window_visible("inventory"));
    assert!(ui.hide_window("inventory"));
    assert!(!ui.show_window("nope"));
    assert!(!ui.hide_window("nope"));
    assert!(!ui.toggle_window("nope"));
    assert!(!ui.is_window_visible("nope"));
}

#[test]
fn showing_a_window_raises_it_above_its_peers() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let a = panel(&mut ui, r, at(0.0, 0.0, 100.0, 100.0));
    let b = panel(&mut ui, r, at(0.0, 0.0, 100.0, 100.0));
    let w = ui.add(
        r,
        at(0.0, 0.0, 100.0, 100.0).with_visible(false),
        Panel::default(),
    );
    ui.register_window("inventory", w, None, 0).unwrap();
    ui.show_window("inventory");
    let z = |id| ui.element(id).unwrap().z_order();
    assert!(z(w) > z(a) && z(w) > z(b));
}

#[test]
fn bring_to_front_sets_the_strictly_greatest_z_order() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let a = panel(&mut ui, r, at(0.0, 0.0, 100.0, 100.0).with_z_order(5));
    let b = panel(&mut ui, r, at(0.0, 0.0, 100.0, 100.0).with_z_order(9));
    ui.bring_to_front(a);
    assert_eq!(ui.element(a).unwrap().z_order(), 10);
    assert!(ui.element(a).unwrap().z_order() > ui.element(b).unwrap().z_order());
}

#[test]
fn a_press_on_a_window_raises_it() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let peer = panel(&mut ui, r, at(300.0, 0.0, 100.0, 100.0).with_z_order(9));
    let clicked = ui.add(r, window(0.0, 0.0, 100.0, 100.0), Panel::default());
    ui.on_mouse_down(Left, 50, 50, 0);
    assert!(ui.element(clicked).unwrap().z_order() > ui.element(peer).unwrap().z_order());
    ui.on_mouse_up(Left, 50, 50, 0);
}

#[test]
fn attaching_under_the_root_assigns_distinct_event_ids_recursively() {
    let mut ui = root(800.0, 600.0);
    let parent = ui.insert(at(0.0, 0.0, 100.0, 100.0), Panel::default());
    let child = ui.add(parent, at(0.0, 0.0, 50.0, 50.0), Panel::default());
    let grand = ui.add(child, at(0.0, 0.0, 25.0, 25.0), Panel::default());
    assert_eq!(ui.event_id(grand), 0);
    let r = ui.root();
    ui.add_child(r, parent);
    let ids: std::collections::HashSet<u32> = [parent, child, grand]
        .iter()
        .map(|id| ui.event_id(*id))
        .collect();
    assert_eq!(ids.len(), 3);
    assert!(!ids.contains(&0));
}

#[test]
fn removing_a_subtree_releases_all_root_ownership() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let w = ui.add(r, at(0.0, 0.0, 200.0, 100.0), Panel::default());
    let field = ui.add(w, CharField::element(100.0, 20.0), CharField::default());
    ui.register_window("test", w, None, 0).unwrap();
    ui.default_text_input = Some(field);
    ui.modal = Some(w);
    ui.set_keyboard_focus(Some(field));
    ui.set_capture(field);
    assert!(ui.remove_child(r, w));
    assert_eq!(ui.keyboard_focus(), None);
    assert_eq!(ui.captured(), None);
    assert_eq!(ui.default_text_input, None);
    assert_eq!(ui.modal, None);
    assert!(!ui.show_window("test"));
}

// ── Time ────────────────────────────────────────────────────────────────

#[test]
fn the_tooltip_fires_before_the_time_pulse_at_the_retail_delay() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let rec = ui.add(r, at(0.0, 0.0, 100.0, 100.0), TimeOrderRecorder::default());
    ui.tick(0.0, 1_000);
    ui.on_mouse_move(10, 10);
    ui.widget_mut::<TimeOrderRecorder>(rec)
        .unwrap()
        .events
        .clear();
    ui.tick(0.0, 1_249);
    assert!(
        !ui.widget::<TimeOrderRecorder>(rec)
            .unwrap()
            .events
            .contains(&"tooltip")
    );
    ui.widget_mut::<TimeOrderRecorder>(rec)
        .unwrap()
        .events
        .clear();
    ui.tick(0.0, 1_250);
    assert_eq!(
        ui.widget::<TimeOrderRecorder>(rec).unwrap().events,
        ["tooltip", "global"]
    );
}

#[test]
fn a_time_pulse_removal_skips_the_detached_sibling() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let victim = ui.insert(at(0.0, 0.0, 100.0, 100.0), TimeOrderRecorder::default());
    let remover = GlobalTimeAction(Box::new(move |ui: &mut UiRoot| {
        let r = ui.root();
        ui.remove_child(r, victim);
    }));
    ui.add(r, at(0.0, 0.0, 100.0, 100.0), remover);
    ui.add_child(r, victim);
    ui.tick(0.0, 1_000);
    assert!(
        ui.widget::<TimeOrderRecorder>(victim)
            .unwrap()
            .events
            .is_empty()
    );
    assert_eq!(ui.parent(victim), None);
}

// ── Pointer regions ─────────────────────────────────────────────────────

#[derive(Debug, Default)]
struct Region {
    right_click: Option<(i32, i32)>,
    lift: Option<(i32, i32)>,
}

#[derive(Debug, Clone, Default)]
struct SharedRegion(std::sync::Arc<std::sync::Mutex<Region>>);

impl ac_ui::tree::RegionHandler for SharedRegion {
    fn right_clicked(&mut self, x: i32, y: i32) -> bool {
        self.0.lock().unwrap().right_click = Some((x, y));
        true
    }
    fn is_drag_source(&self) -> bool {
        true
    }
    fn drag_payload_at(&mut self, x: i32, y: i32) -> Option<ac_ui::DragPayload> {
        self.0.lock().unwrap().lift = Some((x, y));
        Some(ac_ui::DragPayload::new("lifted"))
    }
}

fn region_case(ui: &mut UiRoot, id: ac_ui::ElementId, region: &SharedRegion) {
    ui.element_mut(id).unwrap().pointer_region =
        Some(ac_ui::tree::PointerRegion::new(region.clone()));
    ui.on_mouse_down(Right, 110, 60, 0);
    ui.on_mouse_up(Right, 110, 60, 0);
    assert_eq!(region.0.lock().unwrap().right_click, Some((10, 10)));
    ui.on_mouse_down(Left, 130, 70, 0);
    ui.on_mouse_move(130, 90);
    assert_eq!(
        ui.drag_payload().and_then(|p| p.get::<&str>()),
        Some(&"lifted")
    );
    assert_eq!(region.0.lock().unwrap().lift, Some((30, 20)));
}

#[test]
fn right_click_and_drag_begin_reach_a_dat_element_in_local_coordinates() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let info = std::sync::Arc::new(ac_ui::element_info::ElementInfo::default());
    let id = ui.add(
        r,
        at(100.0, 50.0, 40.0, 40.0),
        ac_ui::widgets::DatElement::new(info),
    );
    region_case(&mut ui, id, &SharedRegion::default());
}

#[test]
fn a_button_with_a_pointer_region_is_a_drag_source_and_takes_the_right_click() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let info = std::sync::Arc::new(ac_ui::element_info::ElementInfo::default());
    let id = ui.add(
        r,
        at(100.0, 50.0, 40.0, 40.0),
        Button::new(info, None, Vec::new(), 0.0),
    );
    let region = SharedRegion::default();
    ui.element_mut(id).unwrap().pointer_region =
        Some(ac_ui::tree::PointerRegion::new(region.clone()));
    let node_is_source = {
        let e = ui.element(id).unwrap();
        ui.widget_dyn(id).unwrap().is_drag_source(e)
    };
    assert!(node_is_source);
    region_case(&mut ui, id, &region);
}

// ── Drag and drop (root level) ──────────────────────────────────────────

#[test]
fn a_drag_arms_only_with_a_payload_and_drops_on_the_target() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let source = ui.add(
        r,
        at(0.0, 0.0, 32.0, 32.0),
        DragSource {
            payload: Some(0x5000_0A01),
            ghost: Some(DragGhost {
                texture: TextureRef::Runtime(9),
                width: 32,
                height: 32,
            }),
            ..DragSource::default()
        },
    );
    let target = ui.add(r, at(100.0, 0.0, 32.0, 32.0), DropTarget::default());
    ui.on_mouse_down(Left, 5, 5, 0);
    ui.on_mouse_move(6, 6);
    assert_eq!(ui.drag_source(), None, "inside the threshold");
    ui.on_mouse_move(110, 10);
    assert_eq!(ui.drag_source(), Some(source));
    assert_eq!(ui.drag_ghost().unwrap().texture, TextureRef::Runtime(9));
    assert_eq!(
        ui.widget::<DropTarget>(target).unwrap().entered,
        [0x5000_0A01]
    );
    ui.on_mouse_up(Left, 110, 12, 0);
    assert_eq!(
        ui.widget::<DropTarget>(target).unwrap().dropped,
        [(0x5000_0A01, 10, 12)]
    );
    assert_eq!(
        ui.widget::<DragSource>(source).unwrap().activations,
        [true, false]
    );
    assert_eq!(ui.drag_source(), None);
    assert_eq!(ui.captured(), None);

    let empty = ui.add(r, at(0.0, 100.0, 32.0, 32.0), DragSource::default());
    ui.on_mouse_down(Left, 5, 105, 0);
    ui.on_mouse_move(60, 160);
    assert_eq!(ui.drag_source(), None);
    assert!(
        ui.widget::<DragSource>(empty)
            .unwrap()
            .activations
            .is_empty()
    );
}

#[test]
fn the_ghost_is_drawn_centred_on_the_pointer_in_the_overlay() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    ui.add(
        r,
        at(0.0, 0.0, 32.0, 32.0),
        DragSource {
            payload: Some(1),
            ghost: Some(DragGhost {
                texture: TextureRef::Runtime(4),
                width: 20,
                height: 10,
            }),
            ..DragSource::default()
        },
    );
    ui.on_mouse_down(Left, 5, 5, 0);
    ui.on_mouse_move(300, 200);
    let list = ui.draw((800.0, 600.0), &NoSprites);
    match list.overlay.last() {
        Some(DrawCmd::Sprite { texture, quad, .. }) => {
            assert_eq!(*texture, TextureRef::Runtime(4));
            assert_eq!((quad.x, quad.y, quad.w, quad.h), (290.0, 195.0, 20.0, 10.0));
        }
        other => panic!("no ghost: {other:?}"),
    }
}

#[test]
fn a_drag_released_over_nothing_goes_to_the_world() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    ui.add(
        r,
        at(0.0, 0.0, 32.0, 32.0),
        DragSource {
            payload: Some(0x5000_0A01),
            ..DragSource::default()
        },
    );
    ui.on_mouse_down(Left, 5, 5, 0);
    ui.on_mouse_move(20, 20);
    ui.drain_output();
    ui.on_mouse_up(Left, 200, 200, 0);
    let out = ui.drain_output();
    let released = out.iter().find_map(|o| match o {
        UiOutput::DragReleasedOutsideUi { payload, x, y } => {
            Some((*payload.get::<u32>().unwrap(), *x, *y))
        }
        _ => None,
    });
    assert_eq!(released, Some((0x5000_0A01, 200, 200)));
}

#[test]
fn a_root_owned_drag_survives_its_source_being_replaced() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let source = ui.add(
        r,
        at(0.0, 0.0, 32.0, 32.0),
        DragSource {
            payload: Some(7),
            ..DragSource::default()
        },
    );
    let target = ui.add(r, at(100.0, 0.0, 32.0, 32.0), DropTarget::default());
    ui.on_mouse_down(Left, 5, 5, 0);
    ui.on_mouse_move(50, 50);
    assert_eq!(ui.drag_source(), Some(source));
    // A controller rebuilds the slot mid-drag.
    ui.remove_child(r, source);
    assert_eq!(ui.drag_source(), Some(source));
    assert_eq!(ui.captured(), Some(r));
    ui.on_mouse_move(110, 10);
    ui.on_mouse_up(Left, 110, 10, 0);
    assert_eq!(
        ui.widget::<DropTarget>(target).unwrap().dropped,
        [(7, 10, 10)]
    );
    assert_eq!(
        ui.widget::<DragSource>(source).unwrap().activations,
        [true, false, false]
    );
}

// ── Child order, clipping ───────────────────────────────────────────────

#[test]
fn children_draw_back_to_front_and_hit_front_to_back_by_z_order() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let first = ui.add(
        r,
        at(0.0, 0.0, 50.0, 50.0).with_z_order(1),
        Panel::default(),
    );
    let second = ui.add(
        r,
        at(0.0, 0.0, 50.0, 50.0).with_z_order(2),
        Panel::default(),
    );
    assert_eq!(ui.pick(10, 10), Some(second));
    ui.set_z_order(first, 3);
    assert_eq!(ui.pick(10, 10), Some(first));
    // Equal z-order keeps insertion order (the later child is on top).
    let third = ui.add(
        r,
        at(0.0, 0.0, 50.0, 50.0).with_z_order(3),
        Panel::default(),
    );
    assert_eq!(ui.pick(10, 10), Some(third));
}

#[test]
fn a_child_outside_its_parent_is_never_hit_and_never_drawn() {
    let mut ui = root(200.0, 200.0);
    let r = ui.root();
    let parent = ui.add(r, at(0.0, 0.0, 50.0, 50.0), ac_ui::tree::Container);
    let outside = Panel {
        background_sprite: 7,
        ..Panel::default()
    };
    ui.add(parent, at(-30.0, -30.0, 20.0, 20.0), outside);
    let straddling = Panel {
        background_sprite: 3,
        ..Panel::default()
    };
    ui.add(parent, at(40.0, 10.0, 30.0, 10.0), straddling);
    assert_eq!(ui.hit_test(parent, -20.0, -20.0), None);

    #[derive(Debug)]
    struct Eight;
    impl ac_ui::SpriteSizes for Eight {
        fn sprite_size(&self, _file: u32) -> Option<(u32, u32)> {
            Some((8, 8))
        }
    }
    let list = ui.draw((200.0, 200.0), &Eight);
    let sprites: Vec<_> = list
        .main
        .iter()
        .filter_map(|c| match c {
            DrawCmd::Sprite { texture, quad, .. } => Some((*texture, *quad)),
            _ => None,
        })
        .collect();
    assert!(sprites.iter().all(|(t, _)| *t != TextureRef::Surface(7)));
    let (_, q) = sprites
        .iter()
        .find(|(t, _)| *t == TextureRef::Surface(3))
        .unwrap();
    assert!(q.x + q.w <= 50.01);
}

// ── Fixed canvas ────────────────────────────────────────────────────────

fn clicking_button(ui: &mut UiRoot) -> ac_ui::ElementId {
    let r = ui.root();
    let info = std::sync::Arc::new(ac_ui::element_info::ElementInfo {
        width: 120.0,
        height: 40.0,
        ..Default::default()
    });
    let mut b = Button::new(info, None, Vec::new(), 0.0);
    b.listeners.click = true;
    ui.add(r, at(300.0, 400.0, 120.0, 40.0), b)
}

fn clicks(ui: &mut UiRoot) -> usize {
    ui.drain_output()
        .iter()
        .filter(|o| {
            matches!(
                o,
                UiOutput::Element {
                    message: ElementMessage::Clicked { .. },
                    ..
                }
            )
        })
        .count()
}

#[test]
fn canvas_scale_is_window_over_canvas_when_fixed() {
    let mut ui = root(1920.0, 1080.0);
    assert_eq!(ui.canvas_scale(), (1.0, 1.0));
    ui.declare_fixed_canvas(1, (800.0, 600.0)).unwrap();
    assert_eq!(ui.canvas_scale(), (2.4, 1.8));
    let mut zero = root(1920.0, 1080.0);
    zero.declare_fixed_canvas(1, (0.0, 600.0)).unwrap();
    assert_eq!(zero.canvas_scale(), (1.0, 1.0));
    let mut no_window = root(0.0, 0.0);
    no_window.declare_fixed_canvas(1, (800.0, 600.0)).unwrap();
    assert_eq!(no_window.canvas_scale(), (1.0, 1.0));
}

#[test]
fn mouse_input_maps_window_coordinates_to_the_canvas() {
    let mut ui = root(1920.0, 1080.0);
    ui.declare_fixed_canvas(1, (800.0, 600.0)).unwrap();
    clicking_button(&mut ui);
    click(&mut ui, 860, 750);
    assert_eq!(clicks(&mut ui), 1);
    assert_eq!(ui.mouse(), (358, 416));
    ui.on_mouse_move(1919, 1079);
    assert_eq!(ui.mouse(), (799, 599));
    click(&mut ui, 300, 400);
    assert_eq!(clicks(&mut ui), 0);

    let mut plain = root(1920.0, 1080.0);
    clicking_button(&mut plain);
    click(&mut plain, 360, 420);
    assert_eq!(clicks(&mut plain), 1);
}

#[test]
fn fixed_canvas_declarations_must_agree() {
    let mut ui = root(1920.0, 1080.0);
    ui.declare_fixed_canvas(1, (800.0, 600.0)).unwrap();
    ui.declare_fixed_canvas(1, (800.0, 600.0)).unwrap();
    assert!(ui.declare_fixed_canvas(1, (640.0, 480.0)).is_err());
    assert!(ui.declare_fixed_canvas(2, (640.0, 480.0)).is_err());
    ui.declare_fixed_canvas(2, (800.0, 600.0)).unwrap();
    ui.revoke_fixed_canvas(1);
    assert_eq!(ui.fixed_canvas(), Some((800.0, 600.0)));
    ui.revoke_fixed_canvas(2);
    assert_eq!(ui.fixed_canvas(), None);
}

// ── Hover and focus ─────────────────────────────────────────────────────

#[test]
fn focus_changes_report_old_and_new() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let a = ui.add(r, CharField::element(10.0, 10.0), CharField::default());
    ui.set_keyboard_focus(Some(a));
    ui.set_keyboard_focus(None);
    let focus: Vec<_> = ui
        .drain_output()
        .into_iter()
        .filter_map(|o| match o {
            UiOutput::FocusChanged { old, new } => Some((old, new)),
            _ => None,
        })
        .collect();
    assert_eq!(focus, [(None, Some(a)), (Some(a), None)]);
}

#[test]
fn a_left_press_focuses_focusable_elements_and_clears_focus_elsewhere() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let field = ui.add(r, CharField::element(100.0, 20.0), CharField::default());
    ui.on_mouse_down(Left, 5, 5, 0);
    ui.on_mouse_up(Left, 5, 5, 0);
    assert_eq!(ui.keyboard_focus(), Some(field));
    ui.on_mouse_down(Left, 500, 500, 0);
    assert_eq!(ui.keyboard_focus(), None);
}

// ── A focus loss that closes its own panel (OpenAC v0.1.25) ──

/// Hides `panel` when it loses focus, counting the losses it hears.
#[derive(Debug)]
struct HidesOnBlur {
    panel: ac_ui::ElementId,
    losses: u32,
}

impl ac_ui::tree::Widget for HidesOnBlur {
    fn on_event(
        &mut self,
        ui: &mut UiRoot,
        _me: ac_ui::ElementId,
        e: &ac_ui::event::UiEvent,
    ) -> bool {
        if e.kind == ac_ui::event::kind::FOCUS_LOST {
            self.losses += 1;
            ui.set_visible(self.panel, false);
            return true;
        }
        false
    }
}

/// A panel holding a field that hides the panel on blur and a plain field
/// below it.
fn blur_closes_panel() -> (UiRoot, ac_ui::ElementId, ac_ui::ElementId, ac_ui::ElementId) {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let panel = panel(&mut ui, r, at(0.0, 0.0, 240.0, 120.0));
    let first = ui.add(
        panel,
        CharField::element(120.0, 20.0),
        HidesOnBlur { panel, losses: 0 },
    );
    let mut second = CharField::element(120.0, 20.0);
    second.top = 32.0;
    let second = ui.add(panel, second, CharField::default());
    ui.set_keyboard_focus(Some(first));
    (ui, panel, first, second)
}

#[test]
fn a_focus_loss_that_hides_its_panel_is_heard_once_and_focuses_nothing() {
    // FieldOnBlurThatClosesItsOwnPanelRunsOnce and
    // FieldOnBlurClosingPanelDoesNotFocusOrCaptureTheNextField.
    let (mut ui, panel, first, second) = blur_closes_panel();
    ui.set_keyboard_focus(Some(second));
    assert_eq!(ui.widget::<HidesOnBlur>(first).unwrap().losses, 1);
    assert!(!ui.element(panel).unwrap().visible);
    assert_eq!(ui.keyboard_focus(), None, "the hidden field is not focused");

    // A press on the next field: the loss hides the panel first, so the
    // press neither focuses nor captures it.
    let (mut ui, _, first, _) = blur_closes_panel();
    ui.on_mouse_down(Left, 20, 40, 0);
    assert_eq!(ui.widget::<HidesOnBlur>(first).unwrap().losses, 1);
    assert_eq!(ui.keyboard_focus(), None);
    assert_eq!(ui.captured(), None);
    ui.on_char('x');
    ui.on_mouse_up(Left, 20, 40, 0);
}

// ── A drag started on the world ─────────────────────────────────────────

fn world_drag_starts(ui: &mut UiRoot) -> Vec<(i32, i32)> {
    ui.drain_output()
        .iter()
        .filter_map(|o| match o {
            UiOutput::WorldDragStart { x, y } => Some((*x, *y)),
            _ => None,
        })
        .collect()
}

#[test]
fn a_press_on_the_world_that_moves_asks_for_a_lift_at_the_press_point() {
    let mut ui = root(800.0, 600.0);
    ui.on_mouse_down(Left, 300, 300, 0);
    ui.on_mouse_move(302, 301);
    assert!(world_drag_starts(&mut ui).is_empty());
    ui.on_mouse_move(320, 310);
    assert_eq!(world_drag_starts(&mut ui), [(300, 300)]);
    // Asked once per press.
    ui.on_mouse_move(340, 330);
    assert!(world_drag_starts(&mut ui).is_empty());
}

#[test]
fn only_a_left_press_on_the_world_asks_for_a_lift() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    ui.add(r, at(0.0, 0.0, 100.0, 100.0), Panel::default());
    ui.on_mouse_down(Right, 300, 300, 0);
    ui.on_mouse_move(350, 350);
    ui.on_mouse_up(Right, 350, 350, 0);
    ui.on_mouse_down(Left, 50, 50, 0);
    ui.on_mouse_move(90, 90);
    ui.on_mouse_up(Left, 90, 90, 0);
    ui.on_mouse_down(Left, 300, 300, 0);
    ui.on_mouse_up(Left, 300, 300, 0);
    ui.on_mouse_move(350, 350);
    assert!(world_drag_starts(&mut ui).is_empty());
}

#[test]
fn a_world_lift_is_held_by_the_root_and_dropped_like_any_drag() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let target = ui.add(r, at(0.0, 0.0, 100.0, 40.0), DropTarget::default());
    ui.on_mouse_down(Left, 300, 300, 0);
    ui.on_mouse_move(320, 320);
    assert!(ui.begin_world_drag(
        ac_ui::event::DragPayload::new(0x5000_0A01u32),
        Some(DragGhost {
            texture: TextureRef::Runtime(9),
            width: 32,
            height: 32,
        }),
    ));
    assert_eq!(ui.drag_source(), Some(r));
    assert_eq!(ui.captured(), Some(r));
    ui.on_mouse_move(20, 20);
    ui.on_mouse_up(Left, 20, 20, 0);
    assert_eq!(
        ui.widget::<DropTarget>(target).unwrap().dropped,
        [(0x5000_0A01, 20, 20)]
    );
    assert_eq!(ui.drag_source(), None);
    assert_eq!(ui.captured(), None);
}

#[test]
fn a_world_lift_released_on_the_world_goes_to_the_world() {
    let mut ui = root(800.0, 600.0);
    ui.on_mouse_down(Left, 300, 300, 0);
    ui.on_mouse_move(320, 320);
    assert!(ui.begin_world_drag(ac_ui::event::DragPayload::new(7u32), None));
    ui.drain_output();
    ui.on_mouse_up(Left, 400, 400, 0);
    let released = ui.drain_output().iter().find_map(|o| match o {
        UiOutput::DragReleasedOutsideUi { payload, x, y } => {
            Some((*payload.get::<u32>().unwrap(), *x, *y))
        }
        _ => None,
    });
    assert_eq!(released, Some((7, 400, 400)));
}

#[test]
fn a_world_lift_is_refused_once_the_button_is_up() {
    let mut ui = root(800.0, 600.0);
    ui.on_mouse_down(Left, 300, 300, 0);
    ui.on_mouse_move(320, 320);
    ui.on_mouse_up(Left, 320, 320, 0);
    assert!(!ui.begin_world_drag(ac_ui::event::DragPayload::new(7u32), None));
    assert_eq!(ui.drag_source(), None);
}
