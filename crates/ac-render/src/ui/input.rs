//! Input routing: the UI first, the world (camera, fly keys, movement)
//! with what the UI leaves. This is the one place the split is decided.
//!
//! The rules are OpenAC's: the retained UI sees every event
//! (`RetainedUiInputBinding`), and the world's input dispatcher acts on a
//! key-down or a press only when the UI did not want it
//! (`InputCaptureSources`: `WantCaptureKeyboard` is "an element has the
//! keyboard focus", `WantCaptureMouse` is "the UI holds or is under the
//! pointer"). Here the UI says what it did not want by falling through
//! (`UiOutput::WorldKey`, `WorldMouse`, `WorldMouseMove`, `WorldScroll`),
//! and a key-down reaches the world only when it fell through and nothing
//! had the keyboard focus before it (typing into the chat entry moves
//! nobody). Releases always reach the world, so nothing held there can stay
//! stuck. While mouse look holds the cursor the pointer belongs to the
//! world and the UI sees none of it (OpenAC `MouseLookController`).
//! Typed characters follow their key-down, as the system sends them.

use ac_ui::UiHost;
use ac_ui::event::{Key, Modifiers, MouseButton};
use ac_ui::root::UiOutput;

/// What the world gets from one input event, in the window's own key
/// type (`K`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum WorldInput<K> {
    /// A key went down (`repeat`: the system's auto-repeat).
    KeyDown {
        /// The physical key.
        key: K,
        /// An auto-repeat of a held key.
        repeat: bool,
    },
    /// A key came up.
    KeyUp(K),
    /// A mouse button went down or up at window pixel (`x`, `y`).
    Button {
        /// The window's button number (0 left, 1 right, 2 middle, ...).
        button: u16,
        /// Down or up.
        pressed: bool,
    },
    /// The pointer moved.
    Moved,
    /// The wheel turned this many notches (positive away from the user).
    Wheel(f32),
}

/// One routed event: what the world gets, and what else the UI asked of
/// the client (session commands, the clipboard, ...).
#[derive(Debug)]
pub struct Routed<K> {
    /// The world's part, if any.
    pub world: Option<WorldInput<K>>,
    /// The UI's other outputs, oldest first.
    pub outputs: Vec<UiOutput>,
}

impl<K> Routed<K> {
    fn new(world: Option<WorldInput<K>>, outputs: Vec<UiOutput>) -> Self {
        Self { world, outputs }
    }
}

/// Take the fall-through outputs out of `outputs`; true when one matched.
fn fell_through(outputs: &mut Vec<UiOutput>, matches: impl Fn(&UiOutput) -> bool) -> bool {
    let before = outputs.len();
    outputs.retain(|o| !matches(o));
    outputs.len() != before
}

/// The UI's mouse button for a window button number (0 left, 1 right,
/// 2 middle); other buttons belong to the world alone.
#[must_use]
pub fn ui_button(button: u16) -> Option<MouseButton> {
    match button {
        0 => Some(MouseButton::Left),
        1 => Some(MouseButton::Right),
        2 => Some(MouseButton::Middle),
        _ => None,
    }
}

/// The button-and-modifier word a press carries (the Windows `MK_` bits
/// the game's elements read).
#[must_use]
pub fn mouse_flags(held: &[u16], modifiers: Modifiers) -> u32 {
    let mut flags = 0;
    for b in held {
        flags |= match b {
            0 => 0x0001,
            1 => 0x0002,
            2 => 0x0010,
            _ => 0,
        };
    }
    if modifiers.contains(Modifiers::SHIFT) {
        flags |= 0x0004;
    }
    if modifiers.contains(Modifiers::CTRL) {
        flags |= 0x0008;
    }
    flags
}

/// A key went down (`text`: the characters it typed) or up.
pub fn key<K>(
    ui: &mut UiHost,
    physical: K,
    key: Key,
    pressed: bool,
    repeat: bool,
    modifiers: Modifiers,
    text: Option<&str>,
) -> Routed<K> {
    if !pressed {
        ui.key_up(key, modifiers);
        return Routed::new(Some(WorldInput::KeyUp(physical)), ui.take_outputs());
    }
    let captured = ui.root.wants_keyboard();
    ui.key_down(key, modifiers);
    // A Ctrl chord (Ctrl+V, Ctrl+C) is a command, though the window still
    // reports its letter as text. AltGr comes as Ctrl and Alt together and
    // does type (AltGr+2 is "@" on some keyboards).
    let chord = modifiers.contains(Modifiers::CTRL) && !modifiers.contains(Modifiers::ALT);
    for c in text.into_iter().flat_map(str::chars).filter(|_| !chord) {
        // Control characters (Enter, Backspace, Tab, Escape) acted as keys.
        if !c.is_control() {
            ui.char_typed(c);
        }
    }
    let mut outputs = ui.take_outputs();
    let fell = fell_through(&mut outputs, |o| matches!(o, UiOutput::WorldKey { .. }));
    let world = (fell && !captured).then_some(WorldInput::KeyDown {
        key: physical,
        repeat,
    });
    Routed::new(world, outputs)
}

