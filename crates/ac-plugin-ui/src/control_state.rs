//! What plugin markup controls remember for the logged-in character (OpenAC
//! `IMarkupControlState`, `MarkupControlStateStore`, `RememberedValue`,
//! `KeyedControlState`): short strings keyed by window, control name and
//! kind (an expander's `expanded`, a split's `position`, a list's `widths`,
//! `sort` and `expanded` rows, the pickers' shared `recent` colours).
//!
//! The window owns one [`ControlStateStore`]; it follows the character
//! (another character's values load on a switch, the old one's are saved
//! first), and [`ControlStateStore::save`] writes what changed, backing off
//! for five seconds after a failure. A settings file that cannot be read is
//! never written over: the session's changes are kept and laid over the
//! file once it reads again.

use std::collections::{BTreeMap, BTreeSet};

/// One remembered value's place: window, control, kind.
#[derive(Debug, Clone, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct ControlKey {
    /// The window (`*` for what every window shares).
    pub window: String,
    /// The control's name.
    pub control: String,
    /// What it remembers.
    pub kind: String,
}

impl ControlKey {
    /// A key from its parts.
    #[must_use]
    pub fn new(window: &str, control: &str, kind: &str) -> Self {
        Self {
            window: window.to_owned(),
            control: control.to_owned(),
            kind: kind.to_owned(),
        }
    }
}

/// One window's remembered values (OpenAC `IMarkupControlState`).
pub trait ControlState {
    /// Moves whenever every value may have changed (another character).
    fn generation(&mut self) -> u64;
    /// The value remembered for a control and kind.
    fn get(&mut self, control: &str, kind: &str) -> Option<String>;
    /// Remembers a value (the settings file is rewritten on the next save:
    /// call it when the player commits a change, not every frame).
    fn set(&mut self, control: &str, kind: &str, value: &str);
    /// Forgets a value (back at the markup's default).
    fn remove(&mut self, control: &str, kind: &str);
    /// Forgets every value a `<repeat>` row stored: names
    /// `name#path` (and `name#path/...` for repeats nested in it) for one of
    /// `names` and one of `paths` (OpenAC `IForgetsKeyedState`).
    fn forget_keyed(&mut self, names: &BTreeSet<String>, paths: &BTreeSet<String>);
}

/// Where a character's values are kept (the settings file).
pub trait ControlStateFile {
    /// Every value of `character`.
    ///
    /// # Errors
    /// The file exists but cannot be read; it must then not be saved over.
    fn load(&self, character: &str) -> Result<BTreeMap<ControlKey, String>, String>;
    /// Replaces every value of `character`.
    ///
    /// # Errors
    /// Why it could not be written.
    fn save(&self, character: &str, values: &BTreeMap<ControlKey, String>) -> Result<(), String>;
}

/// The window name for what every window shares (the recent colours).
pub const SHARED_WINDOW: &str = "*";

/// How long a failed save waits before it tries again, milliseconds.
pub const RETRY_DELAY_MS: u64 = 5000;

/// Every markup window's remembered values for the logged-in character.
/// See the module documentation.
pub struct ControlStateStore {
    file: Option<Box<dyn ControlStateFile>>,
    character_key: Box<dyn Fn() -> String>,
    clock_ms: Box<dyn Fn() -> u64>,
    values: BTreeMap<ControlKey, String>,
    character: Option<String>,
    generation: u64,
    dirty: bool,
    load_failed: bool,
    removed_while_failed: BTreeSet<ControlKey>,
    retry_after_ms: u64,
    reported_failure: bool,
    log: Vec<String>,
}

impl std::fmt::Debug for ControlStateStore {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ControlStateStore")
            .field("character", &self.character)
            .field("values", &self.values.len())
            .field("dirty", &self.dirty)
            .finish_non_exhaustive()
    }
}

/// Whether a character key may be written: not blank, and not the host's
/// no-character key `default`.
fn can_persist(key: Option<&str>) -> bool {
    key.is_some_and(|k| !k.trim().is_empty() && !k.eq_ignore_ascii_case("default"))
}

