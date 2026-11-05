//! The main panels: the windows the toolbar's panel buttons open
//! (inventory, character, spellbook, ...). One shows at a time; opening
//! another hides the one shown (a panel that restores its predecessor
//! brings it back when it closes). They share one place and size, taken
//! from the shared panel frame and then from wherever the player moved or
//! sized the one shown.

use crate::element_info::ElementInfo;
use crate::root::UiRoot;

/// The layout property that makes a panel bring back the one it replaced.
pub const RESTORE_PREVIOUS_PROPERTY: u32 = 0x1000_0049;

#[derive(Debug, Clone, Copy, PartialEq)]
struct Geometry {
    left: f32,
    top: f32,
    width: f32,
    height: f32,
}

#[derive(Debug, Clone, PartialEq)]
struct Entry {
    panel: u32,
    window: String,
    restore_previous: bool,
    shares_geometry: bool,
}

/// The shared panel frame's size limits.
#[derive(Debug, Clone, Copy, PartialEq)]
struct SharedFrame {
    width: f32,
    height: f32,
    min_width: f32,
    max_width: f32,
    min_height: f32,
    max_height: f32,
}

/// See the module documentation.
#[derive(Debug, Default)]
pub struct MainPanels {
    entries: Vec<Entry>,
    active: Option<u32>,
    deferred: Option<u32>,
    applying: bool,
    geometry: Option<Geometry>,
    frame: Option<SharedFrame>,
}

fn window_geometry(ui: &UiRoot, name: &str) -> Option<Geometry> {
    let frame = ui.window(name)?.frame;
    let e = ui.element(frame)?;
    Some(Geometry {
        left: e.left,
        top: e.top,
        width: e.width,
        height: e.height,
    })
}

impl MainPanels {
    /// No panels.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The shared panel frame every main panel window takes the size of.
    /// Call before registering.
    pub fn configure_frame(&mut self, frame: &ElementInfo) {
        let i = |v: Option<i32>| v.map(|v| v as f32);
        self.frame = Some(SharedFrame {
            width: frame.width,
            height: frame.height,
            min_width: i(frame.min_width).unwrap_or(frame.width),
            max_width: i(frame.max_width).unwrap_or(frame.width),
            min_height: i(frame.min_height).unwrap_or(frame.height),
            max_height: i(frame.max_height).unwrap_or(f32::MAX),
        });
    }

    /// The panel shown.
    #[must_use]
    pub fn active(&self) -> Option<u32> {
        self.active
    }

    /// Register window `window` as main panel `panel` (sized to the shared
    /// frame when one is configured: resizable at the bottom only).
    pub fn register(&mut self, ui: &mut UiRoot, panel: u32, window: &str, restore_previous: bool) {
        if let Some(shared) = self.frame
            && let Some(frame) = ui.window(window).map(|w| w.frame)
        {
            if let Some(e) = ui.element_mut(frame) {
                e.min_width = shared.min_width;
                e.max_width = shared.max_width;
                e.min_height = shared.min_height;
                e.max_height = shared.max_height;
                e.resizable = true;
                e.resize_x = true;
                e.resize_y = true;
                e.resizable_edges = crate::geometry::ResizeEdges::BOTTOM;
            }
            let constrained = ui
                .element(frame)
                .is_some_and(|e| e.constrain_resize_to_parent);
            if let Some(e) = ui.element_mut(frame) {
                e.constrain_resize_to_parent = false;
            }
            ui.resize_window(window, shared.width, shared.height);
            if let Some(e) = ui.element_mut(frame) {
                e.resize_x = false;
                e.constrain_resize_to_parent = constrained;
            }
        }
        let entry = Entry {
            panel,
            window: window.to_owned(),
            restore_previous,
            shares_geometry: true,
        };
        if let Some(geometry) = self.geometry {
            Self::apply_geometry(ui, &entry, geometry);
        }
        self.entries.push(entry);
        if ui.is_window_visible(window) {
            self.observe_visibility(ui, window, true);
        }
    }

    fn entry(&self, panel: u32) -> Option<&Entry> {
        self.entries.iter().find(|e| e.panel == panel)
    }

    /// The panel shown in window `window`, if it is a main panel.
    #[must_use]
    pub fn panel_of(&self, window: &str) -> Option<u32> {
        self.entries
            .iter()
            .find(|e| e.window == window)
            .map(|e| e.panel)
    }

    /// The window of main panel `panel`.
    #[must_use]
    pub fn window_of(&self, panel: u32) -> Option<&str> {
        self.entry(panel).map(|e| e.window.as_str())
    }

    /// Whether `panel` shows.
    #[must_use]
    pub fn is_visible(&self, ui: &UiRoot, panel: u32) -> bool {
        self.entry(panel)
            .is_some_and(|e| ui.is_window_visible(&e.window))
    }

    /// Show a hidden panel or hide a shown one; true when it now shows.
    pub fn toggle(&mut self, ui: &mut UiRoot, panel: u32) -> bool {
        let visible = !self.is_visible(ui, panel);
        self.set_visibility(ui, panel, visible) && visible
    }

