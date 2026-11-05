//! The projectile debug overlay.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/ProjectileDebugOverlayControllerTests.cs`
//! and `ProjectileDebugOverlayBehaviourPinTests.cs`.

use glam::{Mat4, Vec3};

use ac_ui::draw::Color;
use ac_ui::geometry::AnchorEdges;
use ac_ui::overlay::{OverlayCamera, OverlayHost};
use ac_ui::panels::projectile_debug::{
    BLOCKED_COLOR, CLEAR_COLOR, LAYER_NAME, ProjectileDebugOverlay, ProjectileDebugSample,
};
use ac_ui::root::UiRoot;
use ac_ui::tree::ElementId;
use ac_ui::widgets::Panel;

const VIEWPORT: (f32, f32) = (800.0, 600.0);

fn camera(viewport: (f32, f32)) -> OverlayCamera {
    OverlayCamera {
        view: Mat4::IDENTITY,
        projection: glam::camera::rh::proj::directx::perspective(
            std::f32::consts::FRAC_PI_2,
            4.0 / 3.0,
            0.1,
            100.0,
        ),
        viewport,
    }
}

fn sample(x: f32, clear: bool) -> ProjectileDebugSample {
    ProjectileDebugSample {
        world_position: Vec3::new(x, 0.0, -10.0),
        is_clear: clear,
        radius: 0.4,
    }
}

struct Fixture {
    ui: UiRoot,
    host: OverlayHost,
    overlay: ProjectileDebugOverlay,
}

fn fixture() -> Fixture {
    let mut ui = UiRoot::new();
    ui.set_size(VIEWPORT.0, VIEWPORT.1);
    let mut host = OverlayHost::mount(&mut ui);
    let overlay = ProjectileDebugOverlay::mount(&mut ui, &mut host);
    Fixture { ui, host, overlay }
}

// AWorldOverlayDrawingIntoADifferentViewportLeavesTheLayersAtTheScreenSize
// (OpenAC v0.1.25): a world overlay projects into its camera's
// viewport but never resizes the band.
#[test]
fn a_world_overlay_drawing_into_another_viewport_leaves_the_layers_at_the_screen_size() {
    let mut ui = UiRoot::new();
    ui.set_size(1920.0, 1080.0);
    let mut host = OverlayHost::mount(&mut ui);
    let canvases = host.add_layer(&mut ui, "Canvases");
    let mut overlay = ProjectileDebugOverlay::mount(&mut ui, &mut host);
    overlay.tick(&mut ui, &[sample(0.0, true)], Some(&camera(VIEWPORT)));
    for id in [host.root(), canvases] {
        let e = ui.element(id).unwrap();
        assert_eq!((e.width, e.height), (1920.0, 1080.0));
    }
}

impl Fixture {
    fn tick(&mut self, samples: &[ProjectileDebugSample], viewport: (f32, f32)) {
        let c = camera(viewport);
        self.overlay.tick(&mut self.ui, samples, Some(&c));
    }

    fn visible(&self, id: ElementId) -> bool {
        self.ui.element(id).unwrap().visible
    }

    fn panel(&self, id: ElementId) -> &Panel {
        self.ui.widget::<Panel>(id).unwrap()
    }
}

#[test]
fn clear_and_blocked_samples_project_without_taking_input() {
    let mut f = fixture();
    f.tick(&[sample(0.0, true), sample(1.0, false)], VIEWPORT);
    let layer = f.overlay.layer();
    // The layer hangs off the band's host, the root's one overlay child.
    assert_eq!(f.ui.parent(layer), Some(f.host.root()));
    assert_eq!(
        f.ui.element(layer).unwrap().name.as_deref(),
        Some(LAYER_NAME)
    );
    assert!(f.visible(layer));
    assert!(f.ui.element(layer).unwrap().click_through);
    let markers = f.ui.children(layer);
    assert_eq!(markers.len(), 2);
    assert!(f.visible(markers[0]) && f.visible(markers[1]));
    assert_eq!(f.panel(markers[0]).border_color, CLEAR_COLOR);
    assert_eq!(f.panel(markers[1]).border_color, BLOCKED_COLOR);
    let clear = f.ui.element(markers[0]).unwrap();
    assert!((380.0..=400.0).contains(&clear.left), "{}", clear.left);
    assert!((280.0..=300.0).contains(&clear.top), "{}", clear.top);

    f.tick(&[], VIEWPORT);
    assert!(!f.visible(layer));
    assert!(markers.iter().all(|m| !f.visible(*m)));
}