fn check(control: &str, kind: &str) {
    assert!(
        !control.trim().is_empty() && !kind.trim().is_empty(),
        "a remembered control value needs a control name and a kind"
    );
}

impl ControlStateStore {
    /// A store over `file` (none: values last the session) for the
    /// character `character_key` names, with `clock_ms` for the back-off.
    pub fn new(
        file: Option<Box<dyn ControlStateFile>>,
        character_key: impl Fn() -> String + 'static,
        clock_ms: impl Fn() -> u64 + 'static,
    ) -> Self {
        Self {
            file,
            character_key: Box::new(character_key),
            clock_ms: Box::new(clock_ms),
            values: BTreeMap::new(),
            character: None,
            generation: 0,
            dirty: false,
            load_failed: false,
            removed_while_failed: BTreeSet::new(),
            retry_after_ms: 0,
            reported_failure: false,
            log: Vec::new(),
        }
    }

    /// Where values are kept from now on (none: they last the session).
    pub fn set_file(&mut self, file: Option<Box<dyn ControlStateFile>>) {
        self.file = file;
    }

    /// One window's values.
    ///
    /// # Panics
    /// `name` is blank (a caller's bug).
    pub fn window(&mut self, name: &str) -> WindowState<'_> {
        assert!(!name.trim().is_empty(), "a window name must not be blank");
        WindowState {
            store: self,
            window: name.to_owned(),
        }
    }

    /// What every window shares.
    pub fn shared(&mut self) -> WindowState<'_> {
        self.window(SHARED_WINDOW)
    }

    /// The messages it logged since the last call (a failed save, a read
    /// problem), once each.
    pub fn take_log(&mut self) -> Vec<String> {
        std::mem::take(&mut self.log)
    }

    /// Writes the current character's values when any changed since the
    /// last save; after a failure, not again until the back-off passed
    /// (`ignore_backoff`: the last save, at shutdown).
    pub fn save(&mut self, ignore_backoff: bool) {
        if !self.dirty {
            return;
        }
        if !ignore_backoff && (self.clock_ms)() < self.retry_after_ms {
            return;
        }
        let character = self.character.clone();
        let Some(file) = self
            .file
            .as_ref()
            .filter(|_| can_persist(character.as_deref()))
        else {
            self.dirty = false;
            return;
        };
        let character = character.unwrap_or_default();
        if self.load_failed {
            let Ok(mut on_disk) = file.load(&character) else {
                self.back_off(None);
                return;
            };
            // The load failed before anything was read, so the values are
            // this session's changes only: lay them over the file as it is.
            for key in &self.removed_while_failed {
                on_disk.remove(key);
            }
            let merged = self.values.len();
            for (key, value) in std::mem::take(&mut self.values) {
                on_disk.insert(key, value);
            }
            self.log.push(format!(
                "markup controls: settings for {character} readable again; merged {merged} changes"
            ));
            self.values = on_disk;
            self.load_failed = false;
            self.removed_while_failed.clear();
            self.reported_failure = false;
        }
        let Some(file) = self.file.as_ref() else {
            return;
        };
        match file.save(&character, &self.values) {
            Ok(()) => {
                self.dirty = false;
                self.reported_failure = false;
                self.retry_after_ms = 0;
            }
            Err(why) => {
                self.back_off(Some(format!(
                    "markup controls: failed to save {character}: {why}"
                )));
            }
        }
    }

    fn back_off(&mut self, message: Option<String>) {
        self.retry_after_ms = (self.clock_ms)() + RETRY_DELAY_MS;
        if self.reported_failure {
            return;
        }
        self.reported_failure = true;
        if let Some(message) = message {
            self.log.push(message);
        }
    }

    fn follow_character(&mut self) {
        let character = (self.character_key)();
        if self.generation > 0 && self.character.as_deref() == Some(character.as_str()) {
            return;
        }
        self.save(true);
        if self.dirty {
            self.log.push(format!(
                "markup controls: unsaved changes for {} were dropped",
                self.character.as_deref().unwrap_or("")
            ));
        }
        self.dirty = false;
        self.load_failed = false;
        self.removed_while_failed.clear();
        self.retry_after_ms = 0;
        self.reported_failure = false;
        self.values = BTreeMap::new();
        if let Some(file) = self.file.as_ref()
            && can_persist(Some(&character))
        {
            match file.load(&character) {
                Ok(values) => self.values = values,
                Err(_) => {
                    self.load_failed = true;
                    self.reported_failure = true;
                    self.log.push(format!(
                        "markup controls: settings could not be read; {character} will not be saved until they can be"
                    ));
                }
            }
        }
        self.character = Some(character);
        self.generation += 1;
    }

    fn get(&mut self, window: &str, control: &str, kind: &str) -> Option<String> {
        check(control, kind);
        self.follow_character();
        self.values
            .get(&ControlKey::new(window, control, kind))
            .cloned()
    }

    fn set(&mut self, window: &str, control: &str, kind: &str, value: &str) {
        check(control, kind);
        self.follow_character();
        let key = ControlKey::new(window, control, kind);
        if self.values.get(&key).map(String::as_str) == Some(value) {
            return;
        }
        self.removed_while_failed.remove(&key);
        self.values.insert(key, value.to_owned());
        self.dirty = true;
    }

    fn remove(&mut self, window: &str, control: &str, kind: &str) {
        check(control, kind);
        self.follow_character();
        let key = ControlKey::new(window, control, kind);
        let removed = self.values.remove(&key).is_some();
        if self.load_failed {
            // The key may be in the file although this session never saw it.
            self.removed_while_failed.insert(key);
            self.dirty = true;
        } else if removed {
            self.dirty = true;
        }
    }

    fn forget_keyed(&mut self, window: &str, names: &BTreeSet<String>, paths: &BTreeSet<String>) {
        self.follow_character();
        let before = self.values.len();
        self.values
            .retain(|key, _| !(key.window == window && is_under(&key.control, names, paths)));
        if self.values.len() != before {
            self.dirty = true;
        }
    }
}

