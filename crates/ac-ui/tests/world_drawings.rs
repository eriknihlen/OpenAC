//! The plugins' world drawings: where each goes on the screen, how it
//! shrinks with distance, and what it draws there.

mod common;

use std::collections::HashMap;
use std::f32::consts::PI;
use std::sync::Arc;

use ac_dat::objects::FontCharDesc;
use ac_plugin_ui::fonts::{FontWeight, bake_bundled};
use ac_plugin_ui::{Color8, DrawingScale, PaintCmd, PaintRect};
use ac_ui::draw::{DrawCmd, NoSprites, TextureRef};
use ac_ui::font::{Atlas, UiFont};
use ac_ui::overlay::{OverlayCamera, OverlayHost};
use ac_ui::plugin::canvas::CanvasFont;
use ac_ui::plugin::world_drawings::{
    DrawingPlacement, DrawingResources, WorldDrawingItem, WorldDrawingLayer, WorldDrawingOverlay,
    place_drawings,
};
use common::root;
use glam::Vec3;

const ART: TextureRef = TextureRef::Runtime(1);
const ATLAS: TextureRef = TextureRef::Runtime(2);
const INTERFACE: TextureRef = TextureRef::Runtime(3);

/// The camera stands at the origin looking along +Y with +Z up, so a point
/// at (0, d, 0) is d metres away, straight ahead. A point 2 m up at 10 m
/// away projects to (400, 240) on an 800x600 screen.
fn camera() -> OverlayCamera {
    OverlayCamera {
        view: glam::camera::rh::view::look_at_mat4(Vec3::ZERO, Vec3::Y, Vec3::Z),
        projection: glam::camera::rh::proj::directx::perspective(PI / 2.0, 4.0 / 3.0, 0.1, 100.0),
        viewport: (800.0, 600.0),
    }
}

fn rect(x: f32, y: f32, w: f32, h: f32) -> PaintRect {
    PaintRect { x, y, w, h }
}

/// A 100x20 plate standing on a point 2 m up, `distance` metres ahead.
fn plate(distance: f32) -> WorldDrawingItem {
    WorldDrawingItem {
        at: Vec3::new(0.0, distance, 2.0),
        size: (100.0, 20.0),
        pivot: (50.0, 20.0),
        paint: Arc::new(vec![PaintCmd::FillRect {
            rect: rect(0.0, 0.0, 100.0, 20.0),
            color: Color8::new(0, 0, 0, 255),
        }]),
        scale: DrawingScale::Fixed,
        max_range: 60.0,
        resources: 0,
    }
}

fn place(drawings: &[WorldDrawingItem]) -> Vec<DrawingPlacement> {
    let mut out = Vec::new();
    place_drawings(drawings, &camera(), &mut out);
    out
}

fn close(a: f32, b: f32) -> bool {
    (a - b).abs() < 1e-3
}

#[test]
fn the_pivot_sits_on_the_anchor() {
    let placed = place(&[plate(10.0)]);
    assert_eq!(placed.len(), 1);
    let p = placed[0];
    assert_eq!((p.x, p.y), (350.0, 220.0));
    assert!(close(p.depth, 10.0));
    assert_eq!((p.scale, p.alpha), (1.0, 1.0));
    // The top-left corner as the pivot hangs the drawing below and right.
    let corner = WorldDrawingItem {
        pivot: (0.0, 0.0),
        ..plate(10.0)
    };
    let p = place(&[corner])[0];
    assert_eq!((p.x, p.y), (400.0, 240.0));
}

#[test]
fn a_fixed_drawing_keeps_its_size_and_one_with_distance_shrinks() {
    let shrinking = DrawingScale::WithDistance {
        near: 10.0,
        far: 30.0,
        smallest: 0.5,
    };
    assert_eq!(shrinking.at(5.0), 1.0);
    assert_eq!(shrinking.at(10.0), 1.0);
    assert!(close(shrinking.at(20.0), 0.75));
    assert_eq!(shrinking.at(30.0), 0.5);
    assert_eq!(shrinking.at(55.0), 0.5);
    assert_eq!(DrawingScale::Fixed.at(55.0), 1.0);

    let far = plate(20.0);
    let fixed = place(std::slice::from_ref(&far))[0];
    let shrunk = place(&[WorldDrawingItem {
        scale: shrinking,
        ..far
    }])[0];
    assert_eq!(fixed.scale, 1.0);
    assert!(close(shrunk.scale, 0.75));
    // The pivot stays on the anchor: the box shrinks towards it.
    let anchor_x = fixed.x + 50.0;
    let anchor_y = fixed.y + 20.0;
    assert!((shrunk.x + 50.0 * 0.75 - anchor_x).abs() <= 0.5);
    assert!((shrunk.y + 20.0 * 0.75 - anchor_y).abs() <= 0.5);
}

