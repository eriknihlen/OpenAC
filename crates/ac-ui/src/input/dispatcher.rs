//! The input dispatcher: key and button transitions in, actions out.
//!
//! A chord is looked up in the scope stack (top wins; the combat scope
//! shadows `Game`, the alternate camera scope shadows everything while its
//! key is held), and every distinct action bound to it in the winning scope
//! fires. Hold bindings fire a press on the way down, a hold on every tick
//! while down and a release on the way up; a scope or binding change
//! releases them first. A rebind capture takes the next chord instead of
//! firing anything.
//!
//! The dispatcher reads the devices (held keys and buttons, the modifiers,
//! whether the UI has the keyboard or the pointer) through
//! [`InputDevices`], and hands what fires to a sink, which may change the
//! dispatcher's scope before the next chord is looked at.

use std::collections::{HashMap, HashSet};

use crate::event::{Key, Modifiers};

use super::action::InputAction;
use super::bindings::KeyBindings;
use super::chord::{ActivationType, Binding, InputKey, InputScope, KeyChord};

/// Milliseconds within which a second press of the same button is a
/// double click.
pub const DOUBLE_CLICK_THRESHOLD_MS: u64 = 500;
/// Pixels a button may travel between press and release and still click.
pub const CLICK_DRAG_THRESHOLD_PIXELS: f32 = 3.0;

/// What the dispatcher reads from the devices.
pub trait InputDevices {
    /// A keyboard key is down.
    fn is_key_held(&self, key: Key) -> bool;
    /// A mouse button (by number) is down.
    fn is_button_held(&self, button: u8) -> bool;
    /// The modifiers held now.
    fn modifiers(&self) -> Modifiers;
    /// The UI has the pointer.
    fn want_capture_mouse(&self) -> bool;
    /// The UI has the keyboard (a text field is focused).
    fn want_capture_keyboard(&self) -> bool;
}

/// What the dispatcher hands its sink.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DispatchEvent {
    /// An action fired.
    Fired(InputAction, ActivationType),
    /// A rebind capture ended: the chord, or `None` when it was cancelled.
    Captured(Option<KeyChord>),
}

/// Where fired actions go. It may change the dispatcher (its scope, its
/// bindings) before the next event is looked at.
pub type Sink<'a> = dyn FnMut(&mut InputDispatcher, DispatchEvent) + 'a;

/// The dispatcher.
#[derive(Debug)]
pub struct InputDispatcher {
    bindings: KeyBindings,
    scopes: Vec<InputScope>,
    combat_scope: Option<InputScope>,
    camera_alternate_scope: bool,
    held_hold_chords: Vec<KeyChord>,
    /// The keys and buttons whose press the dispatcher took and that have
    /// not come up since: only these hold an action. A press the interface
    /// kept (a text field, a capture, the pointer over a window) holds
    /// nothing, and one taken before a field got the keyboard holds until
    /// it is released.
    pressed: HashSet<InputKey>,
    automation_held: HashSet<InputAction>,
    mouse_click_travel: HashMap<u8, f32>,
    active: bool,
    last_mouse_down: Option<(u8, u64)>,
    capturing: bool,
    capture_modifier_candidate: Option<Key>,
    current_physical_chord: Option<KeyChord>,
    pending: Vec<DispatchEvent>,
}

impl InputDispatcher {
    /// A dispatcher over `bindings`, active, with `Always` under `Game`.
    #[must_use]
    pub fn new(bindings: KeyBindings) -> Self {
        Self {
            bindings,
            scopes: vec![InputScope::Always, InputScope::Game],
            combat_scope: None,
            camera_alternate_scope: false,
            held_hold_chords: Vec::new(),
            pressed: HashSet::new(),
            automation_held: HashSet::new(),
            mouse_click_travel: HashMap::new(),
            active: true,
            last_mouse_down: None,
            capturing: false,
            capture_modifier_candidate: None,
            current_physical_chord: None,
            pending: Vec::new(),
        }
    }

    /// The bindings.
    #[must_use]
    pub fn bindings(&self) -> &KeyBindings {
        &self.bindings
    }

    /// Replace the bindings (held hold bindings are released first).
    pub fn set_bindings(&mut self, bindings: KeyBindings) {
        self.release_held_hold_bindings();
        self.bindings = bindings;
    }

