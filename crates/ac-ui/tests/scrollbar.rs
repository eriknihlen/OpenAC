//! Scrollbars, sliders, scrollable panels and list boxes.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiScrollbarTests.cs`,
//! `Layout/ScrollbarThumbBehaviorTests.cs`, `UiScrollablePanelTests.cs` and
//! `UiTemplateListBoxFlushPreservingScrollTests.cs`. `ScalarChanged` and
//! `DragCompleted` are [`ElementMessage`]s here.

mod common;

use std::sync::Arc;

use ac_ui::ElementId;
use ac_ui::MouseButton;
use ac_ui::draw::{DrawCmd, SpriteSizes, TextureRef};
use ac_ui::element_info::{ElementInfo, TemplateEntry};
use ac_ui::event::{UiEvent, kind};
use ac_ui::root::{ElementMessage, UiOutput, UiRoot};
use ac_ui::text::Scrollable;
use ac_ui::tree::Element;
use ac_ui::widgets::scrollbar::{scalar_fill_rect, thumb_rect};
use ac_ui::widgets::{ListBox, Panel, ScrollSource, ScrollablePanel, Scrollbar};
use common::*;

fn model(content: i32, view: i32, line: i32) -> Scrollable {
    let mut m = Scrollable::default();
    m.line_height = line;
    m.set_extents(content, view, false);
    m
}

fn bar_in(ui: &mut UiRoot, bar: Scrollbar, x: f32, y: f32, w: f32, h: f32) -> ElementId {
    let mut e = Element::at(x, y, w, h);
    Scrollbar::configure(&mut e);
    let r = ui.root();
    ui.add(r, e, bar)
}

fn with_model(m: Scrollable) -> Scrollbar {
    build(Scrollbar::default(), |b| b.model = ScrollSource::Own(m))
}

fn build(mut b: Scrollbar, f: impl FnOnce(&mut Scrollbar)) -> Scrollbar {
    f(&mut b);
    b
}

fn send(ui: &mut UiRoot, id: ElementId, k: i32, x: i32, y: i32) -> bool {
    let e = UiEvent {
        kind: k,
        data1: x,
        data2: y,
        target: Some(id),
        ..UiEvent::default()
    };
    ui.send_event(id, &e)
}

fn bar(ui: &UiRoot, id: ElementId) -> &Scrollbar {
    ui.widget::<Scrollbar>(id).unwrap()
}

fn scroll_y(ui: &UiRoot, id: ElementId) -> i32 {
    bar(ui, id).own_model().unwrap().scroll_y()
}

fn messages(ui: &mut UiRoot) -> Vec<ElementMessage> {
    ui.drain_output()
        .into_iter()
        .filter_map(|o| match o {
            UiOutput::Element { message, .. } => Some(message),
            _ => None,
        })
        .collect()
}

fn completed(ui: &mut UiRoot) -> usize {
    messages(ui)
        .iter()
        .filter(|m| matches!(m, ElementMessage::DragCompleted))
        .count()
}

fn scalars(ui: &mut UiRoot) -> Vec<f32> {
    messages(ui)
        .iter()
        .filter_map(|m| match m {
            ElementMessage::ScalarChanged(v) => Some(*v),
            _ => None,
        })
        .collect()
}

fn approx(a: f32, b: f32) {
    assert!((a - b).abs() < 1e-3, "{a} != {b}");
}

#[test]
fn thumb_rect_sizes_and_places_the_thumb() {
    let m = model(400, 100, 16);
    assert_eq!(thumb_rect(&m, 0.0, 200.0), (0.0, 50.0));
    let mut end = m;
    end.scroll_to_end();
    assert_eq!(thumb_rect(&end, 16.0, 200.0), (166.0, 50.0));
    let mut mid = m;
    mid.set_scroll_y(150);
    approx(mid.position_ratio(), 0.5);
    assert_eq!(thumb_rect(&mid, 0.0, 200.0), (75.0, 50.0));
    assert_eq!(thumb_rect(&model(1000, 10, 16), 0.0, 200.0).1, 8.0);
    assert_eq!(thumb_rect(&model(50, 100, 16), 16.0, 100.0), (16.0, 100.0));
}

#[test]
fn horizontal_scalar_click_and_drag_updates_normalized_value() {
    let mut ui = root(200.0, 200.0);
    let mut b = build(Scrollbar::default(), |b| {
        b.scalar = true;
        b.horizontal = true;
    });
    b.set_scalar_position(1.0);
    let id = bar_in(&mut ui, b, 0.0, 0.0, 90.0, 14.0);
    assert!(send(&mut ui, id, kind::MOUSE_DOWN, 8, 0));
    assert_eq!(scalars(&mut ui), vec![0.0]);
    approx(bar(&ui, id).scalar_position(), 0.0);
    assert!(send(&mut ui, id, kind::MOUSE_MOVE, 45, 0));
    approx(scalars(&mut ui)[0], 0.5);
    approx(bar(&ui, id).scalar_position(), 0.5);
    assert!(send(&mut ui, id, kind::MOUSE_UP, 45, 0));
}

