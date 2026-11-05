//! A shape drawn in the tree takes the width of its soft edge from the pixel
//! scale the window gave the root.

mod common;

use ac_ui::draw::{Color, DrawCmd, DrawList, NoSprites, RenderContext};
use ac_ui::root::UiRoot;
use ac_ui::shapes::CornerRadii;
use ac_ui::tree::{ElementId, Widget};

use common::{at, root};

/// Draws one square-cornered rectangle inside its element, so the element's
/// clip leaves its soft edge whole.
#[derive(Debug)]
struct Square;

impl Widget for Square {
    fn draw(&mut self, _ui: &mut UiRoot, _me: ElementId, rc: &mut RenderContext<'_>) {
        rc.fill_rounded_rect(5.0, 5.0, 10.0, 10.0, CornerRadii::default(), Color::WHITE);
    }
}

/// How far the rectangle's soft edge reaches left of its true edge, x = 15.
fn reach(list: &DrawList) -> f32 {
    let left = list
        .commands()
        .flat_map(|cmd| match cmd {
            DrawCmd::Triangles { vertices } => vertices.iter().map(|v| v.x).collect(),
            _ => Vec::new(),
        })
        .fold(f32::MAX, f32::min);
    15.0 - left
}

fn with_square() -> UiRoot {
    let mut ui = root(800.0, 600.0);
    let parent = ui.root();
    ui.add(parent, at(10.0, 10.0, 20.0, 20.0), Square);
    ui
}

#[test]
fn the_soft_edge_is_a_framebuffer_pixel_at_the_scale_the_root_was_given() {
    let mut ui = with_square();
    assert_eq!(reach(&ui.draw((800.0, 600.0), &NoSprites)), 0.5);
    ui.set_pixels_per_unit(2.0);
    assert_eq!(reach(&ui.draw((800.0, 600.0), &NoSprites)), 0.25);
}

#[test]
fn a_fixed_canvas_counts_towards_the_scale() {
    let mut ui = with_square();
    ui.set_pixels_per_unit(2.0);
    ui.declare_fixed_canvas(1, (400.0, 300.0))
        .expect("a canvas");
    // Two screen units a canvas unit, two pixels a screen unit.
    assert_eq!(reach(&ui.draw((800.0, 600.0), &NoSprites)), 0.125);
}

#[test]
fn a_canvas_stretched_unevenly_keeps_the_edge_within_a_pixel() {
    let mut ui = with_square();
    ui.declare_fixed_canvas(1, (400.0, 600.0))
        .expect("a canvas");
    // Two pixels a canvas unit across, one down: a pixel of the larger.
    assert_eq!(reach(&ui.draw((800.0, 600.0), &NoSprites)), 0.25);
}