#[test]
fn past_its_range_behind_the_camera_or_off_the_screen_it_is_left_out() {
    let short = WorldDrawingItem {
        max_range: 8.0,
        ..plate(10.0)
    };
    let behind = WorldDrawingItem {
        at: Vec3::new(0.0, -10.0, 2.0),
        ..plate(10.0)
    };
    let aside = WorldDrawingItem {
        at: Vec3::new(40.0, 10.0, 2.0),
        ..plate(10.0)
    };
    assert!(place(&[short, behind, aside]).is_empty());
    // Half way through the last fifth of its range, half faded.
    let fading = WorldDrawingItem {
        max_range: 11.0,
        ..plate(10.0)
    };
    assert!(close(place(&[fading])[0].alpha, 1.0 - (10.0 - 8.8) / 2.2));
}

#[test]
fn the_farthest_is_drawn_first_and_then_in_the_order_given() {
    let placed = place(&[plate(10.0), plate(30.0), plate(10.0)]);
    let order: Vec<usize> = placed.iter().map(|p| p.drawing).collect();
    assert_eq!(order, [1, 0, 2]);
}

/// The draw commands of one frame of `drawings`, with how many were placed.
fn draw_once(
    drawings: Vec<WorldDrawingItem>,
    resources: Vec<DrawingResources>,
) -> (Vec<DrawCmd>, usize) {
    draw_once_with(None, drawings, resources)
}

/// [`draw_once`] with the interface font (font 0).
fn draw_once_with(
    interface_font: Option<Arc<UiFont>>,
    drawings: Vec<WorldDrawingItem>,
    resources: Vec<DrawingResources>,
) -> (Vec<DrawCmd>, usize) {
    let mut ui = root(800.0, 600.0);
    let mut host = OverlayHost::mount(&mut ui);
    let mut overlay = WorldDrawingOverlay::mount(&mut ui, &mut host, interface_font);
    overlay.tick(&mut ui, drawings, Arc::new(resources), Some(&camera()));
    let placed = ui
        .widget::<WorldDrawingLayer>(overlay.element())
        .unwrap()
        .placement_count();
    let list = ui.draw((800.0, 600.0), &NoSprites);
    (list.main, placed)
}

fn fills(commands: &[DrawCmd]) -> Vec<(f32, f32, f32, f32, f32)> {
    commands
        .iter()
        .filter_map(|c| match c {
            DrawCmd::Fill { x, y, w, h, color } => Some((*x, *y, *w, *h, color.a)),
            _ => None,
        })
        .collect()
}

#[test]
fn a_drawing_paints_its_commands_at_its_place_clipped_to_its_box() {
    let spilling = WorldDrawingItem {
        paint: Arc::new(vec![
            PaintCmd::FillRect {
                rect: rect(0.0, 0.0, 100.0, 20.0),
                color: Color8::new(0, 0, 0, 255),
            },
            // Past the right and bottom of the box.
            PaintCmd::FillRect {
                rect: rect(90.0, 10.0, 50.0, 50.0),
                color: Color8::new(255, 0, 0, 255),
            },
        ]),
        ..plate(10.0)
    };
    let (commands, placed) = draw_once(vec![spilling], vec![DrawingResources::default()]);
    assert_eq!(placed, 1);
    assert_eq!(
        fills(&commands),
        [
            (350.0, 220.0, 100.0, 20.0, 1.0),
            (440.0, 230.0, 10.0, 10.0, 1.0)
        ]
    );
}

#[test]
fn a_shrunk_drawing_scales_its_shapes_and_a_faded_one_its_colours() {
    let far = WorldDrawingItem {
        scale: DrawingScale::WithDistance {
            near: 10.0,
            far: 30.0,
            smallest: 0.5,
        },
        max_range: 31.0,
        ..plate(30.0)
    };
    let (commands, _) = draw_once(vec![far], vec![DrawingResources::default()]);
    let drawn = fills(&commands);
    assert_eq!(drawn.len(), 1);
    let (_, _, w, h, a) = drawn[0];
    assert_eq!((w, h), (50.0, 10.0));
    assert!(a > 0.0 && a < 1.0, "in the last fifth of its range: {a}");
}

