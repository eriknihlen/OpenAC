//! The overlay band, the world labels and the vivid target indicator.
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/UiOverlayHostTests.cs`,
//! `WorldLabelOverlayLayoutTests.cs`, `WorldLabelOverlayDrawTests.cs` and
//! `VividTargetIndicatorLayoutTests.cs`. OpenAC's System.Numerics matrices
//! (`CreateLookAt`, `CreatePerspectiveFieldOfView`) are glam's right-handed
//! look-at and perspective (depth 0 to 1); its draw-call count is the number
//! of runs of one texture in the draw list.

mod common;

use std::f32::consts::PI;
use std::sync::Arc;

use ac_dat::objects::FontCharDesc;
use ac_ui::draw::{Color, DrawCmd, NoSprites, TextureRef};
use ac_ui::element_info::ElementInfo;
use ac_ui::font::{Atlas, UiFont};
use ac_ui::geometry::AnchorEdges;
use ac_ui::import::{BuildContext, LayoutSource, build};
use ac_ui::overlay::{
    LabelPlacement, OverlayCamera, OverlayHost, TargetProjection, VividTargetInfo, WorldLabel,
    WorldLabelAnchor, WorldLabelLayer, WorldLabelOverlay, compute_projection, fade,
    off_screen_image, off_screen_position, place_labels, target_layout, target_tint, z_order,
};
use ac_ui::radar::{RelationshipTraits, colors};
use ac_ui::root::UiRoot;
use ac_ui::tree::Element;
use ac_ui::widgets::Panel;
use common::{at, root};
use glam::{Mat4, Vec2, Vec3};

// ── The band ──────────────────────────────────────────────────────────────

struct NoSource;

impl LayoutSource for NoSource {
    fn layout(&self, _id: u32) -> Option<Arc<ac_dat::objects::LayoutDesc>> {
        None
    }
}

/// The z-order the importer gives an element at this level and read order.
fn imported_z(z_level: i32, read_order: u32) -> i32 {
    let mut ui = root(800.0, 600.0);
    let info = ElementInfo {
        kind: 3,
        read_order,
        z_level,
        ..ElementInfo::default()
    };
    let ctx = BuildContext {
        source: &NoSource,
        default_font: None,
        source_layout_did: 0,
        media_seconds: 0.0,
    };
    let l = build(&mut ui, &info, &ctx);
    ui.element(l.root).unwrap().z_order()
}

#[test]
fn the_band_fits_between_the_z_orders_root_children_take() {
    let windows = [
        imported_z(0, 0),
        imported_z(0, 40),
        Element::default().z_order(),
    ];
    for w in windows {
        assert!(z_order::BAND_CEILING < w, "under a window at {w}");
    }
    assert!(imported_z(1, 0) < z_order::BAND_FLOOR);
    assert!((z_order::BAND_FLOOR..=z_order::BAND_CEILING).contains(&z_order::SHARED_HOST_ROOT));
}

#[test]
fn the_host_root_sits_at_the_very_back_of_the_band() {
    assert_eq!(z_order::SHARED_HOST_ROOT, z_order::BAND_FLOOR);
}

const _: () = assert!(z_order::BAND_FLOOR < z_order::BAND_CEILING);

#[test]
fn the_host_paints_over_a_debug_overlay_and_under_every_window() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let debug = ui.add(
        r,
        at(0.0, 0.0, 10.0, 10.0).with_z_order(imported_z(1, 0)),
        Panel::default(),
    );
    let imported = ui.add(
        r,
        at(0.0, 0.0, 10.0, 10.0).with_z_order(imported_z(0, 0)),
        Panel::default(),
    );
    let code_built = ui.add(r, at(0.0, 0.0, 10.0, 10.0), Panel::default());
    ui.register_window("imported", imported, None, 0).unwrap();
    ui.register_window("code-built", code_built, None, 0)
        .unwrap();
    let host = OverlayHost::mount(&mut ui);
    let z = |id| ui.element(id).unwrap().z_order();
    assert!(z(debug) < z(host.root()));
    assert!(z(host.root()) < z(imported));
    assert!(z(host.root()) < z(code_built));
    ui.bring_to_front(imported);
    assert!(ui.element(host.root()).unwrap().z_order() < ui.element(imported).unwrap().z_order());
}

#[test]
fn the_root_takes_the_interface_rectangle_at_mount() {
    let mut ui = root(800.0, 600.0);
    let host = OverlayHost::mount(&mut ui);
    let e = ui.element(host.root()).unwrap();
    assert_eq!((e.left, e.top, e.width, e.height), (0.0, 0.0, 800.0, 600.0));
    assert_eq!(e.anchors, AnchorEdges::NONE);
    assert!(e.click_through);
}

#[test]
fn a_new_root_size_reaches_the_root_and_every_layer_even_later_ones() {
    // ANewRootSizeReachesTheRootAndEveryLayerIncludingOnesAddedLater
    // (OpenAC v0.1.25): the root is resized behind the band's
    // back, as restoring the layout on entering the world resizes it.
    let mut ui = root(800.0, 600.0);
    let mut host = OverlayHost::mount(&mut ui);
    let first = host.add_layer(&mut ui, "First");
    ui.set_size(1920.0, 1080.0);
    host.follow_root(&mut ui);
    let second = host.add_layer(&mut ui, "Second");
    assert_eq!(host.viewport(), (1920.0, 1080.0));
    for id in [host.root(), first, second] {
        let e = ui.element(id).unwrap();
        assert_eq!(
            (e.left, e.top, e.width, e.height),
            (0.0, 0.0, 1920.0, 1080.0)
        );
    }
}

#[test]
fn a_layer_starts_hidden_click_through_and_anchorless() {
    let mut ui = root(800.0, 600.0);
    let mut host = OverlayHost::mount(&mut ui);
    let layer = host.add_layer(&mut ui, "Layer");
    let e = ui.element(layer).unwrap();
    assert!(!e.visible());
    assert!(e.click_through);
    assert_eq!(e.anchors, AnchorEdges::NONE);
    assert_eq!(ui.parent(layer), Some(host.root()));
}

#[test]
fn a_subtree_added_to_a_layer_is_click_through_all_the_way_down() {
    let mut ui = root(800.0, 600.0);
    let mut host = OverlayHost::mount(&mut ui);
    let layer = host.add_layer(&mut ui, "Layer");
    let branch = ui.insert(at(0.0, 0.0, 10.0, 10.0), Panel::default());
    let leaf = ui.add(branch, at(0.0, 0.0, 5.0, 5.0), Panel::default());
    assert!(!ui.element(branch).unwrap().click_through);
    OverlayHost::add_to_layer(&mut ui, layer, branch, false);
    assert!(ui.element(branch).unwrap().click_through);
    assert!(ui.element(leaf).unwrap().click_through);
}

#[test]
fn two_layers_keep_their_own_visibility_and_order() {
    let mut ui = root(800.0, 600.0);
    let mut host = OverlayHost::mount(&mut ui);
    let first = host.add_layer(&mut ui, "First");
    let second = host.add_layer(&mut ui, "Second");
    ui.set_visible(first, true);
    assert!(ui.element(first).unwrap().visible());
    assert!(!ui.element(second).unwrap().visible());
    assert_eq!(ui.children(host.root()), [first, second]);
}

// ── World labels: where they go ───────────────────────────────────────────

const LINE_HEIGHT: f32 = 16.0;

/// The camera at the origin looking along +Y with +Z up; a 90 degree
/// vertical field of view.
fn camera() -> OverlayCamera {
    OverlayCamera {
        view: glam::camera::rh::view::look_at_mat4(Vec3::ZERO, Vec3::Y, Vec3::Z),
        projection: glam::camera::rh::proj::directx::perspective(PI / 2.0, 4.0 / 3.0, 0.1, 100.0),
        viewport: (800.0, 600.0),
    }
}

fn label(id: u32, text: &str, line: u32, range: f32, offset: f32) -> WorldLabel {
    WorldLabel {
        object_id: id,
        text: text.to_owned(),
        color: Color::WHITE,
        height_offset: offset,
        line,
        max_range: range,
        outline: true,
    }
}

fn plain(id: u32) -> WorldLabel {
    label(id, "name", 0, 60.0, 0.0)
}

fn standing_at(distance: f32, height: f32) -> Option<WorldLabelAnchor> {
    Some(WorldLabelAnchor {
        base: Vec3::new(0.0, distance, 0.0),
        height,
    })
}

fn place(
    labels: &[WorldLabel],
    anchor: &dyn Fn(u32) -> Option<WorldLabelAnchor>,
) -> Vec<LabelPlacement> {
    let mut out = Vec::new();
    place_labels(
        labels,
        anchor,
        &camera(),
        LINE_HEIGHT,
        &|s: &str| s.chars().count() as f32 * 10.0,
        &mut out,
    );
    out
}

fn close(a: f32, b: f32) -> bool {
    (a - b).abs() < 1e-3
}

#[test]
fn the_label_is_centred_over_the_head_with_its_bottom_on_it() {
    let placed = place(&[plain(1)], &|_| standing_at(10.0, 2.0));
    assert_eq!(placed.len(), 1);
    let p = placed[0];
    assert!(close(p.x, 400.0 - 20.0), "{}", p.x);
    assert!(close(p.y, 240.0 - LINE_HEIGHT), "{}", p.y);
    assert!(close(p.depth, 10.0));
    assert_eq!(p.alpha, 1.0);
}

#[test]
fn the_height_offset_raises_the_label_in_metres() {
    let plain_y = place(&[plain(1)], &|_| standing_at(10.0, 2.0))[0].y;
    let raised_y = place(&[label(1, "name", 0, 60.0, 1.0)], &|_| {
        standing_at(10.0, 2.0)
    })[0]
        .y;
    assert!(close(plain_y, 240.0 - LINE_HEIGHT));
    assert!(close(raised_y, 210.0 - LINE_HEIGHT));
}

#[test]
fn out_of_range_unknown_and_behind_the_camera_are_not_placed() {
    assert!(
        place(&[label(1, "name", 0, 9.99, 0.0)], &|_| standing_at(
            10.0, 2.0
        ))
        .is_empty()
    );
    assert!(place(&[plain(1)], &|_| None).is_empty());
    assert!(place(&[plain(1)], &|_| standing_at(-10.0, 2.0)).is_empty());
}

#[test]
fn the_range_is_view_space_depth_in_metres() {
    assert_eq!(
        place(&[label(1, "name", 0, 10.0, 0.0)], &|_| standing_at(
            10.0, 2.0
        ))
        .len(),
        1
    );
    assert!(
        place(&[label(1, "name", 0, 10.0, 0.0)], &|_| standing_at(
            10.01, 2.0
        ))
        .is_empty()
    );
}

#[test]
fn the_fade_covers_the_last_fifth_of_the_range() {
    for (depth, expected) in [(5.0, 1.0), (8.0, 1.0), (9.0, 0.5), (9.5, 0.25), (10.0, 0.0)] {
        assert!((fade(depth, 10.0) - expected).abs() < 1e-4, "{depth}");
        let p = place(&[label(1, "name", 0, 10.0, 0.0)], &|_| {
            standing_at(depth, 2.0)
        });
        assert_eq!(p.len(), 1);
        assert!((p[0].alpha - expected).abs() < 1e-4, "{depth}");
    }
}

#[test]
fn placements_come_far_to_near() {
    let anchor = |id: u32| match id {
        1 => standing_at(5.0, 2.0),
        2 => standing_at(20.0, 2.0),
        3 => standing_at(10.0, 2.0),
        _ => None,
    };
    let placed = place(&[plain(1), plain(2), plain(3)], &anchor);
    assert_eq!(
        placed.iter().map(|p| p.label).collect::<Vec<_>>(),
        [1, 2, 0]
    );
    let depths: Vec<f32> = placed.iter().map(|p| p.depth.round()).collect();
    assert_eq!(depths, [20.0, 10.0, 5.0]);
}

#[test]
fn a_second_line_stacks_one_line_above_the_first() {
    let placed = place(
        &[
            label(1, "name", 0, 60.0, 0.0),
            label(1, "12 m", 1, 60.0, 0.0),
        ],
        &|_| standing_at(10.0, 2.0),
    );
    assert_eq!(placed.len(), 2);
    let first = placed.iter().find(|p| p.label == 0).unwrap();
    let second = placed.iter().find(|p| p.label == 1).unwrap();
    assert!(close(second.y, first.y - LINE_HEIGHT));
    assert_eq!(first.depth, second.depth);
}

#[test]
fn the_output_list_is_reused() {
    let mut out = Vec::new();
    let measure = |s: &str| s.len() as f32 * 10.0;
    place_labels(
        &[plain(1)],
        &|_| standing_at(10.0, 2.0),
        &camera(),
        LINE_HEIGHT,
        &measure,
        &mut out,
    );
    assert_eq!(out.len(), 1);
    place_labels(
        &[],
        &|_| standing_at(10.0, 2.0),
        &camera(),
        LINE_HEIGHT,
        &measure,
        &mut out,
    );
    assert!(out.is_empty());
}

// ── World labels: how they are drawn ──────────────────────────────────────

const FOREGROUND: TextureRef = TextureRef::Surface(1);
const BACKGROUND: TextureRef = TextureRef::Surface(2);

fn font() -> Arc<UiFont> {
    let glyph = |c: char| FontCharDesc {
        unicode: c as u16,
        offset_x: 4,
        offset_y: 4,
        width: 8,
        height: 8,
        ..FontCharDesc::default()
    };
    let atlas = |texture| Atlas {
        texture,
        width: 64,
        height: 64,
    };
    Arc::new(UiFont::new(
        1,
        atlas(FOREGROUND),
        Some(atlas(BACKGROUND)),
        16.0,
        12.0,
        [glyph('A'), glyph('B')],
        4,
        4,
    ))
}

fn anchor(id: u32) -> Option<WorldLabelAnchor> {
    (id != 0).then(|| WorldLabelAnchor {
        base: Vec3::new((id % 8) as f32 - 4.0, 8.0 + (id % 5) as f32, 0.0),
        height: 1.5,
    })
}

fn labels(count: u32, outline: bool) -> Vec<WorldLabel> {
    (1..=count)
        .map(|i| WorldLabel {
            outline,
            ..label(i, "AB", 0, 60.0, 0.0)
        })
        .collect()
}

/// The glyph runs (texture, glyph quads) of one frame.
fn draw_once(labels: &[WorldLabel]) -> (Vec<(TextureRef, usize)>, usize) {
    let mut ui = root(800.0, 600.0);
    let mut host = OverlayHost::mount(&mut ui);
    let mut overlay = WorldLabelOverlay::mount(&mut ui, &mut host, Some(font()));
    overlay.tick(&mut ui, labels, &anchor, Some(&camera()));
    let count = ui
        .widget::<WorldLabelLayer>(overlay.element())
        .unwrap()
        .placement_count();
    let list = ui.draw((800.0, 600.0), &NoSprites);
    let mut runs: Vec<(TextureRef, usize)> = Vec::new();
    for c in &list.main {
        if let DrawCmd::Glyphs { texture, quads, .. } = c {
            match runs.last_mut() {
                Some((t, n)) if t == texture => *n += quads.len(),
                _ => runs.push((*texture, quads.len())),
            }
        }
    }
    (runs, count)
}

#[test]
fn the_whole_set_is_two_runs_however_many_labels() {
    for count in [1, 10, 100] {
        let (runs, placed) = draw_once(&labels(count, true));
        assert_eq!(placed, count as usize);
        assert_eq!(runs.len(), 2, "{count}");
        assert_eq!(runs[0].0, BACKGROUND);
        assert_eq!(runs[0].1, count as usize * 2);
        assert_eq!(runs[1].0, FOREGROUND);
        assert_eq!(runs[1].1, count as usize * 2);
    }
}

#[test]
fn labels_without_an_outline_are_one_run() {
    let (runs, _) = draw_once(&labels(20, false));
    assert_eq!(runs, [(FOREGROUND, 40)]);
}

#[test]
fn a_mix_of_outlined_and_plain_labels_is_still_two_runs() {
    let mut mixed = labels(30, true);
    for l in mixed.iter_mut().step_by(2) {
        l.outline = false;
    }
    let (runs, _) = draw_once(&mixed);
    assert_eq!(runs, [(BACKGROUND, 30), (FOREGROUND, 60)]);
}

#[test]
fn an_empty_set_draws_nothing() {
    let (runs, placed) = draw_once(&[]);
    assert!(runs.is_empty());
    assert_eq!(placed, 0);
}

#[test]
fn what_is_drawn_changes_only_on_a_tick() {
    let mut ui = root(800.0, 600.0);
    let mut host = OverlayHost::mount(&mut ui);
    let mut overlay = WorldLabelOverlay::mount(&mut ui, &mut host, Some(font()));
    let element = overlay.element();
    let count = |ui: &UiRoot| {
        ui.widget::<WorldLabelLayer>(element)
            .unwrap()
            .placement_count()
    };
    overlay.tick(&mut ui, &labels(3, true), &anchor, Some(&camera()));
    assert_eq!(count(&ui), 3);
    overlay.tick(&mut ui, &[], &anchor, Some(&camera()));
    assert_eq!(count(&ui), 0);
    overlay.tick(&mut ui, &labels(5, true), &anchor, Some(&camera()));
    assert_eq!(count(&ui), 5);
}

#[test]
fn labels_stay_up_while_a_camera_and_their_anchor_are_there() {
    // OpenAC `WorldLabelOverlayController.Tick` reads only the plugins'
    // labels, the anchors (`WorldSelectionQuery.TryResolveWorldLabelAnchor`,
    // the live entity) and the game camera (`SelectionCameraSource`, never
    // the portal tunnel's). Here only a missing camera, anchor or label takes
    // it down; the window passes no labels while the portal view shows.
    let mut ui = root(800.0, 600.0);
    let mut host = OverlayHost::mount(&mut ui);
    let mut overlay = WorldLabelOverlay::mount(&mut ui, &mut host, Some(font()));
    let element = overlay.element();
    let count = |ui: &UiRoot| {
        ui.widget::<WorldLabelLayer>(element)
            .unwrap()
            .placement_count()
    };
    let labels = [plain(7)];
    let here = |_: u32| standing_at(10.0, 2.0);
    for _ in 0..3 {
        overlay.tick(&mut ui, &labels, &here, Some(&camera()));
        assert_eq!(count(&ui), 1);
    }
    overlay.tick(&mut ui, &labels, &here, None);
    assert_eq!(count(&ui), 0);
    overlay.tick(&mut ui, &labels, &|_| None, Some(&camera()));
    assert_eq!(count(&ui), 0);
}

// ── The vivid target ──────────────────────────────────────────────────────

const CORNERS: [Vec2; 4] = [Vec2::new(12.0, 12.0); 4];

#[test]
fn the_corners_sit_outside_the_selection_square() {
    let l = target_layout(
        Vec2::new(100.0, 80.0),
        Vec2::new(180.0, 200.0),
        Vec2::new(800.0, 600.0),
        CORNERS,
    );
    assert_eq!(l.position, Vec2::new(88.0, 68.0));
    assert_eq!(l.size, Vec2::new(104.0, 144.0));
    assert_eq!(
        l.corners,
        [
            Vec2::ZERO,
            Vec2::new(92.0, 0.0),
            Vec2::new(92.0, 132.0),
            Vec2::new(0.0, 132.0)
        ]
    );
}

#[test]
fn the_whole_indicator_stays_eight_pixels_inside_the_screen() {
    let l = target_layout(
        Vec2::new(2.0, 3.0),
        Vec2::new(62.0, 83.0),
        Vec2::new(200.0, 150.0),
        CORNERS,
    );
    assert_eq!(l.position, Vec2::new(8.0, 8.0));
    assert_eq!(l.size, Vec2::new(66.0, 87.0));
}

fn ahead_camera() -> OverlayCamera {
    OverlayCamera {
        view: Mat4::IDENTITY,
        projection: glam::camera::rh::proj::directx::perspective(PI / 2.0, 4.0 / 3.0, 0.1, 100.0),
        viewport: (800.0, 600.0),
    }
}

#[test]
fn the_projection_has_no_distance_gate() {
    match compute_projection(Vec3::new(0.0, 0.0, -1_000_000.0), 1.0, &ahead_camera()) {
        TargetProjection::OnScreen { min, max } => {
            let c = (min + max) * 0.5;
            assert!(
                (c.x - 400.0).abs() < 0.1 && (c.y - 300.0).abs() < 0.1,
                "{c}"
            );
        }
        other => panic!("{other:?}"),
    }
}

#[test]
fn the_off_screen_direction_follows_the_viewers_axes() {
    for (x, y, z, angle, image) in [
        (100.0, 0.0, -10.0, 0.0, 6),
        (0.0, 100.0, -10.0, 90.0, 9),
        (-100.0, 0.0, -10.0, 180.0, 11),
        (0.0, -100.0, -10.0, 270.0, 8),
    ] {
        match compute_projection(Vec3::new(x, y, z), 1.0, &ahead_camera()) {
            TargetProjection::OffScreen { angle: a } => {
                assert!((a - angle).abs() < 1e-3, "{a} for {angle}");
                assert_eq!(off_screen_image(a), image);
            }
            other => panic!("{other:?}"),
        }
    }
}

#[test]
fn the_off_screen_arrow_stops_eight_pixels_from_the_edge() {
    for (angle, left, top) in [
        (0.0, 768.0, 288.0),
        (90.0, 388.0, 8.0),
        (180.0, 8.0, 288.0),
        (270.0, 388.0, 568.0),
    ] {
        let p = off_screen_position(angle, Vec2::new(800.0, 600.0), Vec2::new(24.0, 24.0));
        assert!(
            (p.x - left).abs() < 1e-3 && (p.y - top).abs() < 1e-3,
            "{angle}: {p}"
        );
    }
}

#[test]
fn the_arrow_image_follows_the_retail_sectors() {
    for (angle, image) in [
        (0.0, 6),
        (22.999, 6),
        (23.0, 7),
        (68.0, 9),
        (113.0, 12),
        (158.0, 11),
        (203.0, 10),
        (248.0, 8),
        (293.0, 5),
        (338.0, 6),
    ] {
        assert_eq!(off_screen_image(angle), image, "{angle}");
    }
}

const PLAYER_BIT: u32 = 0x0000_0008;
const ADMIN_BIT: u32 = 0x0010_0000;

fn target(item_type: u32, flags: u32, blip: u8) -> VividTargetInfo {
    VividTargetInfo {
        center: Vec3::ZERO,
        radius: 0.0,
        item_type,
        description_flags: flags,
        blip_color: blip,
    }
}

#[test]
fn the_tint_is_the_radars_colour() {
    let member = RelationshipTraits {
        is_fellowship_member: true,
        ..RelationshipTraits::default()
    };
    let leader = RelationshipTraits {
        is_fellowship_leader: true,
        ..RelationshipTraits::default()
    };
    assert_eq!(
        target_tint(&target(0, PLAYER_BIT, 0), &member),
        colors::BRIGHT_GREEN
    );
    assert_eq!(
        target_tint(&target(0, PLAYER_BIT, 0), &leader),
        colors::BRIGHT_GREEN
    );
    assert_eq!(
        target_tint(&target(0, PLAYER_BIT, 0), &RelationshipTraits::default()),
        colors::WHITE
    );
    assert_eq!(
        target_tint(&target(0x10, 0, 8), &RelationshipTraits::default()),
        colors::YELLOW,
        "the server's blip colour wins"
    );
    assert_eq!(
        target_tint(&target(0, PLAYER_BIT | ADMIN_BIT, 0), &member),
        colors::BRIGHT_GREEN
    );
}

/// The shown indicator draws its four tinted corners and nothing else: its
/// frame, the overlay band's root and its layers are transparent (OpenAC's
/// `VividTargetIndicatorController.Mount` and `UiOverlayLayer` give them a
/// zero background and border), so no dark square fills the brackets and
/// nothing dims the screen.
#[test]
fn the_shown_indicator_draws_only_its_four_corners() {
    use ac_ui::overlay::{TargetImage, VIVID_TARGET_IMAGE_COUNT, VividTargetIndicator};
    let mut ui = root(800.0, 600.0);
    let mut host = OverlayHost::mount(&mut ui);
    let layer = host.add_layer(&mut ui, "Layer");
    ui.set_visible(layer, true);
    let images: [TargetImage; VIVID_TARGET_IMAGE_COUNT] = std::array::from_fn(|i| TargetImage {
        file: 0x0600_1000 + i as u32,
        size: Vec2::new(12.0, 12.0),
    });
    let indicator = VividTargetIndicator::mount(&mut ui, images);
    let ahead = VividTargetInfo {
        center: Vec3::new(0.0, 0.0, -10.0),
        radius: 1.0,
        item_type: 0,
        description_flags: PLAYER_BIT,
        blip_color: 0,
    };
    indicator.tick(
        &mut ui,
        Some(&ahead),
        &RelationshipTraits::default(),
        Some(&ahead_camera()),
    );
    let (on_screen, _, _) = indicator.elements();
    assert!(
        ui.element(on_screen).unwrap().visible,
        "shown on the screen"
    );
    let list = ui.draw((800.0, 600.0), &NoSprites);
    let sprites: Vec<TextureRef> = list
        .main
        .iter()
        .filter_map(|c| match c {
            DrawCmd::Sprite { texture, .. } => Some(*texture),
            _ => None,
        })
        .collect();
    assert_eq!(
        sprites,
        (0..4)
            .map(|i| TextureRef::Surface(0x0600_1000 + i))
            .collect::<Vec<_>>()
    );
    let others: Vec<&DrawCmd> = list
        .main
        .iter()
        .filter(|c| !matches!(c, DrawCmd::Sprite { .. }))
        .collect();
    assert!(others.is_empty(), "nothing but the corners: {others:?}");
}

mod real_dats {
    use std::path::PathBuf;

    use ac_ui::overlay::load_vivid_target_images;

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_twelve_indicator_images_are_in_the_dats() {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        let dats = ac_dat::DatCollection::open(dir).unwrap();
        let images = load_vivid_target_images(&dats).expect("all twelve");
        assert!(images.iter().all(|i| i.file != 0 && i.size.x > 0.0));
    }
}

// ── Screen projection (OpenAC `tests/AcDream.Core.Tests/Selection/ScreenProjectionTests.cs`) ──

use ac_ui::overlay::project_sphere;

/// OpenAC's standard camera: identity view (looking down -Z), a 90 degree
/// vertical field of view, 800 x 600.
fn standard_camera() -> OverlayCamera {
    OverlayCamera {
        view: Mat4::IDENTITY,
        projection: glam::camera::rh::proj::directx::perspective(
            PI * 0.5,
            800.0 / 600.0,
            0.1,
            100.0,
        ),
        viewport: (800.0, 600.0),
    }
}

#[test]
fn a_sphere_in_front_projects_to_a_centred_square() {
    let rect =
        project_sphere(Vec3::new(0.0, 0.0, -10.0), 1.0, &standard_camera(), 0.0).expect("in front");
    let (w, h) = (rect.max.x - rect.min.x, rect.max.y - rect.min.y);
    assert!((w - h).abs() < 1e-3);
    assert!((399.0..=401.0).contains(&((rect.min.x + rect.max.x) * 0.5)));
    assert!((299.0..=301.0).contains(&((rect.min.y + rect.max.y) * 0.5)));
    assert!(rect.depth > 0.0);
}

#[test]
fn a_sphere_behind_the_camera_does_not_project() {
    assert!(project_sphere(Vec3::new(0.0, 0.0, 10.0), 1.0, &standard_camera(), 0.0).is_none());
}

#[test]
fn a_far_sphere_is_held_to_the_minimum_side() {
    let rect = project_sphere(Vec3::new(0.0, 0.0, -90.0), 0.01, &standard_camera(), 12.0)
        .expect("in front");
    assert!(rect.max.x - rect.min.x >= 12.0);
    assert!(rect.max.y - rect.min.y >= 12.0);
}

// ── World lines (OpenAC draws its debug lines in the scene; here they go
// on a layer of the band) ────────────────────────────────────────────────

use ac_ui::overlay::{WorldLine, WorldLineLayer, WorldLineOverlay, project_segment};

#[test]
fn a_segment_in_front_projects_to_the_screen() {
    let (a, b) = project_segment(
        Vec3::new(0.0, 0.0, -10.0),
        Vec3::new(5.0, 0.0, -10.0),
        &standard_camera(),
    )
    .expect("in front");
    assert!((a - Vec2::new(400.0, 300.0)).length() < 0.01);
    // A 90 degree field of view, 600 pixels high: 5 m at 10 m is 150 px.
    assert!((b - Vec2::new(550.0, 300.0)).length() < 0.01, "{b}");
}

#[test]
fn a_segment_crossing_the_camera_is_cut_where_it_passes_the_near_depth() {
    let (a, b) = project_segment(
        Vec3::new(0.0, -1.0, -10.0),
        Vec3::new(0.0, -1.0, 10.0),
        &standard_camera(),
    )
    .expect("partly in front");
    assert!((a - Vec2::new(400.0, 330.0)).length() < 0.01, "{a}");
    // The cut end is far below the screen, never flipped above it.
    assert!(b.y > 600.0, "{b}");
    assert!(
        project_segment(
            Vec3::new(0.0, 0.0, 1.0),
            Vec3::new(1.0, 0.0, 5.0),
            &standard_camera()
        )
        .is_none()
    );
}

#[test]
fn world_lines_draw_on_their_own_band_layer_only_while_there_are_any() {
    let mut ui = root(800.0, 600.0);
    let mut host = OverlayHost::mount(&mut ui);
    let mut overlay = WorldLineOverlay::mount(&mut ui, &mut host, "NavigationOverlay");
    let magenta = Color::rgba(1.0, 0.0, 1.0, 1.0);
    let lines = [WorldLine {
        from: Vec3::new(0.0, 0.0, -10.0),
        to: Vec3::new(5.0, 0.0, -10.0),
        color: magenta,
    }];
    overlay.tick(&mut ui, &lines, Some(&standard_camera()));
    assert!(ui.element(overlay.layer()).unwrap().visible);
    assert_eq!(
        ui.widget::<WorldLineLayer>(overlay.element())
            .unwrap()
            .segments()
            .len(),
        1
    );
    let list = ui.draw((800.0, 600.0), &NoSprites);
    assert!(
        list.main
            .iter()
            .any(|c| matches!(c, DrawCmd::Polygon { color, .. } if *color == magenta))
    );
    assert!(ui.element(overlay.element()).unwrap().click_through);

    overlay.tick(&mut ui, &[], Some(&standard_camera()));
    assert!(!ui.element(overlay.layer()).unwrap().visible);
    overlay.tick(&mut ui, &lines, None);
    assert!(!ui.element(overlay.layer()).unwrap().visible);
}