#[test]
fn the_layer_and_every_marker_refuse_input_and_paint_no_chrome() {
    let mut f = fixture();
    f.tick(&[sample(0.0, true)], VIEWPORT);
    let layer = f.overlay.layer();
    assert!(f.ui.element(layer).unwrap().click_through);
    assert_eq!(
        f.panel(layer).background_color,
        Color::rgba(0.0, 0.0, 0.0, 0.0)
    );
    assert_eq!(f.panel(layer).border_color, Color::rgba(0.0, 0.0, 0.0, 0.0));
    for m in f.ui.children(layer) {
        assert!(f.ui.element(m).unwrap().click_through);
    }
    // Every step from the root down is click-through.
    let mut at = f.ui.parent(layer);
    while let Some(e) = at.filter(|e| *e != f.ui.root()) {
        assert!(f.ui.element(e).unwrap().click_through);
        at = f.ui.parent(e);
    }
}

#[test]
fn the_layer_covers_the_camera_viewport() {
    let mut f = fixture();
    f.tick(&[sample(0.0, true)], VIEWPORT);
    let e = f.ui.element(f.overlay.layer()).unwrap();
    assert_eq!((e.left, e.top, e.width, e.height), (0.0, 0.0, 800.0, 600.0));
    assert_eq!(e.anchors, AnchorEdges::NONE);
}

#[test]
fn markers_take_their_colours_and_stay_inside_the_viewport() {
    let mut f = fixture();
    f.tick(&[sample(0.0, true), sample(1.0, false)], VIEWPORT);
    for m in f.ui.children(f.overlay.layer()) {
        let e = f.ui.element(m).unwrap();
        assert!(e.visible);
        let p = f.panel(m);
        assert_eq!(p.border_thickness, 1.5);
        assert_eq!(p.background_color, Color::rgba(0.0, 0.0, 0.0, 0.0));
        assert!((0.0..=800.0).contains(&e.left) && (0.0..=800.0).contains(&(e.left + e.width)));
        assert!((0.0..=600.0).contains(&e.top) && (0.0..=600.0).contains(&(e.top + e.height)));
    }
}

#[test]
fn fewer_samples_hide_the_surplus_markers_instead_of_dropping_them() {
    let mut f = fixture();
    f.tick(&[sample(0.0, true), sample(1.0, false)], VIEWPORT);
    f.tick(&[sample(0.0, true)], VIEWPORT);
    let markers = f.ui.children(f.overlay.layer());
    assert_eq!(markers.len(), 2);
    assert!(f.visible(markers[0]));
    assert!(!f.visible(markers[1]));
    assert!(f.visible(f.overlay.layer()));
}

#[test]
fn a_degenerate_viewport_hides_everything() {
    let mut f = fixture();
    f.tick(&[sample(0.0, true)], VIEWPORT);
    assert!(f.visible(f.overlay.layer()));
    f.tick(&[sample(0.0, true)], (0.0, 0.0));
    assert!(!f.visible(f.overlay.layer()));
    assert!(
        f.ui.children(f.overlay.layer())
            .iter()
            .all(|m| !f.visible(*m))
    );
    // No camera at all: the same.
    f.tick(&[sample(0.0, true)], VIEWPORT);
    f.overlay.tick(&mut f.ui, &[sample(0.0, true)], None);
    assert!(!f.visible(f.overlay.layer()));
}

#[test]
fn the_overlay_draws_under_every_window() {
    let f = fixture();
    let host_z = f.ui.element(f.host.root()).unwrap().z_order;
    assert!(host_z < ac_ui::overlay::z_order::WINDOW_FLOOR);
}