/// One window's view of a [`ControlStateStore`].
pub struct WindowState<'a> {
    store: &'a mut ControlStateStore,
    window: String,
}

impl std::fmt::Debug for WindowState<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WindowState")
            .field("window", &self.window)
            .finish_non_exhaustive()
    }
}

impl ControlState for WindowState<'_> {
    fn generation(&mut self) -> u64 {
        self.store.follow_character();
        self.store.generation
    }

    fn get(&mut self, control: &str, kind: &str) -> Option<String> {
        self.store.get(&self.window, control, kind)
    }

    fn set(&mut self, control: &str, kind: &str, value: &str) {
        self.store.set(&self.window, control, kind, value);
    }

    fn remove(&mut self, control: &str, kind: &str) {
        self.store.remove(&self.window, control, kind);
    }

    fn forget_keyed(&mut self, names: &BTreeSet<String>, paths: &BTreeSet<String>) {
        self.store.forget_keyed(&self.window, names, paths);
    }
}

/// One value a control remembers (OpenAC `RememberedValue`): inactive
/// without a control name (unnamed controls are not remembered). Ask
/// [`Self::try_refresh`] right before computing a value to remember.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RememberedValue {
    control: Option<String>,
    kind: &'static str,
    generation: Option<u64>,
}

impl RememberedValue {
    /// The value `kind` of the control named `control`.
    #[must_use]
    pub fn new(control: Option<&str>, kind: &'static str) -> Self {
        Self {
            control: control.filter(|c| !c.trim().is_empty()).map(str::to_owned),
            kind,
            generation: None,
        }
    }

    /// The control is named, so it remembers.
    #[must_use]
    pub fn is_active(&self) -> bool {
        self.control.is_some()
    }

    /// `Some` with the remembered text (inner `None`: none) the first time
    /// and after every character switch; `None` when nothing changed or it
    /// is inactive.
    pub fn try_refresh(&mut self, state: &mut dyn ControlState) -> Option<Option<String>> {
        let control = self.control.as_deref()?;
        let generation = state.generation();
        if self.generation == Some(generation) {
            return None;
        }
        self.generation = Some(generation);
        Some(state.get(control, self.kind))
    }

