//! Named top-level windows: registration, show/hide/toggle/close, move and
//! resize, raise, and the per-window lifecycle events.
//!
//! A registered window is a direct child of the root with a name. What
//! happens to it is reported as [`UiOutput::Window`] events; the host routes
//! them to the window's panel controller.

use std::collections::HashMap;

use crate::geometry::clamp;
use crate::root::{UiOutput, UiRoot};
use crate::tree::ElementId;

/// Something that happened to a registered window.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WindowEvent {
    /// The window was registered (visible or not).
    Registered {
        /// Visible at registration.
        visible: bool,
    },
    /// The window became visible (reported once per transition).
    Shown,
    /// The window became hidden (reported once per transition).
    Hidden,
    /// The window's frame visibility changed.
    VisibilityChanged(bool),
    /// The window was moved (by drag or programmatically).
    Moved,
    /// The window was resized.
    Resized,
    /// The window was closed (once until shown again).
    Closed,
    /// The UI lock changed.
    LockChanged(bool),
    /// Keyboard focus entered, moved within or left the window: the
    /// focused descendant, or none when focus left.
    DescendantFocusChanged(Option<ElementId>),
    /// Pointer capture entered, moved within or left the window.
    DescendantCaptureChanged(Option<ElementId>),
    /// The window was unregistered (its controller is released).
    Unregistered,
}

/// A registered window, as the root sees it.
#[derive(Debug, Clone, PartialEq)]
pub struct WindowHandle {
    /// Window name.
    pub name: String,
    /// The outer frame (a root child).
    pub frame: ElementId,
    /// The content root (the frame unless given).
    pub content_root: ElementId,
    /// Revision of the authored geometry (for persisted layouts).
    pub authored_geometry_revision: i32,
    /// Frame width at registration.
    pub authored_width: f32,
    /// Frame height at registration.
    pub authored_height: f32,
    notified_visible: bool,
    closed_since_shown: bool,
}

/// Why a window could not be registered.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RegisterError {
    /// The name is empty or blank.
    BlankName,
    /// The frame is not a direct child of the root.
    NotARootChild,
}

impl std::fmt::Display for RegisterError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::BlankName => f.write_str("a window name must not be blank"),
            Self::NotARootChild => {
                f.write_str("a window must be mounted as a direct root child before registration")
            }
        }
    }
}

impl std::error::Error for RegisterError {}

#[derive(Debug, Default)]
pub(crate) struct WindowManager {
    windows: Vec<WindowHandle>,
    default_inputs: HashMap<String, ElementId>,
}

/// A stable revision number of a window's authored geometry: equal inputs
/// give equal values, never negative.
#[must_use]
pub fn compute_authored_geometry_revision(
    width: f32,
    height: f32,
    min_width: f32,
    min_height: f32,
    resizable: bool,
) -> i32 {
    let mut hash: i32 = 17;
    for v in [width, height, min_width, min_height] {
        hash = hash.wrapping_mul(31).wrapping_add(v.to_bits() as i32);
    }
    hash = hash.wrapping_mul(31).wrapping_add(i32::from(resizable));
    hash & 0x7FFF_FFFF
}

impl UiRoot {
    fn window_index(&self, name: &str) -> Option<usize> {
        self.windows.windows.iter().position(|w| w.name == name)
    }

    fn window_index_of_frame(&self, frame: ElementId) -> Option<usize> {
        self.windows.windows.iter().position(|w| w.frame == frame)
    }

    fn emit_window(&mut self, name: &str, event: WindowEvent) {
        self.emit(UiOutput::Window {
            name: name.to_owned(),
            event,
        });
    }

