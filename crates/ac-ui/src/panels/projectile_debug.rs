//! The projectile debug overlay (a diagnostic, empty unless something
//! reports samples): a square marker over each reported projectile path
//! point, green where the path is clear and red where it is blocked, on a
//! click-through layer of the overlay band.

use glam::Vec3;

use crate::draw::Color;
use crate::geometry::AnchorEdges;
use crate::overlay::{OverlayCamera, OverlayHost, project_sphere};
use crate::root::UiRoot;
use crate::tree::{Element, ElementId};
use crate::widgets::Panel;

/// The overlay layer's name.
pub const LAYER_NAME: &str = "PluginProjectileDebugOverlay";
/// A clear sample's marker.
pub const CLEAR_COLOR: Color = Color::rgba(0.0, 1.0, 0.0, 0.95);
/// A blocked sample's marker.
pub const BLOCKED_COLOR: Color = Color::rgba(1.0, 0.0, 0.0, 0.95);
/// A marker's outline, pixels.
pub const BORDER_THICKNESS: f32 = 1.5;
/// A marker's smallest side, pixels.
pub const MIN_SIDE_PIXELS: f32 = 4.0;

/// One reported point.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ProjectileDebugSample {
    /// Where, world metres.
    pub world_position: Vec3,
    /// The path is clear there.
    pub is_clear: bool,
    /// The projectile's radius, metres.
    pub radius: f32,
}

/// See the module documentation.
#[derive(Debug)]
pub struct ProjectileDebugOverlay {
    layer: ElementId,
    markers: Vec<ElementId>,
}

impl ProjectileDebugOverlay {
    /// Take a layer of the overlay band.
    pub fn mount(ui: &mut UiRoot, host: &mut OverlayHost) -> Self {
        let layer = host.add_layer(ui, LAYER_NAME);
        if let Some(p) = ui.widget_mut::<Panel>(layer) {
            p.background_color = Color::rgba(0.0, 0.0, 0.0, 0.0);
            p.border_color = Color::rgba(0.0, 0.0, 0.0, 0.0);
            p.border_thickness = 0.0;
        }
        Self {
            layer,
            markers: Vec::new(),
        }
    }

    /// The layer.
    #[must_use]
    pub fn layer(&self) -> ElementId {
        self.layer
    }

    /// The markers, in sample order (surplus ones hidden).
    #[must_use]
    pub fn markers(&self) -> &[ElementId] {
        &self.markers
    }

    /// One frame: the samples' markers through `camera`; nothing shows
    /// without samples or a viewport.
    pub fn tick(
        &mut self,
        ui: &mut UiRoot,
        samples: &[ProjectileDebugSample],
        camera: Option<&OverlayCamera>,
    ) {
        let Some(camera) = camera.filter(|c| c.viewport.0 > 0.0 && c.viewport.1 > 0.0) else {
            self.hide_all(ui);
            return;
        };
        if samples.is_empty() {
            self.hide_all(ui);
            return;
        }
        self.ensure_markers(ui, samples.len());
        let (vw, vh) = camera.viewport;
        let mut visible = 0;
        for sample in samples {
            let Some(rect) = project_sphere(
                sample.world_position,
                sample.radius,
                camera,
                MIN_SIDE_PIXELS,
            ) else {
                continue;
            };
            if rect.max.x < 0.0 || rect.max.y < 0.0 || rect.min.x > vw || rect.min.y > vh {
                continue;
            }
            let marker = self.markers[visible];
            visible += 1;
            if let Some(e) = ui.element_mut(marker) {
                e.left = rect.min.x.max(0.0);
                e.top = rect.min.y.max(0.0);
                e.width = (vw.min(rect.max.x) - e.left).max(1.0);
                e.height = (vh.min(rect.max.y) - e.top).max(1.0);
            }
            if let Some(p) = ui.widget_mut::<Panel>(marker) {
                p.border_color = if sample.is_clear {
                    CLEAR_COLOR
                } else {
                    BLOCKED_COLOR
                };
            }
            ui.set_visible(marker, true);
        }
        for &m in &self.markers[visible..] {
            ui.set_visible(m, false);
        }
        ui.set_visible(self.layer, visible > 0);
    }

    fn ensure_markers(&mut self, ui: &mut UiRoot, count: usize) {
        while self.markers.len() < count {
            let mut e = Element::at(0.0, 0.0, 1.0, 1.0).with_visible(false);
            e.name = Some(format!("PluginProjectileDebugMarker{}", self.markers.len()));
            e.click_through = true;
            e.anchors = AnchorEdges::NONE;
            let marker = ui.add(
                self.layer,
                e,
                Panel {
                    background_color: Color::rgba(0.0, 0.0, 0.0, 0.0),
                    border_color: CLEAR_COLOR,
                    border_thickness: BORDER_THICKNESS,
                    background_sprite: 0,
                },
            );
            self.markers.push(marker);
        }
    }

    fn hide_all(&self, ui: &mut UiRoot) {
        ui.set_visible(self.layer, false);
        for &m in &self.markers {
            ui.set_visible(m, false);
        }
    }
}