#[test]
fn vertical_scalar_click_and_drag_updates_normalized_value() {
    let mut ui = root(200.0, 200.0);
    let mut b = build(Scrollbar::default(), |b| {
        b.scalar = true;
    });
    b.set_scalar_position(1.0);
    let id = bar_in(&mut ui, b, 0.0, 0.0, 14.0, 90.0);
    assert!(send(&mut ui, id, kind::MOUSE_DOWN, 0, 8));
    approx(scalars(&mut ui)[0], 0.0);
    assert!(send(&mut ui, id, kind::MOUSE_MOVE, 0, 45));
    approx(scalars(&mut ui)[0], 0.5);
    assert!(send(&mut ui, id, kind::MOUSE_UP, 0, 45));
}

#[test]
fn horizontal_scalar_drag_completed_fires_once_at_mouse_up() {
    let mut ui = root(200.0, 200.0);
    let id = bar_in(
        &mut ui,
        build(Scrollbar::default(), |b| {
            b.scalar = true;
            b.horizontal = true;
        }),
        0.0,
        0.0,
        90.0,
        14.0,
    );
    assert!(send(&mut ui, id, kind::MOUSE_DOWN, 5, 0));
    assert!(bar(&ui, id).is_dragging());
    for i in 0..10 {
        send(&mut ui, id, kind::MOUSE_MOVE, 10 + i, 0);
        assert_eq!(completed(&mut ui), 0);
    }
    assert!(send(&mut ui, id, kind::MOUSE_UP, 50, 0));
    assert!(!bar(&ui, id).is_dragging());
    assert_eq!(completed(&mut ui), 1);
    // A mouse-up that was never a drag completes nothing.
    send(&mut ui, id, kind::MOUSE_UP, 10, 0);
    assert_eq!(completed(&mut ui), 0);
}

#[test]
fn vertical_model_drag_completed_only_for_a_thumb_drag() {
    let mut ui = root(200.0, 300.0);
    let id = bar_in(
        &mut ui,
        with_model(model(400, 100, 16)),
        0.0,
        0.0,
        16.0,
        200.0,
    );
    assert!(send(&mut ui, id, kind::MOUSE_DOWN, 0, 5));
    assert!(!bar(&ui, id).is_dragging());
    assert!(send(&mut ui, id, kind::MOUSE_UP, 0, 5));
    assert_eq!(completed(&mut ui), 0);
    assert!(send(&mut ui, id, kind::MOUSE_DOWN, 0, 30));
    assert!(bar(&ui, id).is_dragging());
    assert!(send(&mut ui, id, kind::MOUSE_MOVE, 0, 40));
    assert!(send(&mut ui, id, kind::MOUSE_UP, 0, 40));
    assert_eq!(completed(&mut ui), 1);
}

#[test]
fn capture_loss_mid_drag_ends_the_gesture_once() {
    let mut ui = root(200.0, 200.0);
    let id = bar_in(
        &mut ui,
        build(Scrollbar::default(), |b| {
            b.scalar = true;
            b.horizontal = true;
        }),
        0.0,
        0.0,
        90.0,
        14.0,
    );
    send(&mut ui, id, kind::MOUSE_DOWN, 5, 0);
    send(&mut ui, id, kind::MOUSE_MOVE, 30, 0);
    ui.drain_output();
    send(&mut ui, id, kind::CAPTURE_CHANGED, 0, 0);
    assert!(!bar(&ui, id).is_dragging());
    assert_eq!(completed(&mut ui), 1);
    send(&mut ui, id, kind::MOUSE_UP, 30, 0);
    assert_eq!(completed(&mut ui), 0);
    // No drag: capture change is a no-op.
    send(&mut ui, id, kind::CAPTURE_CHANGED, 0, 0);
    assert_eq!(completed(&mut ui), 0);
}

