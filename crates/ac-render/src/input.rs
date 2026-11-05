//! The window's input state: which keys and mouse buttons are held, and
//! the rules that keep it honest.
//!
//! A key released while another window has the focus never reaches this
//! window, so losing the focus releases everything held (and ends mouse
//! look); otherwise a key would stay "held" until pressed again. Input that
//! arrives after teardown began is dropped.

use std::collections::BTreeSet;
use std::hash::Hash;

use crate::lifetime::QuiescenceGate;

/// What a focus change asks the window to do.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FocusChange<K> {
    /// The window has the focus now.
    pub focused: bool,
    /// Keys that were held and are now released, to be treated as key-ups.
    pub released_keys: Vec<K>,
    /// Mouse look (a captured, hidden cursor) must end.
    pub end_mouse_look: bool,
}

/// Held keys and buttons of one window. Generic over the key type so the
/// rules are tested without a platform.
#[derive(Debug)]
pub struct InputState<K: Ord + Copy + Hash> {
    held_keys: BTreeSet<K>,
    held_buttons: BTreeSet<u16>,
    focused: bool,
    mouse_look: bool,
    gate: QuiescenceGate,
}

impl<K: Ord + Copy + Hash> Default for InputState<K> {
    fn default() -> Self {
        Self::new()
    }
}

impl<K: Ord + Copy + Hash> InputState<K> {
    /// No keys held; the window has the focus (it is created in front).
    #[must_use]
    pub fn new() -> Self {
        Self {
            held_keys: BTreeSet::new(),
            held_buttons: BTreeSet::new(),
            focused: true,
            mouse_look: false,
            gate: QuiescenceGate::new(),
        }
    }

    /// Whether `key` is held.
    #[must_use]
    pub fn is_held(&self, key: K) -> bool {
        self.held_keys.contains(&key)
    }

    /// Whether mouse button `button` is held.
    #[must_use]
    pub fn is_button_held(&self, button: u16) -> bool {
        self.held_buttons.contains(&button)
    }

    /// Whether the window has the focus.
    #[must_use]
    pub fn is_focused(&self) -> bool {
        self.focused
    }

    /// Whether mouse look is on.
    #[must_use]
    pub fn mouse_look(&self) -> bool {
        self.mouse_look
    }

    /// A key went down or up. Returns true for a press that was not already
    /// held (a new press, not an auto-repeat). Ignored after teardown began.
    pub fn key(&mut self, key: K, pressed: bool) -> bool {
        self.gate
            .invoke(|| {
                if pressed {
                    self.focused && self.held_keys.insert(key)
                } else {
                    self.held_keys.remove(&key);
                    false
                }
            })
            .unwrap_or(false)
    }

    /// A mouse button went down or up. Ignored after teardown began.
    pub fn button(&mut self, button: u16, pressed: bool) {
        self.gate.invoke(|| {
            if pressed && self.focused {
                self.held_buttons.insert(button);
            } else if !pressed {
                self.held_buttons.remove(&button);
            }
        });
    }

    /// Turn mouse look on or off. Only a focused window can turn it on.
    /// Returns whether it is on.
    pub fn set_mouse_look(&mut self, on: bool) -> bool {
        self.mouse_look = on && self.focused && self.gate.is_accepting();
        self.mouse_look
    }

    /// The window gained or lost the focus. Losing it releases every held
    /// key and button and ends mouse look.
    pub fn focus_changed(&mut self, focused: bool) -> FocusChange<K> {
        let accepted = self.gate.invoke(|| {
            self.focused = focused;
            if focused {
                return FocusChange {
                    focused,
                    released_keys: Vec::new(),
                    end_mouse_look: false,
                };
            }
            let released_keys = std::mem::take(&mut self.held_keys).into_iter().collect();
            self.held_buttons.clear();
            let end_mouse_look = std::mem::replace(&mut self.mouse_look, false);
            FocusChange {
                focused,
                released_keys,
                end_mouse_look,
            }
        });
        accepted.unwrap_or(FocusChange {
            focused,
            released_keys: Vec::new(),
            end_mouse_look: false,
        })
    }

    /// Teardown began: drop all held input and accept nothing more.
    /// Returns whether mouse look was on (and must be ended).
    pub fn quiesce(&mut self) -> bool {
        self.gate.stop_accepting();
        self.held_keys.clear();
        self.held_buttons.clear();
        std::mem::replace(&mut self.mouse_look, false)
    }

    /// Whether input is still accepted.
    #[must_use]
    pub fn is_accepting(&self) -> bool {
        self.gate.is_accepting()
    }
}

#[cfg(test)]
mod tests {
    //! The behaviour of OpenAC `src/AcDream.App/Input/WindowFocusRouter.cs`
    //! (focus loss ends mouse look; `WindowFocusRouterTests`) and
    //! `QuiescentInputContext.cs` (no input after deactivation;
    //! `QuiescentInputContextTests.DeactivateSilencesKeyboardMouseAndConnectionRelaysBeforePhysicalDetach`),
    //! plus the release of held keys that polling the OS gave OpenAC for free.
    use super::*;

    #[test]
    fn a_press_is_new_once_and_a_release_clears_it() {
        let mut input = InputState::<u32>::new();
        assert!(input.key(17, true));
        assert!(!input.key(17, true), "auto-repeat is not a new press");
        assert!(input.is_held(17));
        assert!(!input.key(17, false));
        assert!(!input.is_held(17));
    }

    #[test]
    fn losing_the_focus_releases_held_keys_and_ends_mouse_look() {
        let mut input = InputState::<u32>::new();
        input.key(17, true);
        input.key(30, true);
        input.button(0, true);
        assert!(input.set_mouse_look(true));
        let change = input.focus_changed(false);
        assert_eq!(change.released_keys, [17, 30]);
        assert!(change.end_mouse_look);
        assert!(!input.is_held(17) && !input.is_button_held(0) && !input.mouse_look());
        // A key-up that went to another window cannot leave anything stuck,
        // and an unfocused window takes no presses or mouse look.
        assert!(!input.key(17, true));
        assert!(!input.set_mouse_look(true));
        let back = input.focus_changed(true);
        assert!(back.focused && back.released_keys.is_empty() && !back.end_mouse_look);
        assert!(input.key(17, true));
    }

    #[test]
    fn nothing_is_accepted_after_teardown_began() {
        let mut input = InputState::<u32>::new();
        input.key(17, true);
        input.set_mouse_look(true);
        assert!(input.quiesce(), "mouse look was on");
        assert!(!input.is_accepting());
        assert!(!input.key(18, true));
        input.button(0, true);
        assert!(!input.is_held(18) && !input.is_button_held(0));
        assert!(!input.set_mouse_look(true));
        assert!(input.focus_changed(false).released_keys.is_empty());
    }
}