    /// Register `frame` (a direct root child) as window `name`. Registering
    /// the same frame under the same name again is a no-op; a name or frame
    /// already registered otherwise is unregistered first.
    pub fn register_window(
        &mut self,
        name: &str,
        frame: ElementId,
        content_root: Option<ElementId>,
        authored_geometry_revision: i32,
    ) -> Result<(), RegisterError> {
        if name.trim().is_empty() {
            return Err(RegisterError::BlankName);
        }
        if self.parent(frame) != Some(self.root()) {
            return Err(RegisterError::NotARootChild);
        }
        let content_root = content_root.unwrap_or(frame);
        let revision = authored_geometry_revision.max(0);
        if let Some(i) = self.window_index(name) {
            let w = &self.windows.windows[i];
            if w.frame == frame
                && w.content_root == content_root
                && w.authored_geometry_revision == revision
            {
                return Ok(());
            }
            self.unregister_window(name);
        }
        if let Some(i) = self.window_index_of_frame(frame) {
            let other = self.windows.windows[i].name.clone();
            self.unregister_window(&other);
        }
        let (visible, width, height) = self
            .element(frame)
            .map_or((false, 0.0, 0.0), |e| (e.visible(), e.width, e.height));
        self.windows.windows.push(WindowHandle {
            name: name.to_owned(),
            frame,
            content_root,
            authored_geometry_revision: revision,
            authored_width: width,
            authored_height: height,
            notified_visible: visible,
            closed_since_shown: false,
        });
        self.emit_window(name, WindowEvent::Registered { visible });
        Ok(())
    }

    /// Unregister window `name`: it is hidden (or its ownership released),
    /// closed and forgotten. False for an unknown name.
    pub fn unregister_window(&mut self, name: &str) -> bool {
        let Some(i) = self.window_index(name) else {
            return false;
        };
        let frame = self.windows.windows[i].frame;
        if self.element(frame).is_some_and(|e| e.visible()) {
            self.set_visible(frame, false);
        } else {
            self.clear_subtree_ownership(frame, false);
        }
        if let Some(i) = self.window_index(name) {
            self.close_at(i);
            self.windows.windows.remove(i);
            self.windows.default_inputs.remove(name);
            self.emit_window(name, WindowEvent::Unregistered);
        }
        true
    }

    fn close_at(&mut self, i: usize) {
        let handle = &mut self.windows.windows[i];
        if handle.closed_since_shown {
            return;
        }
        handle.closed_since_shown = true;
        let name = handle.name.clone();
        self.emit_window(&name, WindowEvent::Closed);
    }

    /// Unregister every window.
    pub fn unregister_all_windows(&mut self) {
        let names: Vec<String> = self
            .windows
            .windows
            .iter()
            .map(|w| w.name.clone())
            .collect();
        for name in names {
            self.unregister_window(&name);
        }
    }

    /// The registered window `name`.
    #[must_use]
    pub fn window(&self, name: &str) -> Option<&WindowHandle> {
        self.windows.windows.iter().find(|w| w.name == name)
    }

    /// Every registered window, in registration order.
    #[must_use]
    pub fn windows(&self) -> &[WindowHandle] {
        &self.windows.windows
    }

    /// The registered window whose frame contains `element`.
    #[must_use]
    pub fn window_of(&self, element: ElementId) -> Option<&WindowHandle> {
        self.windows
            .windows
            .iter()
            .find(|w| self.is_within(Some(element), w.frame))
    }

    /// Show window `name` and raise it. False for an unknown name.
    pub fn show_window(&mut self, name: &str) -> bool {
        let Some(frame) = self.window(name).map(|w| w.frame) else {
            return false;
        };
        self.set_visible(frame, true);
        self.bring_to_front(frame);
        true
    }

    /// Hide window `name`. False for an unknown name.
    pub fn hide_window(&mut self, name: &str) -> bool {
        let Some(frame) = self.window(name).map(|w| w.frame) else {
            return false;
        };
        self.set_visible(frame, false);
        true
    }

    /// Show a hidden window or hide a shown one; the new visibility (false
    /// for an unknown name).
    pub fn toggle_window(&mut self, name: &str) -> bool {
        let Some(frame) = self.window(name).map(|w| w.frame) else {
            return false;
        };
        if self.element(frame).is_some_and(|e| e.visible()) {
            self.hide_window(name);
            false
        } else {
            self.show_window(name);
            true
        }
    }

    /// Hide window `name` and report it closed (once until shown again).
    pub fn close_window(&mut self, name: &str) -> bool {
        if !self.hide_window(name) {
            return false;
        }
        if let Some(i) = self.window_index(name) {
            self.close_at(i);
        }
        true
    }

    /// True when window `name` exists and is visible.
    #[must_use]
    pub fn is_window_visible(&self, name: &str) -> bool {
        self.window(name)
            .and_then(|w| self.element(w.frame))
            .is_some_and(|e| e.visible())
    }