/// A mouse button went down or up at window pixel (`x`, `y`).
#[allow(clippy::too_many_arguments)]
pub fn button<K>(
    ui: &mut UiHost,
    button: u16,
    pressed: bool,
    x: i32,
    y: i32,
    flags: u32,
    mouse_look: bool,
) -> Routed<K> {
    let world = Some(WorldInput::Button { button, pressed });
    let Some(ui_button) = ui_button(button).filter(|_| !mouse_look) else {
        return Routed::new(world, Vec::new());
    };
    if pressed {
        ui.mouse_down(ui_button, x, y, flags);
    } else {
        ui.mouse_up(ui_button, x, y, flags);
    }
    let mut outputs = ui.take_outputs();
    let fell = fell_through(&mut outputs, |o| matches!(o, UiOutput::WorldMouse { .. }));
    Routed::new(world.filter(|_| fell), outputs)
}

/// The pointer left the window: nothing in it is hovered any more (a
/// tooltip goes, a plugin canvas hears its leave). The world hears nothing.
pub fn left<K>(ui: &mut UiHost) -> Routed<K> {
    ui.pointer_left();
    Routed::new(None, ui.take_outputs())
}

/// The pointer moved to window pixel (`x`, `y`).
pub fn moved<K>(ui: &mut UiHost, x: i32, y: i32, mouse_look: bool) -> Routed<K> {
    if mouse_look {
        return Routed::new(Some(WorldInput::Moved), Vec::new());
    }
    ui.mouse_move(x, y);
    let mut outputs = ui.take_outputs();
    let fell = fell_through(&mut outputs, |o| {
        matches!(o, UiOutput::WorldMouseMove { .. })
    });
    Routed::new(fell.then_some(WorldInput::Moved), outputs)
}

/// The wheel turned: `(sideways, notches)`, sideways positive toward the
/// left and notches positive away from the user (a touchpad gives
/// fractions), with Shift held or not.
pub fn wheel<K>(
    ui: &mut UiHost,
    (sideways, notches): (f32, f32),
    shift: bool,
    mouse_look: bool,
) -> Routed<K> {
    if mouse_look {
        return Routed::new(Some(WorldInput::Wheel(notches)), Vec::new());
    }
    // Each wheel event is one step in its direction on each axis, a small
    // trackpad delta included. Trackpads add noise on the off axis, so only
    // the dominant axis counts.
    let (mut x, mut y) = (sideways, notches);
    if x != 0.0 && y != 0.0 {
        if x.abs() >= y.abs() {
            y = 0.0;
        } else {
            x = 0.0;
        }
    }
    let step = |v: f32| {
        if v > 0.0 {
            1
        } else if v < 0.0 {
            -1
        } else {
            0
        }
    };
    let (dx, dy) = (step(x), step(y));
    if dx == 0 && dy == 0 {
        let over_ui = ui.root.wants_mouse();
        return Routed::new((!over_ui).then_some(WorldInput::Wheel(notches)), Vec::new());
    }
    ui.scroll_xy(dx, dy, shift);
    let mut outputs = ui.take_outputs();
    let mut fell_dy = None;
    outputs.retain(|o| match o {
        UiOutput::WorldScroll { dy } => {
            fell_dy = Some(*dy);
            false
        }
        _ => true,
    });
    // A step nothing took reaches the world as the vertical step it was
    // routed as (a Shift sideways tick routes as one).
    let world = fell_dy.map(|d| WorldInput::Wheel(if y != 0.0 { y } else { d as f32 }));
    Routed::new(world, outputs)
}

#[cfg(test)]
mod tests {
    //! The split of OpenAC `InputCaptureSources` / `InputDispatcher`
    //! (`WantCaptureKeyboard` gates key-downs, releases always pass;
    //! `WantCaptureMouse` keeps presses over the UI from the camera) and
    //! `MouseLookController` (mouse look takes the pointer from the UI),
    //! over the retained root's fall-through outputs.
    use super::*;
    use ac_ui::event::{UiEvent, kind};
    use ac_ui::root::UiRoot;
    use ac_ui::tree::{Element, ElementId, Widget};

    /// A text entry: takes the focus, records characters.
    #[derive(Debug, Default)]
    struct Entry {
        text: String,
    }