#[test]
fn horizontal_model_buttons_track_and_thumb_drive_shared_scroll() {
    let mut ui = root(300.0, 100.0);
    let id = bar_in(
        &mut ui,
        build(with_model(model(320, 80, 32)), |b| {
            b.horizontal = true;
        }),
        0.0,
        0.0,
        160.0,
        16.0,
    );
    assert!(send(&mut ui, id, kind::MOUSE_DOWN, 159, 0));
    assert_eq!(scroll_y(&ui, id), 32);
    assert!(send(&mut ui, id, kind::MOUSE_DOWN, 100, 0));
    assert!(scroll_y(&ui, id) >= 80);
    ui.scroll_model_mut(id).unwrap().set_scroll_y(0);
    assert!(send(&mut ui, id, kind::MOUSE_DOWN, 20, 0));
    assert!(send(&mut ui, id, kind::MOUSE_MOVE, 144, 0));
    let max = bar(&ui, id).own_model().unwrap().max_scroll();
    assert_eq!(scroll_y(&ui, id), max);
    assert!(send(&mut ui, id, kind::MOUSE_UP, 144, 0));
}

#[test]
fn horizontal_model_uses_authored_arrow_extents_and_one_cell_step() {
    let mut ui = root(300.0, 100.0);
    let mut m = model(640, 320, 32);
    m.set_scroll_y(64);
    let id = bar_in(
        &mut ui,
        build(with_model(m), |b| {
            b.horizontal = true;
            b.decrement_button_extent = 23.0;
            b.increment_button_extent = 29.0;
        }),
        0.0,
        0.0,
        160.0,
        36.0,
    );
    assert!(send(&mut ui, id, kind::MOUSE_DOWN, 22, 0));
    assert_eq!(scroll_y(&ui, id), 32);
    assert!(send(&mut ui, id, kind::MOUSE_DOWN, 131, 0));
    assert_eq!(scroll_y(&ui, id), 64);
}

#[test]
fn horizontal_model_uses_independent_rollover_and_pressed_arrow_media() {
    let mut ui = root(300.0, 100.0);
    let id = bar_in(
        &mut ui,
        build(with_model(model(640, 320, 32)), |b| {
            b.horizontal = true;
            b.up_sprite = 1;
            b.up_rollover_sprite = 2;
            b.up_pressed_sprite = 3;
            b.down_sprite = 4;
            b.down_rollover_sprite = 5;
            b.down_pressed_sprite = 6;
        }),
        0.0,
        0.0,
        160.0,
        36.0,
    );
    ui.on_mouse_move(5, 10);
    assert_eq!(bar(&ui, id).active_start_sprite(), 2);
    assert_eq!(bar(&ui, id).active_end_sprite(), 4);
    ui.on_mouse_move(155, 10);
    assert_eq!(bar(&ui, id).active_start_sprite(), 1);
    assert_eq!(bar(&ui, id).active_end_sprite(), 5);
    ui.on_mouse_down(MouseButton::Left, 155, 10, 0);
    assert_eq!(bar(&ui, id).active_end_sprite(), 6);
    ui.on_mouse_up(MouseButton::Left, 155, 10, 0);
    assert_eq!(bar(&ui, id).active_end_sprite(), 5);
}

#[test]
fn model_without_overflow_hides_when_disabled_and_stays_inert() {
    let mut ui = root(300.0, 100.0);
    let id = bar_in(
        &mut ui,
        build(with_model(model(320, 320, 32)), |b| {
            b.horizontal = true;
            b.hide_when_disabled = true;
        }),
        0.0,
        0.0,
        160.0,
        36.0,
    );
    assert!(
        !ui.with::<Scrollbar, _>(id, |b, ui| b.presentation_visible(ui))
            .unwrap()
    );
    assert!(!send(&mut ui, id, kind::MOUSE_DOWN, 159, 0));
    assert_eq!(scroll_y(&ui, id), 0);
}

#[test]
fn scalar_fill_rect_grows_left_to_right_inside_its_range() {
    for (fill, x, w) in [(0.0, 0.0, 0.0), (0.5, 0.0, 50.0), (1.0, 0.0, 100.0)] {
        let r = scalar_fill_rect(0.0, 100.0, fill, false);
        approx(r.0, x);
        approx(r.1, w);
    }
    for (fill, x, w) in [(0.0, 104.0, 0.0), (0.5, 104.0, 149.5), (1.0, 104.0, 299.0)] {
        let r = scalar_fill_rect(104.0, 299.0, fill, false);
        approx(r.0, x);
        approx(r.1, w);
    }
}

#[derive(Debug)]
struct Sizes(Vec<(u32, (u32, u32))>);

impl SpriteSizes for Sizes {
    fn sprite_size(&self, file: u32) -> Option<(u32, u32)> {
        self.0.iter().find(|(f, _)| *f == file).map(|(_, s)| *s)
    }
}

fn quads_of(list: &ac_ui::DrawList, file: u32) -> Vec<ac_ui::draw::Quad> {
    list.commands()
        .filter_map(|c| match c {
            DrawCmd::Sprite { texture, quad, .. } if *texture == TextureRef::Surface(file) => {
                Some(*quad)
            }
            _ => None,
        })
        .collect()
}

