//! The host: owns the root, routes its outputs to panel controllers and
//! the tooltip presenter, and hands the rest to the client.
//!
//! Panel controllers are the panel-facing contract: one per registered
//! window, told when it is shown, hidden or loses focus, handed the
//! messages of the widgets inside it, and released exactly once when the
//! window goes. Controllers get the whole root, so they rebuild what they
//! show from runtime state whenever they are told to (presentation holds
//! only what it can rebuild).

use crate::draw::{DrawList, SpriteSizes};
use crate::event::{Key, Modifiers, MouseButton};
use crate::layout_persistence::{WindowState, collapsible_frame_state};
use crate::root::{ElementMessage, UiOutput, UiRoot};
use crate::tooltip::{TooltipPresenter, WorldHover};
use crate::tree::ElementId;
use crate::widgets::NineSliceFrame;
use crate::windows::WindowEvent;

/// The controller of one window's panel. Every method is optional.
pub trait PanelController: crate::tree::AsAny {
    /// The window became visible.
    fn on_shown(&mut self, _ui: &mut UiRoot) {}
    /// One UI frame passed (`dt` seconds), whether or not the window is
    /// shown.
    fn tick(&mut self, _ui: &mut UiRoot, _dt: f64) {}
    /// The window became hidden.
    fn on_hidden(&mut self, _ui: &mut UiRoot) {}
    /// Keyboard focus moved within, into or out of the window.
    fn on_descendant_focus_changed(&mut self, _ui: &mut UiRoot, _focused: Option<ElementId>) {}
    /// Any other event of the window (moved, resized, closed, ...).
    fn on_window_event(&mut self, _ui: &mut UiRoot, _event: &WindowEvent) {}
    /// A widget inside the window sent a message; true when handled (it
    /// then does not reach the client).
    fn on_element_message(
        &mut self,
        _ui: &mut UiRoot,
        _element: ElementId,
        _message: &ElementMessage,
    ) -> bool {
        false
    }
    /// The window state this controller keeps (the chat window's
    /// maximize), for the layout to save; `None` when it keeps none.
    fn capture_window_state(&self, _ui: &UiRoot) -> Option<WindowState> {
        None
    }
    /// Put back a saved window state; false when it keeps none.
    fn restore_window_state(&mut self, _ui: &mut UiRoot, _state: &WindowState) -> bool {
        false
    }
    /// The window was unregistered or the host shut down; called once.
    fn dispose(&mut self, _ui: &mut UiRoot) {}
}

/// Several controllers acting as one: shown and focus in order, hidden
/// and disposed in reverse order, nothing after disposal.
#[derive(Default)]
pub struct ControllerGroup {
    controllers: Vec<Box<dyn PanelController>>,
    disposed: bool,
}

impl std::fmt::Debug for ControllerGroup {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ControllerGroup")
            .field("controllers", &self.controllers.len())
            .field("disposed", &self.disposed)
            .finish()
    }
}

impl ControllerGroup {
    /// A group of these controllers.
    #[must_use]
    pub fn new(controllers: Vec<Box<dyn PanelController>>) -> Self {
        Self {
            controllers,
            disposed: false,
        }
    }

    /// The first member that is a `T`.
    pub fn member_mut<T: PanelController>(&mut self) -> Option<&mut T> {
        self.controllers.iter_mut().find_map(|c| {
            let boxed: &mut dyn PanelController = c.as_mut();
            <dyn PanelController as crate::tree::AsAny>::as_any_mut(boxed).downcast_mut::<T>()
        })
    }
}

impl PanelController for ControllerGroup {
    fn capture_window_state(&self, ui: &UiRoot) -> Option<WindowState> {
        self.controllers
            .iter()
            .find_map(|c| c.capture_window_state(ui))
    }

    fn restore_window_state(&mut self, ui: &mut UiRoot, state: &WindowState) -> bool {
        self.controllers
            .iter_mut()
            .any(|c| c.restore_window_state(ui, state))
    }