    impl Widget for Entry {
        fn on_event(&mut self, _ui: &mut UiRoot, _me: ElementId, e: &UiEvent) -> bool {
            match e.kind {
                kind::CHAR => {
                    if let Some(c) = char::from_u32(e.data0 as u32) {
                        self.text.push(c);
                    }
                    true
                }
                kind::MOUSE_DOWN | kind::MOUSE_UP | kind::CLICK => true,
                _ => false,
            }
        }
    }

    /// A 100 x 100 UI with a focusable entry at (10, 10)..(60, 30).
    fn host() -> (UiHost, ElementId) {
        let mut host = UiHost::new();
        host.root.set_size(100.0, 100.0);
        let root = host.root.root();
        let entry = host.root.add(
            root,
            Element {
                accepts_focus: true,
                is_edit_control: true,
                ..Element::at(10.0, 10.0, 50.0, 20.0)
            },
            Entry::default(),
        );
        host.root.default_text_input = Some(entry);
        (host, entry)
    }

    fn down(ui: &mut UiHost, k: u32, key: Key, text: Option<&str>) -> Routed<u32> {
        super::key(ui, k, key, true, false, Modifiers::NONE, text)
    }

    #[test]
    fn a_key_nobody_wants_reaches_the_world() {
        let (mut ui, _) = host();
        let r = down(&mut ui, 17, Key::W, Some("w"));
        assert_eq!(
            r.world,
            Some(WorldInput::KeyDown {
                key: 17,
                repeat: false
            })
        );
        assert!(
            r.outputs
                .iter()
                .all(|o| !matches!(o, UiOutput::WorldKey { .. }))
        );
    }

    #[test]
    fn typing_into_the_focused_entry_moves_nobody_but_releases_still_pass() {
        let (mut ui, entry) = host();
        // Enter with nothing focused focuses the chat entry (the UI's).
        let r = down(&mut ui, 28, Key::Enter, Some("\r"));
        assert_eq!(r.world, None);
        assert_eq!(ui.root.keyboard_focus(), Some(entry));
        let r = down(&mut ui, 17, Key::W, Some("w"));
        assert_eq!(r.world, None, "W typed, not walked");
        assert_eq!(ui.root.widget::<Entry>(entry).unwrap().text, "w");
        // The release reaches the world all the same.
        let r = super::key(&mut ui, 17, Key::W, false, false, Modifiers::NONE, None);
        assert_eq!(r.world, Some(WorldInput::KeyUp(17)));
        // An F-key while typing is the entry's too (keyboard captured).
        assert_eq!(down(&mut ui, 62, Key::F4, None).world, None);
    }

    #[test]
    fn the_pointer_leaving_the_window_ends_the_hover_and_the_world_hears_nothing() {
        let (mut ui, entry) = host();
        let _: Routed<u32> = moved(&mut ui, 20, 20, false);
        assert_eq!(ui.root.hovered(), Some(entry));
        let r: Routed<u32> = left(&mut ui);
        assert_eq!(r.world, None);
        assert_eq!(ui.root.hovered(), None);
    }

    #[test]
    fn control_characters_are_not_typed() {
        let (mut ui, entry) = host();
        ui.root.set_keyboard_focus(Some(entry));
        down(&mut ui, 14, Key::Backspace, Some("\u{8}"));
        down(&mut ui, 30, Key::A, Some("a"));
        assert_eq!(ui.root.widget::<Entry>(entry).unwrap().text, "a");
    }

    #[test]
    fn a_ctrl_chord_types_nothing_but_altgr_still_types() {
        let (mut ui, entry) = host();
        ui.root.set_keyboard_focus(Some(entry));
        // Ctrl+V arrives with its letter as text; it is a command, not typing.
        super::key(&mut ui, 47, Key::V, true, false, Modifiers::CTRL, Some("v"));
        assert_eq!(ui.root.widget::<Entry>(entry).unwrap().text, "");
        // AltGr reports Ctrl and Alt together; AltGr+2 types "@" on some
        // keyboards.
        let altgr = Modifiers::CTRL | Modifiers::ALT;
        super::key(&mut ui, 3, Key::Number2, true, false, altgr, Some("@"));
        assert_eq!(ui.root.widget::<Entry>(entry).unwrap().text, "@");
    }

    #[test]
    fn an_auto_repeat_is_marked_for_the_world() {
        let (mut ui, _) = host();
        let r = super::key(&mut ui, 17, Key::W, true, true, Modifiers::NONE, None);
        assert_eq!(
            r.world,
            Some(WorldInput::KeyDown {
                key: 17,
                repeat: true
            })
        );
    }