#[test]
fn no_overflow_draws_a_full_track_thumb() {
    let mut ui = root(800.0, 600.0);
    let (top, mid, bot) = (60, 63, 66);
    let id = bar_in(
        &mut ui,
        build(with_model(model(150, 150, 16)), |b| {
            b.thumb_top_sprite = top;
            b.thumb_sprite = mid;
            b.thumb_bot_sprite = bot;
        }),
        0.0,
        0.0,
        16.0,
        200.0,
    );
    let _ = id;
    let sizes = Sizes(vec![(top, (16, 3)), (mid, (16, 3)), (bot, (16, 3))]);
    let list = ui.draw((800.0, 600.0), &sizes);
    let t = quads_of(&list, top);
    assert_eq!(t.len(), 1);
    approx(t[0].y, 16.0);
    let b = quads_of(&list, bot);
    assert_eq!(b.len(), 1);
    approx(b[0].y + b[0].h, 184.0);
}

#[test]
fn single_sprite_thumb_draws_one_untiled_instance() {
    let mut ui = root(800.0, 600.0);
    bar_in(
        &mut ui,
        build(with_model(model(200, 150, 16)), |b| {
            b.thumb_sprite = 42;
        }),
        0.0,
        0.0,
        16.0,
        200.0,
    );
    let list = ui.draw((800.0, 600.0), &Sizes(vec![(42, (16, 16))]));
    let q = quads_of(&list, 42);
    assert_eq!(q.len(), 1);
    assert!(q[0].v1 <= 1.0001, "thumb is not tiled: v1 {}", q[0].v1);
}

#[test]
fn thumb_hover_and_drag_select_rollover_and_pressed_media() {
    let mut ui = root(300.0, 300.0);
    let id = bar_in(
        &mut ui,
        build(with_model(model(200, 150, 10)), |b| {
            b.thumb_sprite = 1;
            b.thumb_rollover_sprite = 2;
            b.thumb_pressed_sprite = 3;
            b.thumb_top_sprite = 10;
            b.thumb_top_rollover_sprite = 20;
            b.thumb_top_pressed_sprite = 30;
            b.thumb_bot_sprite = 100;
            b.thumb_bot_rollover_sprite = 200;
            b.thumb_bot_pressed_sprite = 300;
        }),
        0.0,
        0.0,
        16.0,
        200.0,
    );
    assert_eq!(bar(&ui, id).active_thumb_sprite(), 1);
    send(&mut ui, id, kind::HOVER_ENTER, 8, 50);
    assert_eq!(bar(&ui, id).active_thumb_sprite(), 2);
    send(&mut ui, id, kind::MOUSE_DOWN, 8, 50);
    assert!(bar(&ui, id).is_dragging());
    assert_eq!(bar(&ui, id).active_thumb_sprite(), 3);
    send(&mut ui, id, kind::MOUSE_UP, 8, 50);
    assert_eq!(bar(&ui, id).active_thumb_sprite(), 2);
    send(&mut ui, id, kind::MOUSE_MOVE, 8, 170);
    assert_eq!(bar(&ui, id).active_thumb_sprite(), 1);
    send(&mut ui, id, kind::HOVER_ENTER, 8, 50);
    send(&mut ui, id, kind::HOVER_LEAVE, 0, 0);
    assert_eq!(bar(&ui, id).active_thumb_sprite(), 1);
}

#[test]
fn root_mouse_move_over_arrow_and_thumb_selects_rollover_media() {
    let mut ui = root(800.0, 600.0);
    let id = bar_in(
        &mut ui,
        build(with_model(model(400, 150, 10)), |b| {
            b.up_sprite = 1;
            b.up_rollover_sprite = 2;
            b.up_pressed_sprite = 3;
            b.down_sprite = 7;
            b.down_rollover_sprite = 8;
            b.thumb_sprite = 4;
            b.thumb_rollover_sprite = 5;
        }),
        100.0,
        100.0,
        16.0,
        200.0,
    );
    ui.on_mouse_move(108, 105);
    assert_eq!(bar(&ui, id).active_start_sprite(), 2);
    ui.on_mouse_move(108, 130);
    assert_eq!(bar(&ui, id).active_start_sprite(), 1);
    assert_eq!(bar(&ui, id).active_thumb_sprite(), 5);
    ui.on_mouse_move(108, 290);
    assert_eq!(bar(&ui, id).active_thumb_sprite(), 4);
    assert_eq!(bar(&ui, id).active_end_sprite(), 8);
}