    fn on_shown(&mut self, ui: &mut UiRoot) {
        if self.disposed {
            return;
        }
        for c in &mut self.controllers {
            c.on_shown(ui);
        }
    }

    fn on_hidden(&mut self, ui: &mut UiRoot) {
        if self.disposed {
            return;
        }
        for c in self.controllers.iter_mut().rev() {
            c.on_hidden(ui);
        }
    }

    fn tick(&mut self, ui: &mut UiRoot, dt: f64) {
        if self.disposed {
            return;
        }
        for c in &mut self.controllers {
            c.tick(ui, dt);
        }
    }

    fn on_descendant_focus_changed(&mut self, ui: &mut UiRoot, focused: Option<ElementId>) {
        if self.disposed {
            return;
        }
        for c in &mut self.controllers {
            c.on_descendant_focus_changed(ui, focused);
        }
    }

    fn on_window_event(&mut self, ui: &mut UiRoot, event: &WindowEvent) {
        if self.disposed {
            return;
        }
        for c in &mut self.controllers {
            c.on_window_event(ui, event);
        }
    }

    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if self.disposed {
            return false;
        }
        self.controllers
            .iter_mut()
            .any(|c| c.on_element_message(ui, element, message))
    }

    fn dispose(&mut self, ui: &mut UiRoot) {
        if self.disposed {
            return;
        }
        self.disposed = true;
        for c in self.controllers.iter_mut().rev() {
            c.dispose(ui);
        }
    }
}

/// Attaching a controller failed.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum AttachError {
    /// No window of that name is registered.
    #[error("no retained window named '{0}' is registered")]
    UnknownWindow(String),
    /// The window already has a controller.
    #[error("retained window '{0}' already owns a controller")]
    AlreadyAttached(String),
}

/// The UI host. See the module documentation.
pub struct UiHost {
    /// The UI root.
    pub root: UiRoot,
    controllers: Vec<(String, Box<dyn PanelController>)>,
    tooltips: Option<TooltipPresenter>,
    outputs: Vec<UiOutput>,
}

impl std::fmt::Debug for UiHost {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("UiHost")
            .field("root", &self.root)
            .field("controllers", &self.controllers.len())
            .field("tooltips", &self.tooltips)
            .finish_non_exhaustive()
    }
}

impl Default for UiHost {
    fn default() -> Self {
        Self::new()
    }
}

impl UiHost {
    /// A host with an empty root and no tooltips.
    #[must_use]
    pub fn new() -> Self {
        Self {
            root: UiRoot::new(),
            controllers: Vec::new(),
            tooltips: None,
            outputs: Vec::new(),
        }
    }

    /// Show tooltips with `presenter`.
    pub fn set_tooltips(&mut self, presenter: TooltipPresenter) {
        self.tooltips = Some(presenter);
    }

    /// The tooltip presenter.
    pub fn tooltips_mut(&mut self) -> Option<&mut TooltipPresenter> {
        self.tooltips.as_mut()
    }

    /// Give window `name` its controller. When the window is visible the
    /// controller hears `on_shown` now.
    pub fn attach_controller(
        &mut self,
        name: &str,
        mut controller: Box<dyn PanelController>,
    ) -> Result<(), AttachError> {
        if self.root.window(name).is_none() {
            return Err(AttachError::UnknownWindow(name.to_owned()));
        }
        if self.controllers.iter().any(|(n, _)| n == name) {
            return Err(AttachError::AlreadyAttached(name.to_owned()));
        }
        if self.root.is_window_visible(name) {
            controller.on_shown(&mut self.root);
        }
        self.controllers.push((name.to_owned(), controller));
        self.pump();
        Ok(())
    }

    /// True when window `name` has a controller.
    #[must_use]
    pub fn has_controller(&self, name: &str) -> bool {
        self.controllers.iter().any(|(n, _)| n == name)
    }

