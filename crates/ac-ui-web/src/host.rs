//! The module behind the exports: what the page calls, in plain Rust.

use crate::output::Outputs;

/// Modifier keys held with an input event.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Modifiers {
    /// Shift.
    pub shift: bool,
    /// Control.
    pub ctrl: bool,
    /// Alt.
    pub alt: bool,
    /// The system key.
    pub meta: bool,
}

impl Modifiers {
    /// From the bits the page sends: 0 shift, 1 control, 2 alt, 3 system.
    #[must_use]
    pub fn from_bits(bits: u32) -> Self {
        Self {
            shift: bits & 1 != 0,
            ctrl: bits & 2 != 0,
            alt: bits & 4 != 0,
            meta: bits & 8 != 0,
        }
    }
}

/// What the browser may do with the session, as its relay says.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Session {
    /// The session's login generation: every command is stamped with it.
    pub generation: u64,
    /// The browser has control now.
    pub controlling: bool,
    /// It may take control.
    pub may_control: bool,
    /// It may log the character off.
    pub may_log_off: bool,
    /// It may reconnect the session.
    pub may_reconnect: bool,
}

impl Session {
    /// From what the page sends: the generation, whether it controls, and
    /// the permission bits (0 control, 1 log off, 2 reconnect).
    #[must_use]
    pub fn from_words(generation: u64, controlling: u32, permissions: u32) -> Self {
        Self {
            generation,
            controlling: controlling != 0,
            may_control: permissions & 1 != 0,
            may_log_off: permissions & 2 != 0,
            may_reconnect: permissions & 4 != 0,
        }
    }
}

/// The panels, as the module drives them. Every time is milliseconds on
/// the page's clock; every position is canvas pixels from the top left.
/// An input method returns whether the panels used the event (`false`: it
/// falls through to the world).
pub trait Panels {
    /// The stream encoding's shape, 16 hex characters.
    fn shape(&self) -> String;
    /// The canvas, once, after the stored settings and any preloaded dat
    /// files were handed in.
    fn init(&mut self, width: u32, height: u32, scale: f32, now: f64);
    /// The canvas changed size.
    fn resize(&mut self, width: u32, height: u32, scale: f32);
    /// What the browser may do changed.
    fn session(&mut self, session: Session);
    /// One stream frame from the session.
    fn stream(&mut self, frame: &[u8], now: f64) -> Result<(), String>;
    /// The attach ended.
    fn detached(&mut self);
    /// One animation frame: write what changed to `out`.
    fn frame(&mut self, now: f64, out: &mut Outputs);
    /// A key went down or up; `code` is the physical key's name.
    fn key(&mut self, down: bool, code: &str, modifiers: Modifiers, repeat: bool, now: f64)
    -> bool;
    /// Text was typed.
    fn text(&mut self, text: &str) -> bool;
    /// The pointer moved.
    fn pointer_move(&mut self, x: f32, y: f32, now: f64) -> bool;
    /// A pointer button (0 left, 1 middle, 2 right) went down or up.
    fn pointer_button(
        &mut self,
        button: u32,
        down: bool,
        at: (f32, f32),
        modifiers: Modifiers,
        now: f64,
    ) -> bool;
    /// The pointer left the canvas.
    fn pointer_leave(&mut self);
    /// The wheel turned by `lines` (positive: away from the user).
    fn wheel(&mut self, lines: f32, at: (f32, f32), modifiers: Modifiers) -> bool;
    /// The canvas gained or lost the keyboard.
    fn focus(&mut self, focused: bool);
    /// The clipboard's text, asked for earlier.
    fn paste(&mut self, text: &str);
    /// A dat file's bytes (`None`: the dat has no such file).
    fn dat(&mut self, source: u32, id: u32, bytes: Option<&[u8]>);
    /// A stored setting (`None`: nothing is stored under the key).
    fn storage(&mut self, key: &str, value: Option<&[u8]>);
    /// The dat files worth handing in before [`Panels::init`], as
    /// `(source, id)`; what is listed here counts as asked for.
    fn dat_preload(&mut self) -> Vec<(u32, u32)>;
    /// The page lost every texture it held: each is owed again, and the
    /// picture with them.
    fn textures_lost(&mut self);
}

/// The module: the panels and what they have handed out.
#[derive(Debug)]
pub struct Module<P> {
    panels: P,
    out: Outputs,
    /// The bytes last taken, kept until the next call so the page can read
    /// them.
    taken: Vec<u8>,
}