#[test]
fn disabled_visible_bar_hover_highlights_but_never_scrolls() {
    let mut ui = root(800.0, 600.0);
    let id = bar_in(
        &mut ui,
        build(with_model(model(100, 150, 10)), |b| {
            b.up_sprite = 1;
            b.up_rollover_sprite = 2;
            b.thumb_sprite = 4;
            b.thumb_rollover_sprite = 5;
        }),
        100.0,
        100.0,
        16.0,
        200.0,
    );
    ui.on_mouse_move(108, 105);
    assert_eq!(bar(&ui, id).active_start_sprite(), 2);
    ui.on_mouse_move(108, 130);
    assert_eq!(bar(&ui, id).active_thumb_sprite(), 5);
    assert!(send(&mut ui, id, kind::MOUSE_DOWN, 8, 5));
    assert_eq!(scroll_y(&ui, id), 0);
    ui.widget_mut::<Scrollbar>(id).unwrap().hide_when_disabled = true;
    assert!(!send(&mut ui, id, kind::MOUSE_DOWN, 8, 5));
}

fn fixed_thumb_bar() -> Scrollbar {
    build(with_model(model(400, 300, 16)), |b| {
        b.decrement_button_extent = 17.0;
        b.increment_button_extent = 17.0;
        b.proportional = false;
        b.thumb_extent = 39.0;
    })
}

#[test]
fn fixed_thumb_keeps_its_extent_and_reaches_the_track_end() {
    let b = fixed_thumb_bar();
    let mut m = model(400, 300, 16);
    assert_eq!(b.model_thumb_rect(&m, 17.0, 273.0), (17.0, 39.0));
    m.scroll_to_end();
    assert_eq!(b.model_thumb_rect(&m, 17.0, 273.0).0, 17.0 + 273.0 - 39.0);
}

#[test]
fn fixed_thumb_drag_maps_the_whole_track() {
    let mut ui = root(300.0, 400.0);
    let id = bar_in(&mut ui, fixed_thumb_bar(), 0.0, 0.0, 37.0, 307.0);
    assert!(send(&mut ui, id, kind::MOUSE_DOWN, 10, 20));
    assert!(bar(&ui, id).is_dragging());
    assert!(send(&mut ui, id, kind::MOUSE_MOVE, 10, 137));
    assert_eq!(scroll_y(&ui, id), 50);
    assert!(send(&mut ui, id, kind::MOUSE_MOVE, 10, 254));
    assert_eq!(scroll_y(&ui, id), 100);
    assert!(send(&mut ui, id, kind::MOUSE_UP, 10, 254));
}

#[test]
fn proportional_thumb_honours_the_authored_minimum() {
    let b = build(Scrollbar::default(), |b| {
        b.min_thumb_extent = 37.0;
    });
    assert_eq!(
        b.model_thumb_rect(&model(10_000, 100, 16), 17.0, 273.0).1,
        37.0
    );
}

#[test]
fn a_bar_linked_to_a_text_drives_the_text_model() {
    use ac_ui::widgets::Text;
    let mut ui = root(400.0, 400.0);
    let r = ui.root();
    let mut t = Text::default();
    t.scroll.line_height = 10;
    t.scroll.set_extents(400, 100, false);
    let text = ui.add(r, Element::at(0.0, 0.0, 100.0, 100.0), t);
    let id = bar_in(&mut ui, Scrollbar::linked(text), 100.0, 0.0, 16.0, 200.0);
    assert!(send(&mut ui, id, kind::MOUSE_DOWN, 8, 195));
    assert_eq!(ui.widget::<Text>(text).unwrap().scroll.scroll_y(), 10);
}

// ── Scrollable panel ────────────────────────────────────────────────────

fn panel_with_rows(
    ui: &mut UiRoot,
    w: f32,
    h: f32,
    rows: &[(f32, f32)],
) -> (ElementId, Vec<ElementId>) {
    let r = ui.root();
    let mut sp = ScrollablePanel::new();
    sp.line_height = 20;
    let p = ui.add(r, Element::at(0.0, 0.0, w, h), sp);
    let mut ids = Vec::new();
    for &(top, rh) in rows {
        let row = ui.insert(Element::at(0.0, 0.0, w, rh), Panel::default());
        ui.with::<ScrollablePanel, _>(p, |sp, ui| sp.add_row(ui, p, row, top));
        ids.push(row);
    }
    (p, ids)
}

fn layout(ui: &mut UiRoot, p: ElementId) {
    ui.with::<ScrollablePanel, _>(p, |sp, ui| sp.layout_rows(ui, p));
}

fn visible(ui: &UiRoot, id: ElementId) -> bool {
    ui.element(id).unwrap().visible()
}

