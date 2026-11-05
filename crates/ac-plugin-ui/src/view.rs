//! A window's copy of the session's plugin UI: built from the snapshot it
//! gets when it attaches and kept in step by the changes that follow; it
//! can be dropped at any time.

use std::collections::BTreeMap;
use std::sync::Arc;

use crate::model::{
    CanvasView, ClientWindow, ClientWindowAction, GroundShape, ImageView, PanelView,
    ProjectileSample, UiChange, WorldIconEntry, WorldLabel, WorldLine, WorldTriangle,
};

/// What changed in the copy since the window last looked.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct ViewDirty {
    /// Panels came or went.
    pub panels: bool,
    /// Canvases came or went.
    pub canvases: bool,
    /// Images came or went.
    pub images: bool,
}

/// See the module documentation.
#[derive(Debug, Default)]
pub struct PluginUiView {
    /// Every panel, in registration order.
    pub panels: Vec<PanelView>,
    /// Every canvas, in registration order.
    pub canvases: Vec<CanvasView>,
    /// The image each plugin window's file icon draws, by window and
    /// path (as its markup gives it): the plugin's handle.
    pub file_icons: BTreeMap<(u32, String), u32>,
    /// Every image, by plugin and handle.
    pub images: BTreeMap<(String, u32), ImageView>,
    /// Every font a plugin holds, by plugin and handle.
    pub fonts: BTreeMap<(String, u32), crate::fonts::FontView>,
    /// The world labels.
    pub labels: Arc<Vec<WorldLabel>>,
    /// The world lines.
    pub lines: Arc<Vec<WorldLine>>,
    /// The world triangles.
    pub triangles: Arc<Vec<WorldTriangle>>,
    /// The world icons.
    pub icons: Arc<Vec<WorldIconEntry>>,
    /// The ground shapes.
    pub shapes: Arc<Vec<GroundShape>>,
    /// The projectile markers last sent, with how many sets have come
    /// (the window shows each set for a moment).
    pub projectile: (u64, Arc<Vec<ProjectileSample>>),
    /// The bound plugin hotkeys, in registration order.
    pub hotkeys: Arc<Vec<crate::hotkeys::BoundHotkey>>,
    /// Client-window requests not yet carried out, oldest first.
    pub client_requests: Vec<(ClientWindow, ClientWindowAction)>,
    /// The dialogs waiting for an answer, in the order asked.
    pub dialogs: Vec<crate::model::DialogView>,
    /// The windows shown modal and the windows they block.
    pub modals: Vec<(u32, u32)>,
    /// Counts every change applied.
    pub revision: u64,
    /// Counts the snapshots applied (one per attach).
    pub snapshots: u64,
    dirty: ViewDirty,
}

impl PluginUiView {
    /// An empty copy.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The panel numbered `id`.
    #[must_use]
    pub fn panel(&self, id: u32) -> Option<&PanelView> {
        self.panels.iter().find(|p| p.id == id)
    }

    fn panel_mut(&mut self, id: u32) -> Option<&mut PanelView> {
        self.panels.iter_mut().find(|p| p.id == id)
    }

    /// The canvas numbered `id`.
    #[must_use]
    pub fn canvas(&self, id: u32) -> Option<&CanvasView> {
        self.canvases.iter().find(|c| c.id == id)
    }

    fn canvas_mut(&mut self, id: u32) -> Option<&mut CanvasView> {
        self.canvases.iter_mut().find(|c| c.id == id)
    }

    /// The image `owner` holds under `handle`.
    #[must_use]
    pub fn image(&self, owner: &str, handle: u32) -> Option<&ImageView> {
        self.images.get(&(owner.to_owned(), handle))
    }

    /// The image panel `panel`'s file icon `path` draws (the path as its
    /// markup gives it), once the session holds one.
    #[must_use]
    pub fn file_icon(&self, panel: u32, path: &str) -> Option<&ImageView> {
        let handle = *self.file_icons.get(&(panel, path.to_owned()))?;
        let owner = &self.panel(panel)?.owner.id;
        self.images.get(&(owner.clone(), handle))
    }

    /// What changed since the last call.
    pub fn take_dirty(&mut self) -> ViewDirty {
        std::mem::take(&mut self.dirty)
    }

    /// The client-window requests not yet carried out.
    pub fn take_client_requests(&mut self) -> Vec<(ClientWindow, ClientWindowAction)> {
        std::mem::take(&mut self.client_requests)
    }