    /// Move window `name`, kept inside its parent. False for an unknown
    /// name.
    pub fn move_window(&mut self, name: &str, left: f32, top: f32) -> bool {
        let Some(frame) = self.window(name).map(|w| w.frame) else {
            return false;
        };
        let parent = self
            .parent(frame)
            .and_then(|p| self.element(p))
            .map(|p| (p.width, p.height));
        let Some(e) = self.element(frame) else {
            return false;
        };
        let (mut left, mut top) = (left, top);
        if let Some((pw, ph)) = parent {
            left = clamp(left, 0.0, (pw - e.width).max(0.0));
            top = clamp(top, 0.0, (ph - e.height).max(0.0));
        }
        if e.left == left && e.top == top {
            return true;
        }
        if let Some(e) = self.element_mut(frame) {
            e.left = left;
            e.top = top;
        }
        self.reset_anchor_capture(frame);
        self.windows_notify_moved(frame);
        true
    }

    /// Resize window `name` within its limits (locked axes keep their
    /// size). False for an unknown name.
    pub fn resize_window(&mut self, name: &str, width: f32, height: f32) -> bool {
        let Some(frame) = self.window(name).map(|w| w.frame) else {
            return false;
        };
        let parent = self
            .parent(frame)
            .and_then(|p| self.element(p))
            .map(|p| (p.width, p.height));
        let Some(e) = self.element(frame) else {
            return false;
        };
        let mut width = if e.resize_x { width } else { e.width };
        let mut height = if e.resize_y { height } else { e.height };
        let mut max_w = e.max_width;
        let mut max_h = e.max_height;
        if e.constrain_resize_to_parent
            && let Some((pw, ph)) = parent
        {
            max_w = max_w.min(pw - e.left);
            max_h = max_h.min(ph - e.top);
        }
        width = clamp(width, e.min_width, e.min_width.max(max_w));
        height = clamp(height, e.min_height, e.min_height.max(max_h));
        if e.width == width && e.height == height {
            return true;
        }
        if let Some(e) = self.element_mut(frame) {
            e.width = width;
            e.height = height;
        }
        self.reset_anchor_capture(frame);
        self.windows_notify_resized(frame);
        true
    }

    /// Brings window `name` up to its minimum size, starting from `width` x
    /// `height` instead of its current size, so a window that grew to fit
    /// its content can go back to an earlier size (never below its minimum)
    /// when the content needs less again. Unlike
    /// [`UiRoot::resize_window`] it grows an axis even when the window
    /// cannot be resized along it, and it keeps the window on screen: a
    /// frame that would reach past the parent's right or bottom edge moves
    /// left or up, no further than the parent's origin, and an axis whose
    /// minimum is larger than the parent takes the parent's size at its
    /// origin. The new geometry is announced like a resize, and a move when
    /// the frame moved. False for an unknown name.
    pub fn enforce_minimum_size(&mut self, name: &str, width: f32, height: f32) -> bool {
        let Some(frame) = self.window(name).map(|w| w.frame) else {
            return false;
        };
        let parent = self
            .parent(frame)
            .and_then(|p| self.element(p))
            .map(|p| (p.width, p.height));
        let Some(e) = self.element(frame) else {
            return false;
        };
        let mut width = width.max(e.min_width);
        let mut height = height.max(e.min_height);
        let (mut left, mut top) = (e.left, e.top);
        if let Some((pw, ph)) = parent {
            (left, width) = fit_axis(left, width, pw);
            (top, height) = fit_axis(top, height, ph);
        }
        let resized = width != e.width || height != e.height;
        let moved = left != e.left || top != e.top;
        if !resized && !moved {
            return true;
        }
        if let Some(e) = self.element_mut(frame) {
            e.width = width;
            e.height = height;
            e.left = left;
            e.top = top;
        }
        self.reset_anchor_capture(frame);
        if resized {
            self.windows_notify_resized(frame);
        }
        if moved {
            self.windows_notify_moved(frame);
        }
        true
    }

    /// Set window `name`'s opacity (0..=1).
    pub fn set_window_opacity(&mut self, name: &str, opacity: f32) -> bool {
        let Some(frame) = self.window(name).map(|w| w.frame) else {
            return false;
        };
        if let Some(e) = self.element_mut(frame) {
            e.opacity = opacity.clamp(0.0, 1.0);
        }
        true
    }

    // ── Hooks from the root ─────────────────────────────────────────────