#[test]
fn scrollable_panel_clips_rows_outside_the_viewport() {
    let mut ui = root(800.0, 600.0);
    let (p, rows) = panel_with_rows(
        &mut ui,
        100.0,
        40.0,
        &[(0.0, 20.0), (20.0, 20.0), (40.0, 20.0), (60.0, 20.0)],
    );
    layout(&mut ui, p);
    assert!(visible(&ui, rows[0]) && visible(&ui, rows[1]) && !visible(&ui, rows[2]));
    ui.widget_mut::<ScrollablePanel>(p)
        .unwrap()
        .scroll
        .set_scroll_y(20);
    layout(&mut ui, p);
    assert!(!visible(&ui, rows[0]) && visible(&ui, rows[1]) && visible(&ui, rows[2]));
}

#[test]
fn a_straddling_row_stays_visible() {
    let mut ui = root(800.0, 600.0);
    let (p, rows) = panel_with_rows(
        &mut ui,
        300.0,
        430.0,
        &[
            (0.0, 18.0),
            (18.0, 260.0),
            (278.0, 18.0),
            (296.0, 260.0),
            (556.0, 260.0),
        ],
    );
    layout(&mut ui, p);
    assert!(visible(&ui, rows[0]) && visible(&ui, rows[1]) && visible(&ui, rows[2]));
    assert!(visible(&ui, rows[3]));
    assert!(!visible(&ui, rows[4]));
    ui.widget_mut::<ScrollablePanel>(p)
        .unwrap()
        .scroll
        .set_scroll_y(296);
    layout(&mut ui, p);
    assert!(!visible(&ui, rows[1]));
    assert!(visible(&ui, rows[3]));
}

#[test]
fn the_viewport_clips_hit_testing() {
    let mut ui = root(800.0, 600.0);
    let (p, rows) = panel_with_rows(&mut ui, 300.0, 100.0, &[(60.0, 120.0)]);
    layout(&mut ui, p);
    assert!(visible(&ui, rows[0]));
    assert_eq!(ui.pick(150, 80), Some(rows[0]));
    assert_ne!(ui.pick(150, 140), Some(rows[0]));
}

#[test]
fn a_wheel_step_moves_by_the_line_height() {
    let mut ui = root(800.0, 600.0);
    let (p, _) = panel_with_rows(
        &mut ui,
        100.0,
        40.0,
        &[(0.0, 20.0), (20.0, 20.0), (40.0, 20.0), (60.0, 20.0)],
    );
    layout(&mut ui, p);
    let e = UiEvent {
        kind: kind::SCROLL,
        data0: -1,
        target: Some(p),
        ..UiEvent::default()
    };
    assert!(ui.send_event(p, &e));
    assert_eq!(
        ui.widget::<ScrollablePanel>(p).unwrap().scroll.scroll_y(),
        20
    );
}

// ── List box ────────────────────────────────────────────────────────────

fn list_box(ui: &mut UiRoot, w: f32, h: f32) -> ElementId {
    let info = ElementInfo {
        id: 0x1000_0279,
        kind: 5,
        template_list: vec![TemplateEntry {
            layout: 0x2100_0030,
            element: 0x1000_0281,
        }],
        scrollbar_element_id: 0x1000_027A,
        ..ElementInfo::default()
    };
    let r = ui.root();
    ui.add(r, Element::at(0.0, 0.0, w, h), ListBox::new(Arc::new(info)))
}

fn add_rows(ui: &mut UiRoot, b: ElementId, count: usize) {
    let w = ui.element(b).unwrap().width;
    for _ in 0..count {
        let row = ui.insert(Element::at(0.0, 0.0, w, 40.0), Panel::default());
        ui.with::<ListBox, _>(b, |lb, ui| lb.add_prebuilt_row(ui, b, row));
    }
}

fn lb_layout(ui: &mut UiRoot, b: ElementId) {
    let v = ui.widget::<ListBox>(b).unwrap().viewport_id().unwrap();
    layout(ui, v);
}

fn lb_scroll(ui: &mut UiRoot, b: ElementId) -> i32 {
    ui.scroll_model(b).unwrap().scroll_y()
}

#[test]
fn list_box_flush_resets_scroll() {
    let mut ui = root(800.0, 600.0);
    let b = list_box(&mut ui, 200.0, 100.0);
    add_rows(&mut ui, b, 10);
    lb_layout(&mut ui, b);
    ui.scroll_model_mut(b).unwrap().set_scroll_y(150);
    assert_eq!(lb_scroll(&mut ui, b), 150);
    ui.with::<ListBox, _>(b, |lb, ui| lb.flush(ui));
    assert_eq!(lb_scroll(&mut ui, b), 0);
}