    /// Run `f` on window `name`'s controller as a `T` with the root (feed it
    /// a view model, ask it to start a tell, ...), then route what it
    /// queued. `None` when there is no such controller or it is not a `T`.
    pub fn with_controller<T: PanelController, R>(
        &mut self,
        name: &str,
        f: impl FnOnce(&mut T, &mut UiRoot) -> R,
    ) -> Option<R> {
        let i = self.controller_index(name)?;
        let boxed: &mut dyn PanelController = self.controllers[i].1.as_mut();
        let any = <dyn PanelController as crate::tree::AsAny>::as_any_mut(boxed);
        // A window with several controllers: the member that is a `T`.
        let controller = if any.is::<T>() {
            any.downcast_mut::<T>()?
        } else {
            any.downcast_mut::<ControllerGroup>()?.member_mut::<T>()?
        };
        let r = f(controller, &mut self.root);
        self.pump();
        Some(r)
    }

    /// The state window `name` keeps: its controller's, else its frame's
    /// (a two-stop frame is collapsed or not), else none.
    #[must_use]
    pub fn capture_window_state(&self, name: &str) -> WindowState {
        if let Some(i) = self.controller_index(name)
            && let Some(s) = self.controllers[i].1.capture_window_state(&self.root)
        {
            return s;
        }
        self.root
            .window(name)
            .and_then(|w| {
                let h = self.root.element(w.frame)?.height;
                collapsible_frame_state(self.root.widget::<NineSliceFrame>(w.frame)?, h)
            })
            .unwrap_or_default()
    }

    /// Put back a saved state of window `name` (its controller's, else its
    /// two-stop frame's height and second row).
    pub fn restore_window_state(&mut self, name: &str, state: WindowState) {
        if let Some(i) = self.controller_index(name)
            && self.controllers[i]
                .1
                .restore_window_state(&mut self.root, &state)
        {
            self.pump();
            return;
        }
        let Some(frame) = self.root.window(name).map(|w| w.frame) else {
            return;
        };
        let Some((stop, rows)) = self.root.widget::<NineSliceFrame>(frame).and_then(|f| {
            (f.expanded_height > f.collapsed_height).then(|| {
                let stop = if state.collapsed {
                    f.collapsed_height
                } else {
                    f.expanded_height
                };
                (stop, f.second_row.clone())
            })
        }) else {
            return;
        };
        if let Some(e) = self.root.element_mut(frame) {
            e.height = stop;
        }
        for row in rows {
            self.root.set_visible(row, !state.collapsed);
        }
    }

    /// Route everything the root queued until it is quiet.
    pub fn pump(&mut self) {
        loop {
            let batch = self.root.drain_output();
            if batch.is_empty() {
                return;
            }
            for output in batch {
                self.route(output);
            }
        }
    }

    fn controller_index(&self, name: &str) -> Option<usize> {
        self.controllers.iter().position(|(n, _)| n == name)
    }

    fn route(&mut self, output: UiOutput) {
        match &output {
            UiOutput::TooltipShow(_) | UiOutput::TooltipHide(_) => {
                if let Some(t) = self.tooltips.as_mut() {
                    t.handle(&mut self.root, &output);
                }
                self.outputs.push(output);
            }
            UiOutput::Window { name, event } => {
                if let Some(i) = self.controller_index(name) {
                    let (_, c) = &mut self.controllers[i];
                    match event {
                        WindowEvent::Shown => c.on_shown(&mut self.root),
                        WindowEvent::Hidden => c.on_hidden(&mut self.root),
                        WindowEvent::DescendantFocusChanged(f) => {
                            c.on_descendant_focus_changed(&mut self.root, *f);
                        }
                        WindowEvent::Unregistered => {
                            let (_, mut c) = self.controllers.remove(i);
                            c.dispose(&mut self.root);
                        }
                        other => c.on_window_event(&mut self.root, other),
                    }
                }
                self.outputs.push(output);
            }
            UiOutput::Element { element, message } => {
                let owner = self.root.window_of(*element).map(|w| w.name.clone());
                let handled = match owner.and_then(|n| self.controller_index(&n)) {
                    Some(i) => {
                        self.controllers[i]
                            .1
                            .on_element_message(&mut self.root, *element, message)
                    }
                    None => false,
                };
                if !handled {
                    self.outputs.push(output);
                }
            }
            _ => self.outputs.push(output),
        }
    }

