//! The plugins' world icons: where each goes on the screen, and how few
//! draw runs they cost.

mod common;

use std::f32::consts::PI;

use ac_ui::draw::{Color, DrawCmd, NoSprites, TextureRef};
use ac_ui::overlay::{
    IconPlacement, OverlayCamera, OverlayHost, WorldIcon, WorldIconAnchor, WorldIconLayer,
    WorldIconOverlay, WorldLabel, WorldLabelAnchor, fit_icon, place_icons,
};
use common::root;
use glam::Vec3;

const LINE_HEIGHT: f32 = 16.0;
const ART: TextureRef = TextureRef::Runtime(1);

/// The camera stands at the origin looking along +Y with +Z up, so an
/// object at (0, d, 0) is d metres away, straight ahead. A head 2 m up at
/// 10 m away projects to (400, 240) on an 800x600 screen.
fn camera() -> OverlayCamera {
    OverlayCamera {
        view: glam::camera::rh::view::look_at_mat4(Vec3::ZERO, Vec3::Y, Vec3::Z),
        projection: glam::camera::rh::proj::directx::perspective(PI / 2.0, 4.0 / 3.0, 0.1, 100.0),
        viewport: (800.0, 600.0),
    }
}

fn over(id: u32) -> WorldIcon {
    WorldIcon {
        anchor: WorldIconAnchor::Object(id),
        image: Some((ART, 32.0, 32.0)),
        size: 24.0,
        tint: Color::WHITE,
        border: None,
        max_range: 60.0,
    }
}

fn at_point(point: Vec3) -> WorldIcon {
    WorldIcon {
        anchor: WorldIconAnchor::Point(point),
        ..over(0)
    }
}

fn standing_at(distance: f32) -> Option<WorldLabelAnchor> {
    Some(WorldLabelAnchor {
        base: Vec3::new(0.0, distance, 0.0),
        height: 2.0,
    })
}

fn label(id: u32, line: u32, offset: f32) -> WorldLabel {
    WorldLabel {
        object_id: id,
        text: "name".to_owned(),
        color: Color::WHITE,
        height_offset: offset,
        line,
        max_range: 60.0,
        outline: true,
    }
}

fn place(
    icons: &[WorldIcon],
    anchor: &dyn Fn(u32) -> Option<WorldLabelAnchor>,
    labels: &[WorldLabel],
) -> Vec<IconPlacement> {
    let mut out = Vec::new();
    place_icons(icons, anchor, labels, LINE_HEIGHT, &camera(), &mut out);
    out
}

fn close(a: f32, b: f32) -> bool {
    (a - b).abs() < 1e-3
}

#[test]
fn an_icon_sits_centred_over_the_head_with_its_bottom_just_above_it() {
    let placed = place(&[over(1)], &|_| standing_at(10.0), &[]);
    let [only] = placed.as_slice() else {
        panic!("{placed:?}");
    };
    // Head at (400, 240); the row's bottom is a 2-pixel gap above it.
    assert!(close(only.x, 388.0), "{only:?}");
    assert!(close(only.y, 214.0), "{only:?}");
    assert!(close(only.depth, 10.0), "{only:?}");
    assert_eq!(only.alpha, 1.0);
}

#[test]
fn icons_sit_above_the_objects_labels() {
    let labels = [label(1, 1, 0.0), label(2, 7, 0.0)];
    let placed = place(&[over(1)], &|_| standing_at(10.0), &labels);
    assert!(close(placed[0].y, 240.0 - 2.0 * LINE_HEIGHT - 2.0 - 24.0));
}

#[test]
fn icons_rise_with_the_labels_height_offset_in_metres() {
    let placed = place(&[over(1)], &|_| standing_at(10.0), &[label(1, 0, 1.0)]);
    // A label raised 1 m hangs from 3 m up, which at 10 m projects to
    // y = 210 rather than 240; the row sits a gap above its one line.
    assert!(close(placed[0].y, 210.0 - LINE_HEIGHT - 2.0 - 24.0));
}

#[test]
fn a_negative_or_unusable_label_offset_leaves_the_icons_where_they_were() {
    for offset in [-1.0, f32::NAN, f32::INFINITY] {
        let placed = place(&[over(1)], &|_| standing_at(10.0), &[label(1, 0, offset)]);
        assert!(
            close(placed[0].y, 240.0 - LINE_HEIGHT - 2.0 - 24.0),
            "{offset}"
        );
    }
}

#[test]
fn an_objects_icons_are_centred_in_a_row_with_gaps() {
    let placed = place(&[over(1), over(1), over(1)], &|_| standing_at(10.0), &[]);
    // Three of 24 and two gaps of 2 make 76, centred on 400.
    let xs: Vec<f32> = placed.iter().map(|p| p.x).collect();
    assert_eq!(xs, [362.0, 388.0, 414.0]);
    assert!(placed.iter().all(|p| close(p.y, 214.0)));
}

#[test]
fn the_ninth_icon_starts_a_new_row_above() {
    let placed = place(&[over(1); 9], &|_| standing_at(10.0), &[]);
    assert_eq!(placed.len(), 9);
    assert!(placed[..8].iter().all(|p| close(p.y, 214.0)));
    assert!(close(placed[8].y, 214.0 - 24.0 - 2.0));
    assert!(close(placed[8].x, 388.0));
}