#[test]
fn list_box_flush_preserving_scroll_keeps_then_clamps_the_offset() {
    let mut ui = root(800.0, 600.0);
    let b = list_box(&mut ui, 200.0, 100.0);
    add_rows(&mut ui, b, 10);
    lb_layout(&mut ui, b);
    ui.scroll_model_mut(b).unwrap().set_scroll_y(150);
    ui.with::<ListBox, _>(b, |lb, ui| lb.flush_preserving_scroll(ui));
    assert_eq!(lb_scroll(&mut ui, b), 150);
    add_rows(&mut ui, b, 10);
    lb_layout(&mut ui, b);
    assert_eq!(lb_scroll(&mut ui, b), 150);

    ui.scroll_model_mut(b).unwrap().set_scroll_y(300);
    ui.with::<ListBox, _>(b, |lb, ui| lb.flush_preserving_scroll(ui));
    add_rows(&mut ui, b, 2);
    lb_layout(&mut ui, b);
    assert_eq!(lb_scroll(&mut ui, b), 0);
}

#[test]
fn a_dormant_list_box_has_no_viewport() {
    let mut ui = root(800.0, 600.0);
    let b = list_box(&mut ui, 200.0, 100.0);
    ui.with::<ListBox, _>(b, |lb, ui| lb.flush_preserving_scroll(ui));
    assert!(ui.widget::<ListBox>(b).unwrap().viewport_id().is_none());
}

// ── The list box's viewport ──────────────────────────────────────────────
// OpenAC `tests/AcDream.App.Tests/UI/UiTemplateListBoxViewportTests.cs`.

fn row(ui: &mut UiRoot, b: ElementId, height: f32) -> ElementId {
    let row = ui.insert(Element::at(0.0, 0.0, 260.0, height), Panel::default());
    ui.with::<ListBox, _>(b, |lb, ui| lb.add_prebuilt_row(ui, b, row));
    row
}

fn viewport_of(ui: &UiRoot, b: ElementId) -> ElementId {
    ui.widget::<ListBox>(b).unwrap().viewport_id().unwrap()
}

#[test]
fn the_viewport_fills_the_list_box() {
    let mut ui = root(800.0, 600.0);
    let b = list_box(&mut ui, 276.0, 560.0);
    row(&mut ui, b, 20.0);
    row(&mut ui, b, 20.0);
    let v = viewport_of(&ui, b);
    ui.apply_anchor(v, 276.0, 560.0);
    let e = ui.element(v).unwrap();
    assert_eq!((e.width, e.height), (276.0, 560.0));
}

#[test]
fn rows_that_fit_stay_visible_after_layout() {
    let mut ui = root(800.0, 600.0);
    let b = list_box(&mut ui, 276.0, 560.0);
    let rows: Vec<ElementId> = (0..3).map(|_| row(&mut ui, b, 20.0)).collect();
    let v = viewport_of(&ui, b);
    ui.apply_anchor(v, 276.0, 560.0);
    layout(&mut ui, v);
    for r in rows {
        assert!(visible(&ui, r), "a row that fits was culled");
    }
}

#[test]
fn remove_tail_keeps_the_prefix_and_restacks_later_rows() {
    let mut ui = root(800.0, 600.0);
    let b = list_box(&mut ui, 276.0, 560.0);
    let prefix = row(&mut ui, b, 20.0);
    let removed_a = row(&mut ui, b, 30.0);
    let removed_b = row(&mut ui, b, 40.0);
    assert!(
        ui.with::<ListBox, _>(b, |lb, ui| lb.remove_tail(ui, 1))
            .unwrap()
    );
    let replacement = row(&mut ui, b, 25.0);
    assert_eq!(ui.widget::<ListBox>(b).unwrap().item_count(&ui), 2);
    let v = viewport_of(&ui, b);
    assert_eq!(ui.children(v), [prefix, replacement]);
    assert_eq!(ui.element(replacement).unwrap().top, 20.0);
    assert!(!ui.exists(removed_a) && !ui.exists(removed_b));
}

