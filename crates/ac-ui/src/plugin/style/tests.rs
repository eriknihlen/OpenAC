//! The shapes and colours the modern look is built from.

use super::*;
use crate::draw::{DrawCmd, DrawList, NoSprites};
use crate::event::UiEvent;
use crate::tree::ElementId;

const P: Palette = Palette::MOSS;

/// Every corner a draw list has, with its colour.
pub(crate) fn corners(list: &DrawList) -> Vec<(f32, f32, Color)> {
    let mut out = Vec::new();
    for command in list.commands() {
        match command {
            DrawCmd::Triangles { vertices } => {
                out.extend(vertices.iter().map(|v| (v.x, v.y, v.color)));
            }
            DrawCmd::Fill { x, y, w, h, color } => {
                out.extend([
                    (*x, *y, *color),
                    (x + w, *y, *color),
                    (x + w, y + h, *color),
                    (*x, y + h, *color),
                ]);
            }
            _ => {}
        }
    }
    out
}

/// Whether a corner is in `color`, whatever its alpha: a shape's soft edge
/// keeps the colour and fades only the alpha.
pub(crate) fn has_color(corners: &[(f32, f32, Color)], color: Color) -> bool {
    corners.iter().any(|(_, _, c)| same_rgb(*c, color))
}

/// The same colour, whatever the alpha.
pub(crate) fn same_rgb(a: Color, b: Color) -> bool {
    (a.r - b.r).abs() < 0.004 && (a.g - b.g).abs() < 0.004 && (a.b - b.b).abs() < 0.004
}

fn draw(f: impl FnOnce(&mut RenderContext<'_>)) -> Vec<(f32, f32, Color)> {
    let mut list = DrawList::default();
    let mut rc = RenderContext::new(&mut list, (800.0, 600.0), &NoSprites);
    f(&mut rc);
    drop(rc);
    corners(&list)
}

fn event(kind: i32) -> UiEvent {
    let id = ElementId {
        index: 0,
        generation: 0,
    };
    UiEvent::new(0, id, kind)
}

#[test]
fn derived_colours_follow_the_palette() {
    assert_eq!(hover(&P, P.field), mix(P.field, P.border, 0.30));
    assert_eq!(pressed(P.field), mix(P.field, Color::BLACK, 0.25));
    assert_eq!(focus_glow(&P).a, 0.30);
    assert_eq!(focus_ring_color(&P).a, 0.60);
    assert!((faded(P.field).a - P.field.a * 0.45).abs() < 1e-5);
    assert_eq!(hover(&P, P.field).a, P.field.a);
}

#[test]
fn a_button_draws_its_look_colour() {
    for (look, expected) in [
        (ButtonLook::Normal, P.field),
        (ButtonLook::Hovered, hover(&P, P.field)),
        (ButtonLook::Pressed, pressed(P.field)),
    ] {
        let v = draw(|rc| button(rc, &P, (0.0, 0.0, 80.0, 24.0), P.field, P.border, look));
        assert!(has_color(&v, expected), "{look:?}");
    }
}

#[test]
fn a_disabled_button_is_faded() {
    let look = ButtonLook::Disabled;
    let v = draw(|rc| button(rc, &P, (0.0, 0.0, 80.0, 24.0), P.field, P.border, look));
    assert!(!v.is_empty());
    assert!(v.iter().all(|(_, _, c)| c.a <= DISABLED_ALPHA + 1e-4));
}

#[test]
fn the_switch_knob_sits_on_the_side_of_its_value() {
    for on in [true, false] {
        let knob = if on { P.text } else { P.muted };
        let v = draw(|rc| switch(rc, &P, 0.0, 0.0, on, true));
        let xs: Vec<f32> = v
            .iter()
            .filter(|(_, _, c)| c.a > 0.99 && same_rgb(*c, knob))
            .map(|(x, _, _)| *x)
            .collect();
        assert!(!xs.is_empty());
        let centre = xs.iter().sum::<f32>() / xs.len() as f32;
        assert_eq!(centre > SWITCH_WIDTH / 2.0, on);
    }
}

#[test]
fn pointer_state_follows_hover_and_press() {
    let mut s = PointerState::default();
    assert_eq!(s.look(true), ButtonLook::Normal);
    s.observe(&event(kind::HOVER_ENTER));
    assert_eq!(s.look(true), ButtonLook::Hovered);
    s.observe(&event(kind::MOUSE_DOWN));
    assert_eq!(s.look(true), ButtonLook::Pressed);
    s.observe(&event(kind::HOVER_LEAVE));
    assert_eq!(s.look(true), ButtonLook::Normal);
    s.observe(&event(kind::HOVER_ENTER));
    assert_eq!(s.look(true), ButtonLook::Pressed);
    s.observe(&event(kind::MOUSE_UP));
    assert_eq!(s.look(true), ButtonLook::Hovered);
    assert_eq!(s.look(false), ButtonLook::Disabled);
}

#[test]
fn a_shape_with_no_size_draws_nothing() {
    let v = draw(|rc| surface(rc, 0.0, 0.0, 0.0, 10.0, 4.0, P.field, P.border));
    assert!(v.is_empty());
    let v = draw(|rc| surface(rc, 0.0, 0.0, f32::NAN, 10.0, 4.0, P.field, P.border));
    assert!(v.is_empty());
}

#[test]
fn the_slider_thumb_stays_on_its_track_at_both_ends() {
    for centre in [-50.0, 0.0, 100.0, 500.0] {
        let v = draw(|rc| slider(rc, &P, 100.0, 16.0, centre, false));
        let thumb: Vec<f32> = v
            .iter()
            .filter(|(_, _, c)| c.a > 0.99 && same_rgb(*c, P.text))
            .map(|(x, _, _)| *x)
            .collect();
        assert!(thumb.iter().all(|x| (-1.0..=101.0).contains(x)), "{centre}");
    }
}

#[test]
fn the_header_band_ends_in_its_separator() {
    let v = draw(|rc| header(rc, &P, 200.0, HEADER_HEIGHT));
    assert!(has_color(&v, mix(P.background, P.text, 0.06)));
    assert!(
        v.iter()
            .any(|(_, y, c)| *y == HEADER_HEIGHT && same_rgb(*c, P.border))
    );
}

#[test]
fn the_grip_is_three_dots_in_the_corner() {
    let v = draw(|rc| resize_grip(rc, &P, 100.0, 80.0));
    assert!(has_color(&v, P.muted));
    assert!(v.iter().all(|(x, y, _)| *x > 80.0 && *y > 70.0));
}