    /// Stop dispatching: nothing fires until [`Self::activate`]; capture,
    /// held chords and keys, automation holds and click candidates are
    /// forgotten (a key still down holds nothing until pressed again).
    pub fn deactivate(&mut self) {
        self.active = false;
        self.capturing = false;
        self.capture_modifier_candidate = None;
        self.held_hold_chords.clear();
        self.pressed.clear();
        self.automation_held.clear();
        self.mouse_click_travel.clear();
        self.camera_alternate_scope = false;
    }

    /// Dispatch again.
    pub fn activate(&mut self) {
        self.active = true;
    }

    /// The chord being dispatched (inside a key-down's sink calls).
    #[must_use]
    pub fn current_physical_chord(&self) -> Option<KeyChord> {
        self.current_physical_chord
    }

    /// The scope looked up first.
    #[must_use]
    pub fn active_scope(&self) -> InputScope {
        let top = *self.scopes.last().unwrap_or(&InputScope::Always);
        if self.camera_alternate_scope {
            InputScope::Camera
        } else if top == InputScope::Game
            && let Some(combat) = self.combat_scope
        {
            combat
        } else {
            top
        }
    }

    /// The alternate-camera key went down or up.
    pub fn set_camera_alternate_scope(&mut self, active: bool) {
        if self.camera_alternate_scope == active {
            return;
        }
        self.release_held_hold_bindings();
        self.camera_alternate_scope = active;
    }

    /// Enter a combat scope (melee, missile or magic) or leave it (`None`).
    ///
    /// # Panics
    /// When `scope` is not a combat scope (a caller error).
    pub fn set_combat_scope(&mut self, scope: Option<InputScope>) {
        assert!(
            matches!(
                scope,
                None | Some(
                    InputScope::MeleeCombat | InputScope::MissileCombat | InputScope::MagicCombat
                )
            ),
            "not a combat scope: {scope:?}"
        );
        if self.combat_scope == scope {
            return;
        }
        self.release_held_hold_bindings();
        self.combat_scope = scope;
    }

    /// Push a scope (top wins).
    pub fn push_scope(&mut self, scope: InputScope) {
        self.release_held_hold_bindings();
        self.scopes.push(scope);
    }

    /// Pop the top scope, which must be `expected`; false (and nothing
    /// popped) when it is not.
    pub fn pop_scope(&mut self, expected: InputScope) -> bool {
        if self.scopes.last() != Some(&expected) {
            return false;
        }
        self.release_held_hold_bindings();
        self.scopes.pop();
        true
    }

    /// A rebind capture is open.
    #[must_use]
    pub fn is_capturing(&self) -> bool {
        self.capturing
    }

    /// The keyboard goes into text (a text field or a capture has it), so
    /// gameplay bindings are suppressed.
    #[must_use]
    pub fn wants_text_input(&self, devices: &dyn InputDevices) -> bool {
        self.capturing || devices.want_capture_keyboard()
    }

    /// Open a rebind capture: the next non-modifier chord (or a modifier
    /// pressed and released alone) is reported instead of fired; Escape
    /// cancels.
    pub fn begin_capture(&mut self) {
        self.capturing = true;
        self.capture_modifier_candidate = None;
    }

    /// Cancel the capture (reported as `Captured(None)`).
    pub fn cancel_capture(&mut self) {
        if !self.capturing {
            return;
        }
        self.capturing = false;
        self.capture_modifier_candidate = None;
        self.pending.push(DispatchEvent::Captured(None));
    }

    /// Automation presses an action (refused while inactive, capturing or
    /// while the UI has the keyboard).
    pub fn try_invoke_automation_action(
        &mut self,
        action: InputAction,
        devices: &dyn InputDevices,
        sink: &mut Sink<'_>,
    ) -> bool {
        if !self.active || self.capturing || devices.want_capture_keyboard() {
            return false;
        }
        self.pending
            .push(DispatchEvent::Fired(action, ActivationType::Press));
        self.dispatch(sink);
        true
    }

    /// Automation holds or lets go of an action (a hold is refused while
    /// capturing or while the UI has the keyboard; a release always goes).
    pub fn try_set_automation_action_held(
        &mut self,
        action: InputAction,
        held: bool,
        devices: &dyn InputDevices,
        sink: &mut Sink<'_>,
    ) -> bool {
        if !self.active {
            return false;
        }
        if held {
            if self.capturing || devices.want_capture_keyboard() {
                return false;
            }
            if self.automation_held.insert(action) {
                self.pending
                    .push(DispatchEvent::Fired(action, ActivationType::Press));
            }
        } else if self.automation_held.remove(&action) {
            self.pending
                .push(DispatchEvent::Fired(action, ActivationType::Release));
        }
        self.dispatch(sink);
        true
    }