impl<P: Panels> Module<P> {
    /// A module around `panels`.
    pub fn new(panels: P) -> Self {
        Self {
            panels,
            out: Outputs::default(),
            taken: Vec::new(),
        }
    }

    /// The panels.
    pub fn panels(&mut self) -> &mut P {
        &mut self.panels
    }

    /// One animation frame: the mask of the kinds that have something to
    /// take.
    pub fn frame(&mut self, now: f64) -> u32 {
        self.panels.frame(now, &mut self.out);
        self.out.mask()
    }

    /// Take the bytes of `kind`: they stay readable until the next take.
    pub fn take(&mut self, kind: u32) -> &[u8] {
        self.taken = self.out.take(kind);
        &self.taken
    }

    /// Hand the page bytes that are not a frame's output (the shape, the
    /// preload list): readable until the next take.
    pub fn hand(&mut self, bytes: Vec<u8>) -> &[u8] {
        self.taken = bytes;
        &self.taken
    }

    /// The bytes last taken or handed.
    #[must_use]
    pub fn taken(&self) -> &[u8] {
        &self.taken
    }
}

/// Panels that show nothing and use no input: the module's interface with
/// nothing behind it.
#[derive(Debug, Default)]
pub struct NoPanels {
    /// A picture is owed (the first frame, and after a resize).
    stale: bool,
}

impl Panels for NoPanels {
    fn shape(&self) -> String {
        "0".repeat(16)
    }

    fn init(&mut self, _width: u32, _height: u32, _scale: f32, _now: f64) {
        self.stale = true;
    }

    fn resize(&mut self, _width: u32, _height: u32, _scale: f32) {
        self.stale = true;
    }

    fn session(&mut self, _session: Session) {}

    fn stream(&mut self, _frame: &[u8], _now: f64) -> Result<(), String> {
        Ok(())
    }

    fn detached(&mut self) {}

    fn frame(&mut self, _now: f64, out: &mut Outputs) {
        if std::mem::take(&mut self.stale) {
            out.picture(&[], &[]);
        }
    }

    fn key(
        &mut self,
        _down: bool,
        _code: &str,
        _modifiers: Modifiers,
        _repeat: bool,
        _now: f64,
    ) -> bool {
        false
    }

    fn text(&mut self, _text: &str) -> bool {
        false
    }

    fn pointer_move(&mut self, _x: f32, _y: f32, _now: f64) -> bool {
        false
    }

    fn pointer_button(
        &mut self,
        _button: u32,
        _down: bool,
        _at: (f32, f32),
        _modifiers: Modifiers,
        _now: f64,
    ) -> bool {
        false
    }

    fn pointer_leave(&mut self) {}

    fn wheel(&mut self, _lines: f32, _at: (f32, f32), _modifiers: Modifiers) -> bool {
        false
    }

    fn focus(&mut self, _focused: bool) {}

    fn paste(&mut self, _text: &str) {}

    fn dat(&mut self, _source: u32, _id: u32, _bytes: Option<&[u8]>) {}

    fn storage(&mut self, _key: &str, _value: Option<&[u8]>) {}

    fn dat_preload(&mut self) -> Vec<(u32, u32)> {
        Vec::new()
    }

    fn textures_lost(&mut self) {
        self.stale = true;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::output::kind;

    #[test]
    fn a_frame_says_what_there_is_to_take_and_taking_keeps_the_bytes_readable() {
        let mut module = Module::new(NoPanels::default());
        assert_eq!(module.frame(0.0), 0, "nothing before the canvas is known");
        module.panels().init(800, 600, 1.0, 0.0);
        let mask = module.frame(16.0);
        assert_eq!(
            mask, 0b11,
            "the first picture: corners and runs, both empty"
        );
        assert!(module.take(kind::VERTICES).is_empty());
        assert_eq!(module.frame(32.0), 0b10, "the runs are still to take");
        assert!(module.take(kind::RUNS).is_empty());
        assert_eq!(
            module.frame(48.0),
            0,
            "nothing changed: the last picture stands"
        );
        assert_eq!(module.hand(vec![1, 2, 3]), [1, 2, 3]);
        assert_eq!(module.taken(), [1, 2, 3]);
    }

    #[test]
    fn the_pages_words_are_read_as_modifiers_and_permissions() {
        assert_eq!(
            Modifiers::from_bits(0b0101),
            Modifiers {
                shift: true,
                alt: true,
                ..Modifiers::default()
            }
        );
        assert_eq!(
            Session::from_words(7, 1, 0b101),
            Session {
                generation: 7,
                controlling: true,
                may_control: true,
                may_log_off: false,
                may_reconnect: true,
            }
        );
    }
}