    /// Remembers what the player just chose; `None` forgets it (back at the
    /// default).
    pub fn remember(&self, state: &mut dyn ControlState, value: Option<&str>) {
        let Some(control) = self.control.as_deref() else {
            return;
        };
        match value {
            None => state.remove(control, self.kind),
            Some(v) => state.set(control, self.kind, v),
        }
    }
}

/// A key as a `<repeat>` row's path stores it: `%` and `/` written `%25`
/// and `%2F`, so a key with a slash cannot read as a nested path.
#[must_use]
pub fn escape_key(key: &str) -> String {
    if key.contains(['%', '/']) {
        key.replace('%', "%25").replace('/', "%2F")
    } else {
        key.to_owned()
    }
}

/// The path of a row keyed `key`, inside a row at `parent` (outermost
/// first, joined by `/`).
#[must_use]
pub fn keyed_path(parent: Option<&str>, key: &str) -> String {
    match parent {
        Some(p) => format!("{p}/{}", escape_key(key)),
        None => escape_key(key),
    }
}

/// The name a control in a row at `path` is stored under: `name#path`.
#[must_use]
pub fn keyed_name(control: &str, path: &str) -> String {
    format!("{control}#{path}")
}

/// Whether `control` is `name#path` for one of `names` and one of `paths`,
/// or `name#path/...` for a repeat nested in it.
#[must_use]
pub fn is_under(control: &str, names: &BTreeSet<String>, paths: &BTreeSet<String>) -> bool {
    let Some((name, path)) = control.split_once('#') else {
        return false;
    };
    if !names.contains(name) {
        return false;
    }
    if paths.contains(path) {
        return true;
    }
    path.match_indices('/')
        .any(|(slash, _)| paths.contains(&path[..slash]))
}

/// A row's view of its window's state: every name stored as `name#path`
/// (OpenAC `KeyedControlState`).
pub struct KeyedState<'a> {
    /// The window's state.
    pub root: &'a mut dyn ControlState,
    /// The row's path.
    pub path: &'a str,
}

impl std::fmt::Debug for KeyedState<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("KeyedState")
            .field("path", &self.path)
            .finish_non_exhaustive()
    }
}

impl ControlState for KeyedState<'_> {
    fn generation(&mut self) -> u64 {
        self.root.generation()
    }

    fn get(&mut self, control: &str, kind: &str) -> Option<String> {
        self.root.get(&keyed_name(control, self.path), kind)
    }

    fn set(&mut self, control: &str, kind: &str, value: &str) {
        self.root.set(&keyed_name(control, self.path), kind, value);
    }

    fn remove(&mut self, control: &str, kind: &str) {
        self.root.remove(&keyed_name(control, self.path), kind);
    }

    fn forget_keyed(&mut self, names: &BTreeSet<String>, paths: &BTreeSet<String>) {
        self.root.forget_keyed(names, paths);
    }
}

/// Values in memory only, for a window with no store and for tests.
#[derive(Debug, Clone, Default)]
pub struct MemoryState {
    /// The values, by control and kind.
    pub values: BTreeMap<(String, String), String>,
    /// What [`ControlState::generation`] answers.
    pub generation: u64,
    /// How many sets were made.
    pub sets: usize,
}

impl ControlState for MemoryState {
    fn generation(&mut self) -> u64 {
        self.generation
    }

    fn get(&mut self, control: &str, kind: &str) -> Option<String> {
        self.values
            .get(&(control.to_owned(), kind.to_owned()))
            .cloned()
    }

    fn set(&mut self, control: &str, kind: &str, value: &str) {
        self.sets += 1;
        self.values
            .insert((control.to_owned(), kind.to_owned()), value.to_owned());
    }

    fn remove(&mut self, control: &str, kind: &str) {
        self.values.remove(&(control.to_owned(), kind.to_owned()));
    }

    fn forget_keyed(&mut self, names: &BTreeSet<String>, paths: &BTreeSet<String>) {
        self.values
            .retain(|(control, _), _| !is_under(control, names, paths));
    }
}

#[cfg(test)]
mod tests;
