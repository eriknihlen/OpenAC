//! Two presentation rules over every registered window: the chat windows'
//! opacity (one value while the window has the keyboard focus, another
//! otherwise, linked so the focused window is never the more transparent)
//! and the lock presentation (`/lockui`: the authored "locked" chrome shows,
//! the live chrome, the resize grips and the frames' grip art hide).
//!
//! Both follow the root each frame ([`WindowOpacity::apply`],
//! [`LockPresentation::follow`]) instead of hearing window events.

use std::collections::HashMap;

use ac_host::settings::chat_opacity;

use crate::root::UiRoot;
use crate::tree::ElementId;
use crate::widgets::{NineSliceFrame, ResizeGrip};

/// The chat windows whose opacity follows the focus.
pub const CHAT_WINDOW_NAMES: [&str; 5] = [
    "chat",
    "chat-window-1",
    "chat-window-2",
    "chat-window-3",
    "chat-window-4",
];

/// The chat windows' opacity. See the module documentation.
#[derive(Debug, Clone, PartialEq)]
pub struct WindowOpacity {
    default_opacity: f32,
    active_opacity: f32,
    disposed: bool,
}

impl WindowOpacity {
    /// The rule with these values (each clamped to 0..=1; the active one
    /// wins when they disagree: the default falls to it).
    #[must_use]
    pub fn new(default_opacity: f32, active_opacity: f32) -> Self {
        let (d, a) = chat_opacity::set_active(
            default_opacity.clamp(0.0, 1.0),
            active_opacity.clamp(0.0, 1.0),
        );
        Self {
            default_opacity: d,
            active_opacity: a,
            disposed: false,
        }
    }

    /// The unfocused opacity.
    #[must_use]
    pub fn default_opacity(&self) -> f32 {
        self.default_opacity
    }

    /// The focused opacity.
    #[must_use]
    pub fn active_opacity(&self) -> f32 {
        self.active_opacity
    }

    /// Set the unfocused opacity (the focused one rises to it).
    pub fn set_default_opacity(&mut self, value: f32) {
        if self.disposed {
            return;
        }
        (self.default_opacity, self.active_opacity) =
            chat_opacity::set_default(self.active_opacity, value);
    }

    /// Set the focused opacity (the unfocused one falls to it).
    pub fn set_active_opacity(&mut self, value: f32) {
        if self.disposed {
            return;
        }
        (self.default_opacity, self.active_opacity) =
            chat_opacity::set_active(self.default_opacity, value);
    }

    /// Set both, the default first (the order the player's options apply).
    pub fn set_opacity(&mut self, default_opacity: f32, active_opacity: f32) {
        self.set_default_opacity(default_opacity);
        self.set_active_opacity(active_opacity);
    }

    /// Stop following; the windows keep their opacity.
    pub fn dispose(&mut self) {
        self.disposed = true;
    }

    /// Give each registered chat window its opacity: active while the
    /// keyboard focus is inside it, default otherwise.
    pub fn apply(&self, ui: &mut UiRoot) {
        if self.disposed {
            return;
        }
        let focused = ui
            .keyboard_focus()
            .and_then(|f| ui.window_of(f))
            .map(|w| w.name.clone());
        for name in CHAT_WINDOW_NAMES {
            if ui.window(name).is_none() {
                continue;
            }
            let value = if focused.as_deref() == Some(name) {
                self.active_opacity
            } else {
                self.default_opacity
            };
            ui.set_window_opacity(name, value);
        }
    }
}

/// The authored chrome blocks: eight "locked" pieces then eight live ones.
pub const AUTHORED_CHROME_BLOCKS: [(u32, u32); 8] = [
    (0x1000_0633, 0x1000_063B),
    (0x1000_0643, 0x1000_064B),
    (0x1000_0653, 0x1000_065B),
    (0x1000_0663, 0x1000_066B),
    (0x1000_0673, 0x1000_067B),
    (0x1000_0683, 0x1000_068B),
    (0x1000_0693, 0x1000_069B),
    (0x1000_06A5, 0x1000_06AD),
];

/// The smart box's live chrome (no locked counterpart).
pub const SMART_BOX_LIVE_CHROME_START: u32 = 0x1000_06CA;

fn is_authored_locked_chrome(id: u32) -> bool {
    AUTHORED_CHROME_BLOCKS
        .iter()
        .any(|&(start, _)| id >= start && id < start + 8)
}

fn is_authored_live_chrome(id: u32) -> bool {
    AUTHORED_CHROME_BLOCKS
        .iter()
        .any(|&(_, start)| id >= start && id < start + 8)
        || (SMART_BOX_LIVE_CHROME_START..SMART_BOX_LIVE_CHROME_START + 8).contains(&id)
}

#[derive(Debug, Default, Clone)]
struct WindowChrome {
    locked: Vec<ElementId>,
    live: Vec<(ElementId, bool)>,
    frames: Vec<(ElementId, bool)>,
}

impl WindowChrome {
    fn capture(ui: &UiRoot, frame: ElementId) -> Self {
        let mut c = Self::default();
        let mut stack = vec![frame];
        while let Some(id) = stack.pop() {
            if let Some(f) = ui.widget::<NineSliceFrame>(id) {
                c.frames.push((id, f.draw_resize_affordances));
            }
            let dat_id = ui.element(id).map_or(0, |e| e.dat_element_id);
            let visible = ui.element(id).is_some_and(|e| e.visible());
            if is_authored_locked_chrome(dat_id) {
                c.locked.push(id);
            } else if is_authored_live_chrome(dat_id) || ui.widget::<ResizeGrip>(id).is_some() {
                c.live.push((id, visible));
            }
            let mut children = ui.children(id);
            children.reverse();
            stack.extend(children);
        }
        c
    }

    fn apply(&self, ui: &mut UiRoot, locked: bool) {
        for &id in &self.locked {
            ui.set_visible(id, locked);
        }
        for &(id, unlocked) in &self.live {
            ui.set_visible(id, !locked && unlocked);
        }
        for &(id, unlocked) in &self.frames {
            if let Some(f) = ui.widget_mut::<NineSliceFrame>(id) {
                f.draw_resize_affordances = !locked && unlocked;
            }
        }
    }
}

/// The lock presentation. See the module documentation.
#[derive(Debug, Default)]
pub struct LockPresentation {
    windows: HashMap<String, (ElementId, WindowChrome)>,
    applied: Option<bool>,
    disposed: bool,
}

impl LockPresentation {
    /// A presentation following nothing yet.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// One frame: windows registered since the last call take the current
    /// lock's chrome (their unlocked look captured first); a lock change
    /// swaps every window's chrome.
    pub fn follow(&mut self, ui: &mut UiRoot) {
        if self.disposed {
            return;
        }
        let locked = ui.ui_locked();
        let registered: Vec<(String, ElementId)> = ui
            .windows()
            .iter()
            .map(|w| (w.name.clone(), w.frame))
            .collect();
        self.windows
            .retain(|name, (frame, _)| registered.iter().any(|(n, f)| n == name && f == frame));
        for (name, frame) in registered {
            if self.windows.contains_key(&name) {
                continue;
            }
            let chrome = WindowChrome::capture(ui, frame);
            chrome.apply(ui, locked);
            self.windows.insert(name, (frame, chrome));
        }
        if self.applied != Some(locked) {
            self.applied = Some(locked);
            for (_, chrome) in self.windows.values() {
                chrome.apply(ui, locked);
            }
        }
    }

    /// Stop following.
    pub fn dispose(&mut self) {
        self.disposed = true;
        self.windows.clear();
    }
}