#[test]
fn the_viewport_follows_a_list_box_that_shrinks_on_its_first_layout() {
    use ac_ui::geometry::PixelRect;
    use ac_ui::layout_policy::LayoutPolicy;
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let host = ui.add(r, Element::at(0.0, 0.0, 300.0, 362.0), Panel::default());
    let mut slot_e = Element::at(2.0, 25.0, 298.0, 575.0);
    slot_e.layout_policy = Some(
        LayoutPolicy::new(
            1,
            1,
            1,
            1,
            PixelRect::from_position_and_size(2, 25, 298, 575),
            PixelRect::from_position_and_size(0, 0, 300, 600),
        )
        .unwrap(),
    );
    let slot = ui.add(host, slot_e, Panel::default());
    let info = ElementInfo {
        id: 0x1000_01FA,
        kind: 5,
        template_list: vec![TemplateEntry {
            layout: 0x2100_002B,
            element: 0x1000_0218,
        }],
        scrollbar_element_id: 0x1000_01FB,
        ..ElementInfo::default()
    };
    let mut box_e = Element::at(0.0, 0.0, 276.0, 560.0);
    box_e.layout_policy = Some(
        LayoutPolicy::new(
            1,
            1,
            1,
            1,
            PixelRect::from_position_and_size(0, 0, 276, 560),
            PixelRect::from_position_and_size(0, 0, 298, 575),
        )
        .unwrap(),
    );
    let b = ui.add(slot, box_e, ListBox::new(Arc::new(info)));
    row(&mut ui, b, 20.0);
    let v = viewport_of(&ui, b);
    ui.apply_anchor(slot, 300.0, 362.0);
    let (sw, sh) = ui.element(slot).map(|e| (e.width, e.height)).unwrap();
    ui.apply_anchor(b, sw, sh);
    let (bw, bh) = ui.element(b).map(|e| (e.width, e.height)).unwrap();
    ui.apply_anchor(v, bw, bh);
    let vh = ui.element(v).unwrap().height;
    assert_eq!(vh, bh);
    assert!(
        vh < 400.0,
        "the viewport ({vh}) did not shrink with its list box"
    );
}

// ── Row templates ─────────────────────────────────────────────────────────
// OpenAC `tests/AcDream.App.Tests/UI/Layout/RowTemplateResolverTests.cs`.

mod row_templates {
    use std::cell::Cell;

    use ac_ui::element_info::ElementInfo;
    use ac_ui::panels::{RowTemplateResolver, RowTemplates};
    use ac_ui::widgets::Text;

    use super::common::{at, root};

    fn info(element: u32) -> Option<ElementInfo> {
        Some(ElementInfo {
            id: element,
            kind: 0xC,
            ..ElementInfo::default()
        })
    }

    #[test]
    fn the_import_is_read_once_but_every_row_is_built_fresh() {
        let imports = Cell::new(0);
        let builds = Cell::new(0);
        let mut ui = root(800.0, 600.0);
        let mut resolver = RowTemplateResolver::new(
            |_, element| {
                imports.set(imports.get() + 1);
                info(element)
            },
            |ui: &mut ac_ui::root::UiRoot, _: &ElementInfo| {
                builds.set(builds.get() + 1);
                ui.insert(at(0.0, 0.0, 10.0, 10.0), Text::default())
            },
        );
        let rows: Vec<_> = (0..3)
            .map(|_| {
                resolver
                    .build_row(&mut ui, 0x2100_0030, 0x1000_0281)
                    .unwrap()
            })
            .collect();
        assert_eq!(imports.get(), 1);
        assert_eq!(resolver.import_count(), 1);
        assert_eq!(builds.get(), 3);
        assert_ne!(rows[0], rows[1]);
        assert_ne!(rows[1], rows[2]);
    }

    #[test]
    fn different_templates_import_independently() {
        let imports = Cell::new(0);
        let mut ui = root(800.0, 600.0);
        let mut resolver = RowTemplateResolver::new(
            |_, element| {
                imports.set(imports.get() + 1);
                info(element)
            },
            |ui: &mut ac_ui::root::UiRoot, _: &ElementInfo| {
                ui.insert(at(0.0, 0.0, 10.0, 10.0), Text::default())
            },
        );
        for (layout, element) in [
            (0x2100_0030, 0x1000_0281),
            (0x2100_005D, 0x1000_0519),
            (0x2100_0030, 0x1000_0281),
            (0x2100_005D, 0x1000_0519),
        ] {
            resolver.build_row(&mut ui, layout, element);
        }
        assert_eq!(imports.get(), 2);
    }

    #[test]
    fn a_missing_template_is_remembered_and_never_read_again() {
        let imports = Cell::new(0);
        let mut ui = root(800.0, 600.0);
        let mut resolver = RowTemplateResolver::new(
            |_, _| {
                imports.set(imports.get() + 1);
                None
            },
            |_: &mut ac_ui::root::UiRoot, _: &ElementInfo| -> ac_ui::ElementId {
                panic!("nothing is built for a missing template")
            },
        );
        assert!(
            resolver
                .build_row(&mut ui, 0x2100_0030, 0x1000_0281)
                .is_none()
        );
        assert!(
            resolver
                .build_row(&mut ui, 0x2100_0030, 0x1000_0281)
                .is_none()
        );
        assert_eq!(imports.get(), 1);
    }
}