    /// Show or hide `panel`. Showing it hides the panel shown; hiding the
    /// shown panel brings back the one it deferred.
    pub fn set_visibility(&mut self, ui: &mut UiRoot, panel: u32, visible: bool) -> bool {
        let Some(requested) = self.entry(panel).cloned() else {
            return false;
        };
        self.applying = true;
        let result = if visible {
            if self.active == Some(panel) {
                self.prepare_geometry(ui, &requested);
                ui.show_window(&requested.window)
            } else {
                let previous = self
                    .active
                    .and_then(|id| self.entry(id).cloned())
                    .filter(|e| ui.is_window_visible(&e.window));
                if let Some(previous) = &previous {
                    self.capture_geometry(ui, previous);
                }
                self.deferred = match &previous {
                    Some(p) if requested.restore_previous && !p.restore_previous => self.active,
                    _ => None,
                };
                self.active = Some(panel);
                if let Some(previous) = &previous {
                    ui.hide_window(&previous.window);
                }
                self.prepare_geometry(ui, &requested);
                ui.show_window(&requested.window)
            }
        } else if self.active != Some(panel) {
            if self.deferred == Some(panel) {
                self.deferred = None;
            }
            ui.hide_window(&requested.window)
        } else {
            self.capture_geometry(ui, &requested);
            let hidden = ui.hide_window(&requested.window);
            self.active = None;
            match self.deferred.take().and_then(|id| self.entry(id).cloned()) {
                Some(deferred) => {
                    self.active = Some(deferred.panel);
                    self.prepare_geometry(ui, &deferred);
                    ui.show_window(&deferred.window) || hidden
                }
                None => hidden,
            }
        };
        self.applying = false;
        result
    }

    /// Window `window` was shown or hidden by other means (its close
    /// button): the panel bookkeeping follows.
    pub fn observe_visibility(&mut self, ui: &mut UiRoot, window: &str, visible: bool) {
        if self.applying {
            return;
        }
        if let Some(panel) = self.panel_of(window) {
            self.set_visibility(ui, panel, visible);
        }
    }

    /// Window `window` was moved or resized by the player: every main
    /// panel takes its place and size.
    pub fn observe_geometry(&mut self, ui: &mut UiRoot, window: &str) {
        if self.applying {
            return;
        }
        let Some(source) = self
            .entries
            .iter()
            .find(|e| e.window == window && e.shares_geometry)
            .cloned()
        else {
            return;
        };
        if let Some(g) = window_geometry(ui, &source.window) {
            self.geometry = Some(g);
            self.synchronize(ui, &source.window);
        }
    }

    fn capture_geometry(&mut self, ui: &mut UiRoot, entry: &Entry) {
        if !entry.shares_geometry {
            return;
        }
        if let Some(g) = window_geometry(ui, &entry.window) {
            self.geometry = Some(g);
            self.synchronize(ui, &entry.window);
        }
    }

    fn prepare_geometry(&mut self, ui: &mut UiRoot, entry: &Entry) {
        if !entry.shares_geometry {
            return;
        }
        match self.geometry {
            None => {
                self.geometry = window_geometry(ui, &entry.window);
                self.synchronize(ui, &entry.window);
            }
            Some(g) => Self::apply_geometry(ui, entry, g),
        }
    }

    fn synchronize(&mut self, ui: &mut UiRoot, source: &str) {
        let Some(g) = self.geometry else {
            return;
        };
        let applying = std::mem::replace(&mut self.applying, true);
        for e in &self.entries {
            if e.shares_geometry && e.window != source {
                Self::apply_geometry(ui, e, g);
            }
        }
        self.applying = applying;
    }

    fn apply_geometry(ui: &mut UiRoot, entry: &Entry, g: Geometry) {
        let Some(now) = window_geometry(ui, &entry.window) else {
            return;
        };
        if now.left != g.left || now.top != g.top {
            ui.move_window(&entry.window, g.left, g.top);
        }
        if now.width != g.width || now.height != g.height {
            ui.resize_window(&entry.window, g.width, g.height);
        }
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `RetailPanelUiController`: one main panel at a time, a
    //! restoring panel brings back its predecessor, the shown panel's place
    //! is every panel's place.

    use super::*;
    use crate::tree::Element;
    use crate::widgets::Panel;

    fn window(ui: &mut UiRoot, name: &str, left: f32) {
        let root = ui.root();
        let mut e = Element::at(left, 10.0, 100.0, 200.0);
        e.max_width = 1000.0;
        e.max_height = 1000.0;
        let id = ui.add(root, e, Panel::default());
        ui.register_window(name, id, Some(id), 0).unwrap();
        ui.hide_window(name);
    }

    fn panels() -> (UiRoot, MainPanels) {
        let mut ui = UiRoot::new();
        ui.set_size(1000.0, 800.0);
        for (name, left) in [("inventory", 10.0), ("character", 300.0), ("magic", 500.0)] {
            window(&mut ui, name, left);
        }
        let mut p = MainPanels::new();
        p.register(&mut ui, 7, "inventory", false);
        p.register(&mut ui, 11, "character", false);
        p.register(&mut ui, 13, "magic", true);
        (ui, p)
    }

    #[test]
    fn one_panel_shows_at_a_time_in_one_place() {
        let (mut ui, mut p) = panels();
        assert!(p.toggle(&mut ui, 7));
        assert!(p.toggle(&mut ui, 11));
        assert!(!ui.is_window_visible("inventory"));
        assert!(ui.is_window_visible("character"));
        assert_eq!(p.active(), Some(11));
        // The character window opened where the inventory was.
        let frame = ui.window("character").unwrap().frame;
        assert_eq!(ui.element(frame).unwrap().left, 10.0);
        assert!(!p.toggle(&mut ui, 11));
        assert_eq!(p.active(), None);
    }

    #[test]
    fn a_restoring_panel_brings_back_the_one_it_replaced() {
        let (mut ui, mut p) = panels();
        p.toggle(&mut ui, 7);
        p.toggle(&mut ui, 13);
        assert!(!ui.is_window_visible("inventory"));
        p.set_visibility(&mut ui, 13, false);
        assert!(ui.is_window_visible("inventory"));
        assert_eq!(p.active(), Some(7));
    }
}