#[test]
fn icons_of_different_sizes_share_the_rows_bottom() {
    let big = WorldIcon {
        size: 40.0,
        ..over(1)
    };
    let placed = place(&[over(1), big], &|_| standing_at(10.0), &[]);
    assert!(close(placed[0].y + 24.0, 238.0));
    assert!(close(placed[1].y + 40.0, 238.0));
}

#[test]
fn an_objects_icons_keep_the_order_given_even_when_other_icons_come_between() {
    let placed = place(
        &[over(1), over(2), over(1)],
        &|id| (id == 1).then(|| standing_at(10.0)).flatten(),
        &[],
    );
    let order: Vec<usize> = placed.iter().map(|p| p.icon).collect();
    assert_eq!(order, [0, 2]);
    assert!(placed[0].x < placed[1].x);
}

#[test]
fn an_icon_past_its_range_is_cut_and_the_row_closes_up() {
    let short = WorldIcon {
        max_range: 5.0,
        ..over(1)
    };
    let placed = place(&[short, over(1)], &|_| standing_at(10.0), &[]);
    assert_eq!(placed.len(), 1);
    assert_eq!(placed[0].icon, 1);
    assert!(close(placed[0].x, 388.0));
}

#[test]
fn the_last_fifth_of_the_range_fades() {
    let faded = WorldIcon {
        max_range: 11.0,
        ..over(1)
    };
    let placed = place(&[faded], &|_| standing_at(10.0), &[]);
    // The fade runs from 8.8 m to 11 m.
    assert!((placed[0].alpha - (1.0 / 2.2)).abs() < 1e-3);
}

#[test]
fn a_missing_object_an_object_behind_the_camera_and_an_icon_off_screen_are_not_placed() {
    assert!(place(&[over(1)], &|_| None, &[]).is_empty());
    assert!(place(&[over(1)], &|_| standing_at(-10.0), &[]).is_empty());
    let aside = |_| {
        Some(WorldLabelAnchor {
            base: Vec3::new(50.0, 10.0, 0.0),
            height: 2.0,
        })
    };
    assert!(place(&[over(1)], &aside, &[]).is_empty());
}

#[test]
fn a_positions_icon_is_centred_on_it() {
    let placed = place(&[at_point(Vec3::new(0.0, 10.0, 2.0))], &|_| None, &[]);
    assert!(close(placed[0].x, 388.0));
    assert!(close(placed[0].y, 228.0));
    assert!(close(placed[0].depth, 10.0));
}

#[test]
fn placements_run_far_to_near_then_by_index() {
    let near = Vec3::new(0.0, 5.0, 0.0);
    let far = Vec3::new(0.0, 20.0, 0.0);
    let placed = place(
        &[at_point(near), at_point(far), at_point(near)],
        &|_| None,
        &[],
    );
    let order: Vec<usize> = placed.iter().map(|p| p.icon).collect();
    assert_eq!(order, [1, 0, 2]);
}

#[test]
fn an_image_is_fitted_into_its_square_keeping_its_shape() {
    assert_eq!(fit_icon(24.0, (32.0, 32.0)), (0.0, 0.0, 24.0, 24.0));
    assert_eq!(fit_icon(24.0, (32.0, 16.0)), (0.0, 6.0, 24.0, 12.0));
    assert_eq!(fit_icon(24.0, (16.0, 32.0)), (6.0, 0.0, 12.0, 24.0));
}

/// The fills and the sprites' textures of one frame, and how many icons
/// were placed.
fn draw_once(icons: &[WorldIcon]) -> (usize, Vec<TextureRef>, usize) {
    let mut ui = root(800.0, 600.0);
    let mut host = OverlayHost::mount(&mut ui);
    let mut overlay = WorldIconOverlay::mount(&mut ui, &mut host, LINE_HEIGHT);
    overlay.tick(&mut ui, icons, &|_| standing_at(10.0), &[], Some(&camera()));
    let placed = ui
        .widget::<WorldIconLayer>(overlay.element())
        .unwrap()
        .placement_count();
    let list = ui.draw((800.0, 600.0), &NoSprites);
    let mut fills = 0;
    let mut sprites = Vec::new();
    for c in &list.main {
        match c {
            DrawCmd::Fill { .. } => {
                assert!(sprites.is_empty(), "every border comes first");
                fills += 1;
            }
            DrawCmd::Sprite { texture, .. } => sprites.push(*texture),
            _ => {}
        }
    }
    (fills, sprites, placed)
}

#[test]
fn every_border_is_drawn_before_every_image() {
    let framed = WorldIcon {
        border: Some(Color::WHITE),
        ..over(1)
    };
    let (fills, sprites, placed) = draw_once(&[framed; 4]);
    assert_eq!(placed, 4);
    assert_eq!(fills, 16);
    assert_eq!(sprites, [ART; 4]);
}

#[test]
fn an_icon_whose_image_does_not_resolve_is_placed_but_draws_nothing() {
    let gone = WorldIcon {
        image: None,
        border: Some(Color::WHITE),
        ..over(1)
    };
    let (fills, sprites, placed) = draw_once(&[gone, over(1)]);
    assert_eq!(placed, 2);
    assert_eq!(fills, 0);
    assert_eq!(sprites, [ART]);
}

#[test]
fn nothing_placed_hides_the_layer() {
    let (fills, sprites, placed) = draw_once(&[]);
    assert_eq!((fills, sprites.len(), placed), (0, 0, 0));
}