    /// An action is held now: one of its chords is down with exactly its
    /// modifiers (a bare chord stays held with Shift added), its key's press
    /// was taken here, and it resolves to the action in the active scopes;
    /// or automation holds it. A held action ends when its key comes up,
    /// not when a text field takes the keyboard: the character running on
    /// a held key keeps running while the chat entry has the keyboard, and
    /// a key typed into the entry moves nothing.
    #[must_use]
    pub fn is_action_held(&self, action: InputAction, devices: &dyn InputDevices) -> bool {
        if !self.active || action == InputAction::None {
            return false;
        }
        if self.automation_held.contains(&action) {
            return true;
        }
        self.bindings.all().iter().any(|b| {
            b.action == action
                && self.pressed.contains(&b.chord.key)
                && is_chord_held(b.chord, devices)
                && self
                    .find_active_held(b.chord, b.activation, devices)
                    .is_some_and(|active| active.action == action)
        })
    }

    fn find_active_held(
        &self,
        candidate: KeyChord,
        activation: ActivationType,
        devices: &dyn InputDevices,
    ) -> Option<Binding> {
        let actual = candidate.with_modifiers(devices.modifiers());
        if let Some(active) = self.find_active(actual, activation) {
            return Some(active);
        }
        if candidate.modifiers == Modifiers::NONE && devices.modifiers() == Modifiers::SHIFT {
            self.find_active(candidate, activation)
        } else {
            None
        }
    }

    fn find_active(&self, chord: KeyChord, activation: ActivationType) -> Option<Binding> {
        self.find_active_bindings(chord, activation)
            .first()
            .copied()
    }

    fn find_active_bindings(&self, chord: KeyChord, activation: ActivationType) -> Vec<Binding> {
        if self.camera_alternate_scope {
            let camera = self.find_in_scope(InputScope::Camera, chord, activation);
            if !camera.is_empty() {
                return camera;
            }
        }
        for &scope in self.scopes.iter().rev() {
            if scope == InputScope::Game
                && let Some(combat) = self.combat_scope
            {
                let found = self.find_in_scope(combat, chord, activation);
                if !found.is_empty() {
                    return found;
                }
            }
            let found = self.find_in_scope(scope, chord, activation);
            if !found.is_empty() {
                return found;
            }
        }
        Vec::new()
    }

    fn find_in_scope(
        &self,
        scope: InputScope,
        chord: KeyChord,
        activation: ActivationType,
    ) -> Vec<Binding> {
        let mut found: Vec<Binding> = Vec::new();
        for b in self.bindings.all() {
            if b.scope == scope
                && b.chord == chord
                && b.activation == activation
                && !found.iter().any(|f| f.action == b.action)
            {
                found.push(*b);
            }
        }
        found
    }

    fn fire_all(&mut self, bindings: &[Binding], activation: ActivationType) {
        for b in bindings {
            self.pending
                .push(DispatchEvent::Fired(b.action, activation));
        }
    }

    fn release_held_hold_bindings(&mut self) {
        if self.held_hold_chords.is_empty() {
            return;
        }
        let mut releases = Vec::new();
        for chord in std::mem::take(&mut self.held_hold_chords) {
            releases.extend(self.find_active_bindings(chord, ActivationType::Hold));
        }
        self.fire_all(&releases, ActivationType::Release);
    }

    /// Hand everything queued (releases from a scope or binding change,
    /// a cancelled capture) to `sink`, in order.
    pub fn dispatch(&mut self, sink: &mut Sink<'_>) {
        while !self.pending.is_empty() {
            let event = self.pending.remove(0);
            sink(self, event);
        }
    }

    /// One frame: every held hold chord fires its hold (none while the UI
    /// has the keyboard). A chord the sink released meanwhile is skipped.
    pub fn tick(&mut self, devices: &dyn InputDevices, sink: &mut Sink<'_>) {
        if !self.active || devices.want_capture_keyboard() || self.held_hold_chords.is_empty() {
            return;
        }
        let snapshot = self.held_hold_chords.clone();
        for chord in snapshot {
            if !self.held_hold_chords.contains(&chord) {
                continue;
            }
            let holds = self.find_active_bindings(chord, ActivationType::Hold);
            self.fire_all(&holds, ActivationType::Hold);
            self.dispatch(sink);
        }
    }

