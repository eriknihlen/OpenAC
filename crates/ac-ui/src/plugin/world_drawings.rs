//! The plugins' world drawings: pictures a plugin paints with a canvas's
//! commands, hung over objects or at positions in the world. Each frame
//! every drawing's anchor goes through the camera, and the drawing is
//! painted with its pivot on that point, at full size or shrunk with its
//! distance, fading over the last fifth of its range. They sit on the
//! overlay band under the icons and labels, and take no input.

use std::collections::HashMap;
use std::sync::Arc;

use ac_plugin_ui::{DrawingScale, PaintCmd};
use glam::Vec3;

use super::canvas::{CanvasFont, PaintWith, paint};
use super::widgets::Icon;
use crate::draw::RenderContext;
use crate::font::UiFont;
use crate::geometry::AnchorEdges;
use crate::overlay::{OverlayCamera, OverlayHost, fade, project_sphere};
use crate::root::UiRoot;
use crate::tree::{Element, ElementId, Widget};

/// The overlay band layer the drawings sit on.
pub const LAYER_NAME: &str = "PluginWorldDrawingOverlay";

/// One drawing to place this frame.
#[derive(Debug, Clone, PartialEq)]
pub struct WorldDrawingItem {
    /// The point its pivot sits on, in the camera's world frame: the top
    /// of its object or its position, raised by its height offset.
    pub at: Vec3,
    /// Its box at full size, units.
    pub size: (f32, f32),
    /// The point of the box on `at`, units from its top-left at full size.
    pub pivot: (f32, f32),
    /// What it shows.
    pub paint: Arc<Vec<PaintCmd>>,
    /// How its size follows its distance.
    pub scale: DrawingScale,
    /// Past this many metres it is not shown; the last fifth fades.
    pub max_range: f32,
    /// Which of the frame's [`DrawingResources`] it draws with (its
    /// plugin's).
    pub resources: usize,
}

/// One plugin's images and fonts, as its drawings draw with them.
#[derive(Debug, Clone, Default)]
pub struct DrawingResources {
    /// Its images by handle.
    pub images: HashMap<u32, Icon>,
    /// Its fonts by handle.
    pub fonts: HashMap<u32, CanvasFont>,
}

/// One drawing on the screen.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct DrawingPlacement {
    /// Which drawing.
    pub drawing: usize,
    /// The left of its box, whole pixels.
    pub x: f32,
    /// The top of its box, whole pixels.
    pub y: f32,
    /// The fraction of full size it is drawn at.
    pub scale: f32,
    /// Distance from the camera to its anchor, metres.
    pub depth: f32,
    /// How solid.
    pub alpha: f32,
}

/// Fill `out` with this frame's placements, farthest first and then in the
/// order given, so where drawings overlap the nearer one is drawn last.
/// A drawing behind the camera, past its range or wholly off the screen is
/// left out.
pub fn place_drawings(
    drawings: &[WorldDrawingItem],
    camera: &OverlayCamera,
    out: &mut Vec<DrawingPlacement>,
) {
    out.clear();
    let (vw, vh) = camera.viewport;
    for (index, drawing) in drawings.iter().enumerate() {
        let Some(rect) = project_sphere(drawing.at, 0.0, camera, 0.0) else {
            continue;
        };
        if rect.depth > drawing.max_range {
            continue;
        }
        let scale = drawing.scale.at(rect.depth);
        let centre = (rect.min + rect.max) * 0.5;
        // Whole pixels, so a drawing does not shimmer as its anchor drifts.
        let x = (centre.x - drawing.pivot.0 * scale).round();
        let y = (centre.y - drawing.pivot.1 * scale).round();
        let (w, h) = (drawing.size.0 * scale, drawing.size.1 * scale);
        if x + w < 0.0 || x > vw || y + h < 0.0 || y > vh {
            continue;
        }
        out.push(DrawingPlacement {
            drawing: index,
            x,
            y,
            scale,
            depth: rect.depth,
            alpha: fade(rect.depth, drawing.max_range),
        });
    }
    out.sort_by(|a, b| {
        b.depth
            .total_cmp(&a.depth)
            .then_with(|| a.drawing.cmp(&b.drawing))
    });
}