    #[test]
    fn a_press_over_the_ui_stays_there_and_one_over_the_world_does_not() {
        let (mut ui, _) = host();
        let over_ui: Routed<u32> = button(&mut ui, 0, true, 20, 20, 1, false);
        assert_eq!(over_ui.world, None);
        let _: Routed<u32> = button(&mut ui, 0, false, 20, 20, 0, false);
        let over_world: Routed<u32> = button(&mut ui, 0, true, 80, 80, 1, false);
        assert_eq!(
            over_world.world,
            Some(WorldInput::Button {
                button: 0,
                pressed: true
            })
        );
        // Back/forward buttons are the world's alone.
        let side: Routed<u32> = button(&mut ui, 3, true, 20, 20, 0, false);
        assert!(side.world.is_some());
    }

    #[test]
    fn mouse_look_takes_the_pointer_from_the_ui() {
        let (mut ui, _) = host();
        let r: Routed<u32> = button(&mut ui, 0, true, 20, 20, 1, true);
        assert!(r.world.is_some() && r.outputs.is_empty());
        let r: Routed<u32> = moved(&mut ui, 20, 20, true);
        assert_eq!(r.world, Some(WorldInput::Moved));
        assert_eq!(ui.root.mouse(), (0, 0), "the UI saw no move");
        let r: Routed<u32> = wheel(&mut ui, (0.0, 1.0), false, true);
        assert_eq!(r.world, Some(WorldInput::Wheel(1.0)));
    }

    #[test]
    fn the_wheel_over_the_world_zooms_and_a_fraction_goes_by_the_pointer() {
        let (mut ui, _) = host();
        let _: Routed<u32> = moved(&mut ui, 80, 80, false);
        let r: Routed<u32> = wheel(&mut ui, (0.0, -1.0), false, false);
        assert_eq!(r.world, Some(WorldInput::Wheel(-1.0)));
        let r: Routed<u32> = wheel(&mut ui, (0.0, 0.25), false, false);
        assert_eq!(r.world, Some(WorldInput::Wheel(0.25)));
        let _: Routed<u32> = moved(&mut ui, 20, 20, false);
        let r: Routed<u32> = wheel(&mut ui, (0.0, 0.25), false, false);
        assert_eq!(r.world, None, "a fraction over the UI is not the camera's");
    }

    /// A list of eight 36-pixel cells, seven tall, under the pointer.
    fn eight_cell_list() -> (UiHost, ElementId) {
        use ac_ui::widgets::item_slot::slot_element;
        use ac_ui::widgets::{ItemCell, ItemList};
        let mut ui = UiHost::new();
        ui.root.set_size(800.0, 600.0);
        let root = ui.root.root();
        let list = ItemList::create(&mut ui.root, root, Element::at(0.0, 0.0, 36.0, 252.0));
        ui.root.with::<ItemList, _>(list, |l, ui| {
            l.cell_width = 36.0;
            l.cell_height = 36.0;
            for _ in 1..8 {
                let cell = ui.insert(slot_element(), ItemCell::default());
                l.add_item(ui, list, cell);
            }
        });
        let _: Routed<u32> = moved(&mut ui, 18, 234, false);
        (ui, list)
    }

    #[test]
    fn every_wheel_event_is_one_step_in_its_direction() {
        // OpenAC v0.1.25 (#195) `SilkRetainedMouseScrollTests`:
        // DownwardWheelEvent_revealsEighthSlotThroughMouseSurface and
        // WheelEvent_emitsOneDirectionalStep (a trackpad's 0.01 and a
        // three-notch flick are each one step).
        use ac_ui::widgets::ItemList;
        for delta in [-0.01, -0.5, -1.0, -3.0] {
            let (mut ui, list) = eight_cell_list();
            let r: Routed<u32> = wheel(&mut ui, (0.0, delta), false, false);
            assert_eq!(r.world, None);
            let scroll = ui.root.widget::<ItemList>(list).unwrap().scroll.scroll_y();
            assert_eq!(scroll, 36, "{delta}");
        }
        for delta in [0.01, 0.5, 1.0, 3.0] {
            let (mut ui, list) = eight_cell_list();
            let _: Routed<u32> = wheel(&mut ui, (0.0, -3.0), false, false);
            let _: Routed<u32> = wheel(&mut ui, (0.0, delta), false, false);
            let scroll = ui.root.widget::<ItemList>(list).unwrap().scroll.scroll_y();
            assert_eq!(scroll, 0, "{delta}");
        }
    }

    #[test]
    fn the_press_flags_carry_buttons_and_modifiers() {
        assert_eq!(mouse_flags(&[0], Modifiers::NONE), 0x0001);
        assert_eq!(
            mouse_flags(&[1, 2], Modifiers::SHIFT),
            0x0002 | 0x0010 | 0x0004
        );
        assert_eq!(mouse_flags(&[], Modifiers::CTRL), 0x0008);
    }
}