    /// A key went down with `modifiers` held.
    pub fn key_down(
        &mut self,
        key: Key,
        modifiers: Modifiers,
        devices: &dyn InputDevices,
        sink: &mut Sink<'_>,
    ) {
        if !self.active {
            return;
        }
        if self.capturing {
            if key == Key::Escape {
                self.capturing = false;
                self.capture_modifier_candidate = None;
                self.pending.push(DispatchEvent::Captured(None));
            } else if is_modifier_key(key) {
                self.capture_modifier_candidate = Some(key);
            } else {
                self.capturing = false;
                self.capture_modifier_candidate = None;
                self.pending
                    .push(DispatchEvent::Captured(Some(KeyChord::key(key, modifiers))));
            }
            self.dispatch(sink);
            return;
        }
        if devices.want_capture_keyboard() {
            return;
        }
        self.pressed.insert(InputKey::Key(key));
        let chord = keyboard_chord(key, modifiers);
        self.current_physical_chord = Some(chord);
        let presses = self.find_active_bindings(chord, ActivationType::Press);
        self.fire_all(&presses, ActivationType::Press);
        let clicks = self.find_active_bindings(chord, ActivationType::Click);
        self.fire_all(&clicks, ActivationType::Click);
        let holds = self.find_active_bindings(chord, ActivationType::Hold);
        // The presses go out before the chord counts as held: a scope
        // change they cause does not release it.
        self.fire_all(&holds, ActivationType::Press);
        self.dispatch(sink);
        if !holds.is_empty() && !self.held_hold_chords.contains(&chord) {
            self.held_hold_chords.push(chord);
        }
        self.current_physical_chord = None;
    }

    /// A key came up with `modifiers` held. Releases always go through,
    /// even while the UI has the keyboard.
    pub fn key_up(&mut self, key: Key, modifiers: Modifiers, sink: &mut Sink<'_>) {
        if !self.active {
            return;
        }
        self.pressed.remove(&InputKey::Key(key));
        if self.capturing {
            if self.capture_modifier_candidate == Some(key) {
                self.capturing = false;
                self.capture_modifier_candidate = None;
                self.pending
                    .push(DispatchEvent::Captured(Some(keyboard_chord(
                        key, modifiers,
                    ))));
                self.dispatch(sink);
            }
            return;
        }
        let chord = keyboard_chord(key, modifiers);
        let releases = self.find_active_bindings(chord, ActivationType::Release);
        self.fire_all(&releases, ActivationType::Release);
        self.release_held_matching(InputKey::Key(key), 0);
        self.dispatch(sink);
    }

    fn release_held_matching(&mut self, key: InputKey, device: u8) {
        let (released, kept): (Vec<KeyChord>, Vec<KeyChord>) =
            std::mem::take(&mut self.held_hold_chords)
                .into_iter()
                .partition(|held| held.key == key && held.device == device);
        self.held_hold_chords = kept;
        for held in released {
            let holds = self.find_active_bindings(held, ActivationType::Hold);
            self.fire_all(&holds, ActivationType::Release);
        }
    }

    /// A mouse button went down at `now_ms` (any monotonic millisecond
    /// clock) with `modifiers` held.
    pub fn mouse_down(
        &mut self,
        button: u8,
        modifiers: Modifiers,
        now_ms: u64,
        devices: &dyn InputDevices,
        sink: &mut Sink<'_>,
    ) {
        if !self.active {
            return;
        }
        self.mouse_click_travel.remove(&button);
        if self.capturing {
            self.capturing = false;
            self.capture_modifier_candidate = None;
            self.pending
                .push(DispatchEvent::Captured(Some(KeyChord::mouse(
                    button, modifiers,
                ))));
            self.dispatch(sink);
            return;
        }
        if devices.want_capture_mouse() {
            return;
        }
        self.pressed.insert(InputKey::Mouse(button));
        let chord = KeyChord::mouse(button, modifiers);
        let presses = self.find_active_bindings(chord, ActivationType::Press);
        self.fire_all(&presses, ActivationType::Press);
        let holds = self.find_active_bindings(chord, ActivationType::Hold);
        self.fire_all(&holds, ActivationType::Press);
        self.dispatch(sink);
        if !holds.is_empty() && !self.held_hold_chords.contains(&chord) {
            self.held_hold_chords.push(chord);
        }
        if self.find_active(chord, ActivationType::Click).is_some() {
            self.mouse_click_travel.insert(button, 0.0);
        }
        match self.last_mouse_down {
            Some((last, at))
                if last == button && now_ms.saturating_sub(at) <= DOUBLE_CLICK_THRESHOLD_MS =>
            {
                let doubles = self.find_active_bindings(chord, ActivationType::DoubleClick);
                self.fire_all(&doubles, ActivationType::DoubleClick);
                self.last_mouse_down = None;
            }
            _ => self.last_mouse_down = Some((button, now_ms)),
        }
        self.dispatch(sink);
    }