    pub(crate) fn windows_prepare_to_hide(&mut self, subtree: ElementId) {
        let Some(i) = self.window_index_of_frame(subtree) else {
            return;
        };
        if let Some(input) = self.default_text_input
            && self.is_within(Some(input), subtree)
        {
            let name = self.windows.windows[i].name.clone();
            self.windows.default_inputs.insert(name, input);
        }
    }

    pub(crate) fn windows_on_visibility_changed(&mut self, element: ElementId, visible: bool) {
        let Some(i) = self.window_index_of_frame(element) else {
            return;
        };
        let name = self.windows.windows[i].name.clone();
        let frame = self.windows.windows[i].frame;
        if visible
            && self.default_text_input.is_none()
            && let Some(&input) = self.windows.default_inputs.get(&name)
            && self.is_within(Some(input), frame)
        {
            self.default_text_input = Some(input);
        }
        self.notify_visibility(i, visible);
        self.emit_window(&name, WindowEvent::VisibilityChanged(visible));
    }

    fn notify_visibility(&mut self, i: usize, visible: bool) {
        let handle = &mut self.windows.windows[i];
        if handle.notified_visible == visible {
            return;
        }
        handle.notified_visible = visible;
        let name = handle.name.clone();
        if visible {
            handle.closed_since_shown = false;
            self.emit_window(&name, WindowEvent::Shown);
        } else {
            self.emit_window(&name, WindowEvent::Hidden);
        }
    }

    pub(crate) fn windows_on_subtree_removing(&mut self, subtree: ElementId) {
        let inside: Vec<String> = self
            .windows
            .windows
            .iter()
            .filter(|w| self.is_within(Some(w.frame), subtree))
            .map(|w| w.name.clone())
            .collect();
        for name in inside {
            if let Some(i) = self.window_index(&name) {
                self.notify_visibility(i, false);
            }
            if let Some(i) = self.window_index(&name) {
                self.close_at(i);
                self.windows.windows.remove(i);
                self.windows.default_inputs.remove(&name);
                self.emit_window(&name, WindowEvent::Unregistered);
            }
        }
    }

    pub(crate) fn windows_on_focus_changed(
        &mut self,
        old: Option<ElementId>,
        new: Option<ElementId>,
    ) {
        let frames: Vec<(String, ElementId)> = self
            .windows
            .windows
            .iter()
            .map(|w| (w.name.clone(), w.frame))
            .collect();
        for (name, frame) in frames {
            let had = self.is_within(old, frame);
            let has = self.is_within(new, frame);
            if had || has {
                self.emit_window(
                    &name,
                    WindowEvent::DescendantFocusChanged(if has { new } else { None }),
                );
            }
        }
    }

    pub(crate) fn windows_on_capture_changed(
        &mut self,
        old: Option<ElementId>,
        new: Option<ElementId>,
    ) {
        let frames: Vec<(String, ElementId)> = self
            .windows
            .windows
            .iter()
            .map(|w| (w.name.clone(), w.frame))
            .collect();
        for (name, frame) in frames {
            let had = self.is_within(old, frame);
            let has = self.is_within(new, frame);
            if had || has {
                self.emit_window(
                    &name,
                    WindowEvent::DescendantCaptureChanged(if has { new } else { None }),
                );
            }
        }
    }

    pub(crate) fn windows_on_lock_changed(&mut self, locked: bool) {
        let names: Vec<String> = self
            .windows
            .windows
            .iter()
            .map(|w| w.name.clone())
            .collect();
        for name in names {
            self.emit_window(&name, WindowEvent::LockChanged(locked));
        }
    }

    pub(crate) fn windows_notify_moved(&mut self, frame: ElementId) {
        if let Some(i) = self.window_index_of_frame(frame) {
            let name = self.windows.windows[i].name.clone();
            self.emit_window(&name, WindowEvent::Moved);
        }
    }

    pub(crate) fn windows_notify_resized(&mut self, frame: ElementId) {
        if let Some(i) = self.window_index_of_frame(frame) {
            let name = self.windows.windows[i].name.clone();
            self.emit_window(&name, WindowEvent::Resized);
        }
    }
}

/// Keeps one axis of a window inside a parent `space` long. A parent with
/// no extent yet (not laid out) leaves the axis alone.
fn fit_axis(start: f32, size: f32, space: f32) -> (f32, f32) {
    if space <= 0.0 {
        return (start, size);
    }
    if size > space {
        return (0.0, space.max(0.0));
    }
    if start + size > space {
        return ((space - size).max(0.0), size);
    }
    (start, size)
}