    /// What the client must hear, oldest first.
    pub fn take_outputs(&mut self) -> Vec<UiOutput> {
        std::mem::take(&mut self.outputs)
    }

    // ── Input ───────────────────────────────────────────────────────────

    /// The modifier keys now held.
    pub fn set_modifiers(&mut self, modifiers: Modifiers) {
        self.root.set_modifiers(modifiers);
    }

    /// Pointer moved to window (`x`, `y`).
    pub fn mouse_move(&mut self, x: i32, y: i32) {
        self.root.on_mouse_move(x, y);
        self.pump();
    }

    /// The pointer left the window: nothing is hovered any more.
    pub fn pointer_left(&mut self) {
        self.root.leave_hover();
        self.pump();
    }

    /// Mouse button down.
    pub fn mouse_down(&mut self, button: MouseButton, x: i32, y: i32, flags: u32) {
        self.root.on_mouse_down(button, x, y, flags);
        self.pump();
    }

    /// Mouse button up.
    pub fn mouse_up(&mut self, button: MouseButton, x: i32, y: i32, flags: u32) {
        self.root.on_mouse_up(button, x, y, flags);
        self.pump();
    }

    /// Wheel turned.
    pub fn scroll(&mut self, dy: i32) {
        self.root.on_scroll(dy);
        self.pump();
    }

    /// Wheel turned on either axis (each step -1, 0 or +1; +1 is left or
    /// up), with Shift held or not.
    pub fn scroll_xy(&mut self, dx: i32, dy: i32, shift: bool) {
        self.root.on_scroll_xy(dx, dy, shift);
        self.pump();
    }

    /// Key down.
    pub fn key_down(&mut self, key: Key, modifiers: Modifiers) {
        self.root.on_key_down(key, modifiers);
        self.pump();
    }

    /// Key up.
    pub fn key_up(&mut self, key: Key, modifiers: Modifiers) {
        self.root.on_key_up(key, modifiers);
        self.pump();
    }

    /// Character typed.
    pub fn char_typed(&mut self, c: char) {
        self.root.on_char(c);
        self.pump();
    }

    // ── Frame ───────────────────────────────────────────────────────────

    /// One UI frame: tick the root, then the tooltip presenter (with the
    /// world's hover state, when there is a world).
    pub fn tick(&mut self, dt: f64, now_ms: i64, world: Option<&dyn WorldHover>) {
        self.root.tick(dt, now_ms);
        self.pump();
        for (_, c) in &mut self.controllers {
            c.tick(&mut self.root, dt);
        }
        self.pump();
        if let Some(t) = self.tooltips.as_mut() {
            t.tick(&mut self.root, world);
        }
        self.pump();
    }

    /// Run only the tooltip presenter's frame (it also runs inside
    /// [`UiHost::tick`]).
    pub fn tick_tooltips(&mut self, world: Option<&dyn WorldHover>) {
        if let Some(t) = self.tooltips.as_mut() {
            t.tick(&mut self.root, world);
        }
        self.pump();
    }

    /// Draw the UI for a screen of `screen` pixels.
    pub fn draw(&mut self, screen: (f32, f32), sprites: &dyn SpriteSizes) -> DrawList {
        let list = self.root.draw(screen, sprites);
        self.pump();
        list
    }

    /// Unregister every window (each controller is released once), then
    /// release any controller left.
    pub fn shutdown(&mut self) {
        self.root.unregister_all_windows();
        self.pump();
        for (_, mut c) in std::mem::take(&mut self.controllers) {
            c.dispose(&mut self.root);
        }
        self.pump();
    }
}

impl Drop for UiHost {
    fn drop(&mut self) {
        for (_, mut c) in std::mem::take(&mut self.controllers) {
            c.dispose(&mut self.root);
        }
    }
}