/// The one element drawing every drawing, each clipped to its box.
#[derive(Debug, Default)]
pub struct WorldDrawingLayer {
    font: Option<Arc<UiFont>>,
    drawings: Vec<WorldDrawingItem>,
    resources: Arc<Vec<DrawingResources>>,
    placements: Vec<DrawingPlacement>,
}

impl WorldDrawingLayer {
    /// How many drawings it draws.
    #[must_use]
    pub fn placement_count(&self) -> usize {
        self.placements.len()
    }

    /// This frame's placements, in the order they are drawn.
    #[must_use]
    pub fn placements(&self) -> &[DrawingPlacement] {
        &self.placements
    }
}

impl Widget for WorldDrawingLayer {
    fn draw(&mut self, _ui: &mut UiRoot, _me: ElementId, rc: &mut RenderContext<'_>) {
        let empty = DrawingResources::default();
        for p in &self.placements {
            let drawing = &self.drawings[p.drawing];
            let resources = self.resources.get(drawing.resources).unwrap_or(&empty);
            let with = PaintWith {
                images: &resources.images,
                fonts: &resources.fonts,
                font: self.font.as_deref(),
            };
            let (w, h) = drawing.size;
            rc.push_alpha(p.alpha);
            rc.push_transform(p.x, p.y);
            rc.push_clip(0.0, 0.0, w * p.scale, h * p.scale);
            paint(rc, &drawing.paint, drawing.size, p.scale, &with);
            rc.pop_clip();
            rc.pop_transform();
            rc.pop_alpha();
        }
    }

    fn hit_test(&self, _element: &Element, _x: f32, _y: f32) -> bool {
        false
    }
}

/// Hangs the plugins' drawings in the world on the overlay band, on a
/// layer of its own; mounted before the icons and labels, so they draw
/// over it.
#[derive(Debug)]
pub struct WorldDrawingOverlay {
    layer: ElementId,
    element: ElementId,
    scratch: Vec<DrawingPlacement>,
}

impl WorldDrawingOverlay {
    /// Mount on `host`, drawing text in font 0 with `font`.
    pub fn mount(ui: &mut UiRoot, host: &mut OverlayHost, font: Option<Arc<UiFont>>) -> Self {
        let layer = host.add_layer(ui, LAYER_NAME);
        let (w, h) = host.viewport();
        let e = Element {
            click_through: true,
            anchors: AnchorEdges::NONE,
            name: Some("PluginWorldDrawings".to_owned()),
            ..Element::at(0.0, 0.0, w, h)
        };
        let element = ui.insert(
            e,
            WorldDrawingLayer {
                font,
                ..WorldDrawingLayer::default()
            },
        );
        OverlayHost::add_to_layer(ui, layer, element, false);
        Self {
            layer,
            element,
            scratch: Vec::new(),
        }
    }

    /// The drawing element.
    #[must_use]
    pub fn element(&self) -> ElementId {
        self.element
    }

    /// Place `drawings` for this frame (none without a camera), drawing each
    /// with the `resources` it names (shared, so the caller keeps them from
    /// frame to frame).
    pub fn tick(
        &mut self,
        ui: &mut UiRoot,
        drawings: Vec<WorldDrawingItem>,
        resources: Arc<Vec<DrawingResources>>,
        camera: Option<&OverlayCamera>,
    ) {
        self.scratch.clear();
        if let Some(camera) = camera.filter(|c| c.viewport.0 > 0.0 && c.viewport.1 > 0.0)
            && !drawings.is_empty()
        {
            if let Some(e) = ui.element_mut(self.element) {
                e.width = camera.viewport.0;
                e.height = camera.viewport.1;
            }
            place_drawings(&drawings, camera, &mut self.scratch);
        }
        let shown = !self.scratch.is_empty();
        if let Some(w) = ui.widget_mut::<WorldDrawingLayer>(self.element) {
            std::mem::swap(&mut w.placements, &mut self.scratch);
            w.drawings = drawings;
            w.resources = resources;
        }
        ui.set_visible(self.layer, shown);
    }
}