#[test]
fn images_and_fonts_come_from_the_drawings_own_plugin() {
    let font = CanvasFont {
        font: Arc::new(bake_bundled(FontWeight::Regular, 16.0, &[(32, 126)]).unwrap()),
        texture: ATLAS,
        sharp: None,
    };
    let with_font = DrawingResources {
        images: HashMap::from([(7, (ART, 32.0, 32.0))]),
        fonts: HashMap::from([(3, font)]),
    };
    let paint = Arc::new(vec![
        PaintCmd::Image {
            image: 7,
            rect: rect(0.0, 0.0, 20.0, 20.0),
            tint: Color8::WHITE,
        },
        PaintCmd::Text {
            text: "Drudge".to_owned(),
            at: (24.0, 0.0),
            color: Color8::WHITE,
            outline: false,
            font: 3,
        },
    ]);
    let mine = WorldDrawingItem {
        paint: Arc::clone(&paint),
        resources: 0,
        ..plate(10.0)
    };
    // Another plugin's drawing names the same handles, which it does not
    // hold: it draws nothing.
    let theirs = WorldDrawingItem {
        paint,
        resources: 1,
        ..plate(10.0)
    };
    let (commands, placed) = draw_once(
        vec![mine, theirs],
        vec![with_font, DrawingResources::default()],
    );
    assert_eq!(placed, 2);
    let sprites: Vec<_> = commands
        .iter()
        .filter_map(|c| match c {
            DrawCmd::Sprite { texture, quad, .. } => Some((*texture, quad.x, quad.y, quad.w)),
            _ => None,
        })
        .collect();
    assert_eq!(sprites, [(ART, 350.0, 220.0, 20.0)]);
    let glyph_runs: Vec<_> = commands
        .iter()
        .filter_map(|c| match c {
            DrawCmd::Glyphs { texture, quads, .. } => Some((*texture, quads.len())),
            _ => None,
        })
        .collect();
    assert_eq!(glyph_runs, [(ATLAS, 6)]);
}

#[test]
fn text_in_a_plugin_font_shrinks_with_its_drawing() {
    let font = CanvasFont {
        font: Arc::new(bake_bundled(FontWeight::Regular, 16.0, &[(32, 126)]).unwrap()),
        texture: ATLAS,
        sharp: None,
    };
    let resources = || DrawingResources {
        images: HashMap::new(),
        fonts: HashMap::from([(1, font.clone())]),
    };
    let named = |scale| WorldDrawingItem {
        paint: Arc::new(vec![PaintCmd::Text {
            text: "W".to_owned(),
            at: (0.0, 0.0),
            color: Color8::WHITE,
            outline: false,
            font: 1,
        }]),
        scale,
        ..plate(30.0)
    };
    let glyph = |commands: &[DrawCmd]| {
        commands
            .iter()
            .find_map(|c| match c {
                DrawCmd::Glyphs { quads, .. } => Some((quads[0].w, quads[0].h)),
                _ => None,
            })
            .unwrap()
    };
    let (full, _) = draw_once(vec![named(DrawingScale::Fixed)], vec![resources()]);
    let half = DrawingScale::WithDistance {
        near: 10.0,
        far: 30.0,
        smallest: 0.5,
    };
    let (shrunk, _) = draw_once(vec![named(half)], vec![resources()]);
    let (w, h) = glyph(&full);
    assert_eq!(glyph(&shrunk), (w * 0.5, h * 0.5));
}

/// Text in the interface font is drawn at the font's own size, so it keeps
/// its size when its drawing shrinks; the plate around it shrinks.
#[test]
fn text_in_the_interface_font_keeps_its_size() {
    let interface = Arc::new(UiFont::new(
        1,
        Atlas {
            texture: INTERFACE,
            width: 64,
            height: 64,
        },
        None,
        16.0,
        12.0,
        [FontCharDesc {
            unicode: u16::from(b'W'),
            width: 8,
            height: 10,
            ..FontCharDesc::default()
        }],
        0,
        0,
    ));
    let named = |scale| WorldDrawingItem {
        paint: Arc::new(vec![
            PaintCmd::FillRect {
                rect: rect(0.0, 0.0, 100.0, 20.0),
                color: Color8::new(0, 0, 0, 255),
            },
            PaintCmd::Text {
                text: "W".to_owned(),
                at: (0.0, 0.0),
                color: Color8::WHITE,
                outline: false,
                font: 0,
            },
        ]),
        scale,
        ..plate(30.0)
    };
    let glyph = |commands: &[DrawCmd]| {
        commands
            .iter()
            .find_map(|c| match c {
                DrawCmd::Glyphs { texture, quads, .. } if *texture == INTERFACE => {
                    Some((quads[0].w, quads[0].h))
                }
                _ => None,
            })
            .unwrap()
    };
    let half = DrawingScale::WithDistance {
        near: 10.0,
        far: 30.0,
        smallest: 0.5,
    };
    let (full, _) = draw_once_with(
        Some(interface.clone()),
        vec![named(DrawingScale::Fixed)],
        vec![DrawingResources::default()],
    );
    let (shrunk, _) = draw_once_with(
        Some(interface),
        vec![named(half)],
        vec![DrawingResources::default()],
    );
    assert_eq!(glyph(&shrunk), glyph(&full));
    assert_eq!(glyph(&full), (8.0, 10.0));
    let plate_width = |commands: &[DrawCmd]| fills(commands)[0].2;
    assert!(close(plate_width(&shrunk), plate_width(&full) * 0.5));
}

#[test]
fn nothing_placed_hides_the_layer() {
    let (commands, placed) = draw_once(Vec::new(), Vec::new());
    assert_eq!((commands.len(), placed), (0, 0));
}