    /// Applies one change.
    pub fn apply(&mut self, change: UiChange) {
        self.revision += 1;
        match change {
            UiChange::Clear => {
                self.snapshots += 1;
                self.panels.clear();
                self.canvases.clear();
                self.images.clear();
                self.fonts.clear();
                self.file_icons.clear();
                self.labels = Arc::default();
                self.lines = Arc::default();
                self.triangles = Arc::default();
                self.icons = Arc::default();
                self.shapes = Arc::default();
                self.hotkeys = Arc::default();
                self.client_requests.clear();
                self.dialogs.clear();
                self.modals.clear();
                self.dirty = ViewDirty {
                    panels: true,
                    canvases: true,
                    images: true,
                };
            }
            UiChange::PanelAdded(panel) => {
                self.panels.retain(|p| p.id != panel.id);
                self.panels.push(*panel);
                self.dirty.panels = true;
            }
            UiChange::PanelRemoved(id) => {
                self.panels.retain(|p| p.id != id);
                self.file_icons.retain(|(panel, _), _| *panel != id);
                self.dirty.panels = true;
            }
            UiChange::PanelValues { panel, values } => {
                if let Some(p) = self.panel_mut(panel) {
                    p.values.extend(values);
                }
            }
            UiChange::PanelRequestedVisible { panel, visible } => {
                if let Some(p) = self.panel_mut(panel) {
                    p.requested_visible = visible;
                }
            }
            UiChange::ControlLabel { panel, node, label } => {
                if let Some(p) = self.panel_mut(panel) {
                    p.label_overrides.insert(node, label);
                }
            }
            UiChange::ControlVisible {
                panel,
                node,
                visible,
            } => {
                if let Some(p) = self.panel_mut(panel) {
                    p.visible_overrides.insert(node, visible);
                }
            }
            UiChange::CanvasAdded(canvas) => {
                self.canvases.retain(|c| c.id != canvas.id);
                self.canvases.push(*canvas);
                self.dirty.canvases = true;
            }
            UiChange::CanvasRemoved(id) => {
                self.canvases.retain(|c| c.id != id);
                self.dirty.canvases = true;
            }
            UiChange::CanvasPaint { canvas, paint } => {
                if let Some(c) = self.canvas_mut(canvas) {
                    c.paint = paint;
                }
            }
            UiChange::CanvasPlacement {
                canvas,
                visible,
                anchor,
                offset,
            } => {
                if let Some(c) = self.canvas_mut(canvas) {
                    c.visible = visible;
                    c.anchor = anchor;
                    c.offset = offset;
                }
            }
            UiChange::CanvasListening { canvas, listening } => {
                if let Some(c) = self.canvas_mut(canvas) {
                    c.listening = listening;
                }
            }
            UiChange::CanvasReleasePointer(canvas) => {
                if let Some(c) = self.canvas_mut(canvas) {
                    c.release_count = c.release_count.wrapping_add(1);
                }
            }
            UiChange::ImageAdded(image) => {
                self.images
                    .insert((image.owner.clone(), image.handle), *image);
                self.dirty.images = true;
            }
            UiChange::FileIcon {
                panel,
                path,
                handle,
            } => {
                if handle == 0 {
                    self.file_icons.remove(&(panel, path));
                } else {
                    self.file_icons.insert((panel, path), handle);
                }
            }
            UiChange::ImageRemoved { owner, handle } => {
                self.images.remove(&(owner, handle));
                self.dirty.images = true;
            }
            UiChange::FontAdded(font) => {
                self.fonts.insert((font.owner.clone(), font.handle), *font);
            }
            UiChange::FontRemoved { owner, handle } => {
                self.fonts.remove(&(owner, handle));
            }
            UiChange::Labels(labels) => self.labels = labels,
            UiChange::Lines(lines) => self.lines = lines,
            UiChange::Triangles(triangles) => self.triangles = triangles,
            UiChange::Icons(icons) => self.icons = icons,
            UiChange::Shapes(shapes) => self.shapes = shapes,
            UiChange::ProjectileDebug(samples) => {
                self.projectile = (self.projectile.0 + 1, samples);
            }
            UiChange::ClientWindow { window, action } => {
                self.client_requests.push((window, action));
            }
            UiChange::Hotkeys(table) => self.hotkeys = table,
            UiChange::DialogAdded(dialog) => self.dialogs.push(*dialog),
            UiChange::DialogRemoved(handle) => self.dialogs.retain(|d| d.handle != handle),
            UiChange::Modal { panel, owner } => {
                self.modals.retain(|(p, _)| *p != panel);
                if let Some(owner) = owner {
                    self.modals.push((panel, owner));
                }
            }
        }
    }

    /// Applies changes in order.
    pub fn apply_all(&mut self, changes: impl IntoIterator<Item = UiChange>) {
        for change in changes {
            self.apply(change);
        }
    }
}