    /// A mouse button came up with `modifiers` held: its releases, then a
    /// click when it was pressed off the UI and travelled at most
    /// [`CLICK_DRAG_THRESHOLD_PIXELS`].
    pub fn mouse_up(
        &mut self,
        button: u8,
        modifiers: Modifiers,
        devices: &dyn InputDevices,
        sink: &mut Sink<'_>,
    ) {
        if !self.active {
            return;
        }
        self.pressed.remove(&InputKey::Mouse(button));
        let chord = KeyChord::mouse(button, modifiers);
        let travel = self.mouse_click_travel.remove(&button);
        let releases = self.find_active_bindings(chord, ActivationType::Release);
        self.fire_all(&releases, ActivationType::Release);
        self.release_held_matching(InputKey::Mouse(button), 1);
        if let Some(travel) = travel
            && !devices.want_capture_mouse()
            && travel <= CLICK_DRAG_THRESHOLD_PIXELS
        {
            let clicks = self.find_active_bindings(chord, ActivationType::Click);
            self.fire_all(&clicks, ActivationType::Click);
        }
        self.dispatch(sink);
    }

    /// The pointer moved by (`dx`, `dy`) pixels: a held click candidate
    /// accumulates the distance (the UI taking the pointer drops them).
    pub fn mouse_move(&mut self, dx: f32, dy: f32, devices: &dyn InputDevices) {
        if !self.active || self.mouse_click_travel.is_empty() {
            return;
        }
        if devices.want_capture_mouse() {
            self.mouse_click_travel.clear();
            return;
        }
        let distance = (dx * dx + dy * dy).sqrt();
        self.mouse_click_travel.retain(|button, travel| {
            if devices.is_button_held(*button) {
                *travel += distance;
                true
            } else {
                false
            }
        });
    }

    /// A wheel step: up presses `ScrollUp`, down `ScrollDown` (magnitude
    /// dropped; nothing while the UI has the pointer).
    pub fn scroll(&mut self, delta: f32, devices: &dyn InputDevices, sink: &mut Sink<'_>) {
        if !self.active || devices.want_capture_mouse() {
            return;
        }
        if delta > 0.0 {
            self.pending.push(DispatchEvent::Fired(
                InputAction::ScrollUp,
                ActivationType::Press,
            ));
        } else if delta < 0.0 {
            self.pending.push(DispatchEvent::Fired(
                InputAction::ScrollDown,
                ActivationType::Press,
            ));
        }
        self.dispatch(sink);
    }
}

fn is_chord_held(chord: KeyChord, devices: &dyn InputDevices) -> bool {
    let down = match (chord.device, chord.key) {
        (0, InputKey::Key(key)) => devices.is_key_held(key),
        (1, InputKey::Mouse(button)) => devices.is_button_held(button),
        _ => false,
    };
    if !down {
        return false;
    }
    let mut current = devices.modifiers();
    if chord.modifiers == Modifiers::NONE {
        current = Modifiers(current.0 & !Modifiers::SHIFT.0);
    }
    current == chord.modifiers
}

fn is_modifier_key(key: Key) -> bool {
    matches!(
        key,
        Key::ShiftLeft
            | Key::ShiftRight
            | Key::ControlLeft
            | Key::ControlRight
            | Key::AltLeft
            | Key::AltRight
            | Key::SuperLeft
            | Key::SuperRight
    )
}

/// The chord of a key: a modifier key's own bit is dropped (the platform
/// reports Shift held while Shift itself goes down).
fn keyboard_chord(key: Key, modifiers: Modifiers) -> KeyChord {
    let own = match key {
        Key::ShiftLeft | Key::ShiftRight => Modifiers::SHIFT.0,
        Key::ControlLeft | Key::ControlRight => Modifiers::CTRL.0,
        Key::AltLeft | Key::AltRight => Modifiers::ALT.0,
        Key::SuperLeft | Key::SuperRight => Modifiers::WIN.0,
        _ => 0,
    };
    KeyChord::key(key, Modifiers(modifiers.0 & !own))
}

#[cfg(test)]
#[path = "dispatcher_tests.rs"]
mod tests;
