//! Ported from OpenAC `tests/AcDream.App.Tests/UI/MarkupControlStateStoreTests.cs`,
//! `RememberedValueTests.cs` and `KeyedControlStateTests.cs`. The settings
//! file is an in-memory stand-in that can be made unreadable or unwritable,
//! as the C# tests do with a corrupt file and a blocking file.

use std::cell::{Cell, RefCell};
use std::rc::Rc;

use super::*;

#[derive(Default)]
struct Disk {
    /// Every character's values.
    characters: BTreeMap<String, BTreeMap<ControlKey, String>>,
    /// The file cannot be read (it is corrupt).
    unreadable: bool,
    /// The file cannot be written (something blocks it).
    blocked: bool,
    /// How many saves reached the file.
    writes: usize,
}

#[derive(Clone, Default)]
struct File(Rc<RefCell<Disk>>);

impl ControlStateFile for File {
    fn load(&self, character: &str) -> Result<BTreeMap<ControlKey, String>, String> {
        let disk = self.0.borrow();
        if disk.unreadable {
            return Err("corrupt".to_owned());
        }
        Ok(disk.characters.get(character).cloned().unwrap_or_default())
    }

    fn save(&self, character: &str, values: &BTreeMap<ControlKey, String>) -> Result<(), String> {
        let mut disk = self.0.borrow_mut();
        if disk.blocked {
            return Err("blocked".to_owned());
        }
        if disk.unreadable {
            return Err("unreadable".to_owned());
        }
        disk.characters.insert(character.to_owned(), values.clone());
        disk.writes += 1;
        Ok(())
    }
}

struct Fixture {
    file: File,
    character: Rc<RefCell<String>>,
    now: Rc<Cell<u64>>,
}

impl Fixture {
    fn new() -> Self {
        Self {
            file: File::default(),
            character: Rc::new(RefCell::new("alice".to_owned())),
            now: Rc::new(Cell::new(0)),
        }
    }

    fn store(&self) -> ControlStateStore {
        let character = Rc::clone(&self.character);
        let now = Rc::clone(&self.now);
        ControlStateStore::new(
            Some(Box::new(self.file.clone())),
            move || character.borrow().clone(),
            move || now.get(),
        )
    }

    fn be(&self, character: &str) {
        *self.character.borrow_mut() = character.to_owned();
    }

    fn writes(&self) -> usize {
        self.file.0.borrow().writes
    }
}

fn get(store: &mut ControlStateStore, window: &str, control: &str, kind: &str) -> Option<String> {
    store.window(window).get(control, kind)
}

#[test]
fn values_are_kept_per_window_control_and_kind() {
    // ValuesAreKeptPerWindowControlAndKind
    let f = Fixture::new();
    let mut store = f.store();
    store.window("nav").set("adv", "expanded", "false");
    store.window("nav").set("adv", "widths", "10");
    store.window("loot").set("adv", "expanded", "true");
    assert_eq!(
        get(&mut store, "nav", "adv", "expanded").as_deref(),
        Some("false")
    );
    assert_eq!(
        get(&mut store, "nav", "adv", "widths").as_deref(),
        Some("10")
    );
    assert_eq!(
        get(&mut store, "loot", "adv", "expanded").as_deref(),
        Some("true")
    );
    assert_eq!(get(&mut store, "nav", "other", "expanded"), None);
}

#[test]
fn saved_values_load_into_a_fresh_store_and_unsaved_ones_do_not() {
    // SavedValuesLoadIntoAFreshStore, UnsavedValuesAreNotWritten
    let f = Fixture::new();
    f.store().window("nav").set("adv", "expanded", "false");
    assert_eq!(get(&mut f.store(), "nav", "adv", "expanded"), None);
    let mut store = f.store();
    store.window("nav").set("adv", "expanded", "false");
    store.save(false);
    assert_eq!(
        get(&mut f.store(), "nav", "adv", "expanded").as_deref(),
        Some("false")
    );
}

#[test]
fn save_writes_only_when_something_changed() {
    // SaveWritesOnlyWhenSomethingChanged
    let f = Fixture::new();
    let mut store = f.store();
    store.window("nav").set("adv", "expanded", "false");
    store.save(false);
    assert_eq!(f.writes(), 1);
    store.save(false);
    store.window("nav").set("adv", "expanded", "false");
    store.save(false);
    assert_eq!(f.writes(), 1);
    store.window("nav").set("adv", "expanded", "true");
    store.save(false);
    assert_eq!(f.writes(), 2);
}

#[test]
fn forget_keyed_drops_only_that_windows_named_values_under_the_paths() {
    // ForgetKeyedDropsOnlyThatWindowsNamedValuesUnderThePathsAndIsSaved
    let f = Fixture::new();
    let mut store = f.store();
    {
        let mut nav = store.window("nav");
        nav.set("row#7", "expanded", "true");
        nav.set("row#7/3", "widths", "10");
        nav.set("row#70", "expanded", "true");
        nav.set("row#8", "expanded", "true");
        nav.set("other#7", "expanded", "true");
        nav.set("adv", "expanded", "true");
    }
    store.window("loot").set("row#7", "expanded", "true");
    store.save(false);
    let names = BTreeSet::from(["row".to_owned()]);
    let paths = BTreeSet::from(["7".to_owned()]);
    store.window("nav").forget_keyed(&names, &paths);
    store.save(false);
    let mut fresh = f.store();
    assert_eq!(get(&mut fresh, "nav", "row#7", "expanded"), None);
    assert_eq!(get(&mut fresh, "nav", "row#7/3", "widths"), None);
    for control in ["row#70", "row#8", "other#7", "adv"] {
        assert_eq!(
            get(&mut fresh, "nav", control, "expanded").as_deref(),
            Some("true"),
            "{control}"
        );
    }
    assert_eq!(
        get(&mut fresh, "loot", "row#7", "expanded").as_deref(),
        Some("true")
    );
}

#[test]
fn each_character_has_its_own_values() {
    // EachCharacterHasItsOwnValues, GenerationIsStableForTheSameCharacter
    let f = Fixture::new();
    let mut store = f.store();
    store.window("nav").set("adv", "expanded", "false");
    let alice = store.window("nav").generation();
    assert_eq!(store.window("nav").generation(), alice);
    f.be("bob");
    assert_eq!(get(&mut store, "nav", "adv", "expanded"), None);
    assert!(store.window("nav").generation() > alice);
    store.window("nav").set("adv", "expanded", "true");
    f.be("alice");
    assert_eq!(
        get(&mut store, "nav", "adv", "expanded").as_deref(),
        Some("false")
    );
    store.save(false);
    let mut fresh = f.store();
    assert_eq!(
        get(&mut fresh, "nav", "adv", "expanded").as_deref(),
        Some("false")
    );
    f.be("bob");
    assert_eq!(
        get(&mut fresh, "nav", "adv", "expanded").as_deref(),
        Some("true")
    );
}

#[test]
fn without_a_settings_file_values_last_the_session() {
    // WithoutASettingsFileValuesLastTheSession
    let mut store = ControlStateStore::new(None, || "alice".to_owned(), || 0);
    store.window("nav").set("adv", "expanded", "false");
    store.save(false);
    assert_eq!(
        get(&mut store, "nav", "adv", "expanded").as_deref(),
        Some("false")
    );
}

#[test]
fn a_blank_or_default_character_is_never_written() {
    // ABlankCharacterIsNeverWritten, TheHostsNoCharacterKeyIsNeverWritten
    for key in ["", "default", "Default"] {
        let f = Fixture::new();
        f.be(key);
        let mut store = f.store();
        store.window("nav").set("adv", "expanded", "false");
        store.save(false);
        assert_eq!(f.writes(), 0, "{key:?}");
    }
}

#[test]
#[should_panic(expected = "window name")]
fn a_blank_window_name_is_a_bug() {
    // ABlankWindowNameThrows
    let f = Fixture::new();
    let _ = f.store().window(" ");
}

#[test]
#[should_panic(expected = "control name and a kind")]
fn a_blank_control_or_kind_is_a_bug() {
    // ABlankControlOrKindThrows, RemoveRejectsBlankNames
    let f = Fixture::new();
    let _ = f.store().window("nav").get("c", " ");
}

#[test]
fn a_failed_write_keeps_the_value_and_retries_after_the_back_off() {
    // AFailedWriteKeepsTheValueAndDoesNotThrow,
    // AFailedWriteIsNotRetriedUntilTheBackOffPasses
    let f = Fixture::new();
    f.file.0.borrow_mut().blocked = true;
    let mut store = f.store();
    store.window("nav").set("adv", "expanded", "false");
    for _ in 0..100 {
        store.save(false);
    }
    assert_eq!(
        get(&mut store, "nav", "adv", "expanded").as_deref(),
        Some("false")
    );
    assert_eq!(store.take_log().len(), 1, "a failure is reported once");
    f.file.0.borrow_mut().blocked = false;
    store.save(false);
    assert_eq!(f.writes(), 0);
    f.now.set(4999);
    store.save(false);
    assert_eq!(f.writes(), 0);
    f.now.set(5000);
    store.save(false);
    assert_eq!(f.writes(), 1);
}

#[test]
fn ignoring_the_back_off_tries_again_at_once() {
    // IgnoringTheBackOffTriesAgainAtOnce
    let f = Fixture::new();
    f.file.0.borrow_mut().blocked = true;
    let mut store = f.store();
    store.window("nav").set("adv", "expanded", "false");
    store.save(false);
    f.file.0.borrow_mut().blocked = false;
    store.save(false);
    assert_eq!(f.writes(), 0);
    store.save(true);
    assert_eq!(f.writes(), 1);
}

#[test]
fn an_unreadable_file_is_never_overwritten_and_a_later_save_merges() {
    // AnUnreadableFileIsNeverOverwritten, ALaterSaveRetriesAFailedLoad,
    // AFailedLoadIsNotRetriedUntilTheBackOffPasses
    let f = Fixture::new();
    f.file.0.borrow_mut().unreadable = true;
    let mut store = f.store();
    store.window("nav").set("adv", "expanded", "false");
    store.save(false);
    assert_eq!(f.writes(), 0);
    assert_eq!(
        get(&mut store, "nav", "adv", "expanded").as_deref(),
        Some("false")
    );
    {
        let mut disk = f.file.0.borrow_mut();
        disk.unreadable = false;
        disk.characters
            .entry("alice".to_owned())
            .or_default()
            .insert(ControlKey::new("nav", "mobs", "widths"), "1".to_owned());
    }
    f.now.set(1000);
    store.save(false);
    assert_eq!(f.writes(), 0, "not before the back-off");
    f.now.set(5001);
    store.save(false);
    let mut fresh = f.store();
    assert_eq!(
        get(&mut fresh, "nav", "adv", "expanded").as_deref(),
        Some("false")
    );
    assert_eq!(
        get(&mut fresh, "nav", "mobs", "widths").as_deref(),
        Some("1")
    );
}

#[test]
fn a_removal_made_while_the_load_is_failing_survives_the_merge() {
    // ARemovalMadeWhileTheLoadIsFailingSurvivesTheRetryMerge
    let f = Fixture::new();
    f.file.0.borrow_mut().unreadable = true;
    let mut store = f.store();
    store.window("nav").remove("adv", "expanded");
    store.window("nav").set("mobs", "widths", "1");
    store.save(false);
    {
        let mut disk = f.file.0.borrow_mut();
        disk.unreadable = false;
        let alice = disk.characters.entry("alice".to_owned()).or_default();
        alice.insert(
            ControlKey::new("nav", "adv", "expanded"),
            "false".to_owned(),
        );
        alice.insert(ControlKey::new("nav", "other", "widths"), "9".to_owned());
    }
    f.now.set(5001);
    store.save(false);
    let mut fresh = f.store();
    assert_eq!(get(&mut fresh, "nav", "adv", "expanded"), None);
    assert_eq!(
        get(&mut fresh, "nav", "mobs", "widths").as_deref(),
        Some("1")
    );
    assert_eq!(
        get(&mut fresh, "nav", "other", "widths").as_deref(),
        Some("9")
    );
}

#[test]
fn a_character_switch_saves_the_old_character_first() {
    // SaveAfterACharacterChangeStillWritesTheOldCharacter,
    // ACharacterSwitchFlushesChangesHeldBackByAFailedLoad
    let f = Fixture::new();
    let mut store = f.store();
    store.window("nav").set("adv", "expanded", "false");
    f.be("bob");
    assert_eq!(get(&mut store, "nav", "adv", "expanded"), None);
    f.be("alice");
    assert_eq!(
        get(&mut f.store(), "nav", "adv", "expanded").as_deref(),
        Some("false")
    );
}

#[test]
fn remove_forgets_a_value_and_a_missing_one_changes_nothing() {
    // RemoveForgetsAValue, RemovingAMissingValueChangesNothing
    let f = Fixture::new();
    let mut store = f.store();
    store.window("nav").remove("adv", "expanded");
    store.save(false);
    assert_eq!(f.writes(), 0);
    store.window("nav").set("adv", "expanded", "false");
    store.window("nav").set("mobs", "widths", "1");
    store.save(false);
    store.window("nav").remove("adv", "expanded");
    assert_eq!(get(&mut store, "nav", "adv", "expanded"), None);
    store.save(false);
    let mut fresh = f.store();
    assert_eq!(get(&mut fresh, "nav", "adv", "expanded"), None);
    assert_eq!(
        get(&mut fresh, "nav", "mobs", "widths").as_deref(),
        Some("1")
    );
}

#[test]
fn shared_uses_the_reserved_window_key() {
    // MarkupColorSupportTests.Store_SharedUsesTheReservedWindowKey
    let mut store = ControlStateStore::new(None, || "Alice".to_owned(), || 0);
    store.shared().set("color", "recent", "#FF000000");
    assert_eq!(
        get(&mut store, SHARED_WINDOW, "color", "recent").as_deref(),
        Some("#FF000000")
    );
    assert_eq!(get(&mut store, "other", "color", "recent"), None);
}

#[test]
fn a_remembered_value_refreshes_once_per_character() {
    // RememberedValueTests: inactive without a name; refreshes the first
    // time and after a switch; remembers and forgets.
    let mut state = MemoryState {
        generation: 1,
        ..MemoryState::default()
    };
    let mut unnamed = RememberedValue::new(None, "expanded");
    assert!(!unnamed.is_active());
    assert_eq!(unnamed.try_refresh(&mut state), None);
    unnamed.remember(&mut state, Some("x"));
    assert_eq!(state.sets, 0);

    let mut named = RememberedValue::new(Some("adv"), "expanded");
    assert_eq!(named.try_refresh(&mut state), Some(None));
    assert_eq!(named.try_refresh(&mut state), None);
    named.remember(&mut state, Some("false"));
    state.generation += 1;
    assert_eq!(
        named.try_refresh(&mut state),
        Some(Some("false".to_owned()))
    );
    named.remember(&mut state, None);
    assert_eq!(state.get("adv", "expanded"), None);
}

#[test]
fn keyed_names_escape_and_match_in_place() {
    // KeyedControlStateTests: names are stored as name#path, nested paths
    // join outermost first, a key's % and / are escaped.
    assert_eq!(keyed_path(None, "7"), "7");
    assert_eq!(keyed_path(Some("7"), "3"), "7/3");
    assert_eq!(keyed_path(None, "a/b%c"), "a%2Fb%25c");
    assert_eq!(keyed_name("row", "7/3"), "row#7/3");
    let names = BTreeSet::from(["row".to_owned()]);
    let paths = BTreeSet::from(["7".to_owned()]);
    assert!(is_under("row#7", &names, &paths));
    assert!(is_under("row#7/3", &names, &paths));
    assert!(is_under("row#7/3/1", &names, &paths));
    assert!(!is_under("row#70", &names, &paths));
    assert!(!is_under("other#7", &names, &paths));
    assert!(!is_under("row", &names, &paths));
    let slashed = BTreeSet::from([keyed_path(None, "a/b")]);
    assert!(is_under(
        &keyed_name("row", &keyed_path(None, "a/b")),
        &names,
        &slashed
    ));
    assert!(!is_under("row#a/b", &names, &slashed));

    let mut state = MemoryState::default();
    let mut row = KeyedState {
        root: &mut state,
        path: "7/3",
    };
    row.set("adv", "expanded", "true");
    assert_eq!(row.get("adv", "expanded").as_deref(), Some("true"));
    assert_eq!(state.get("adv#7/3", "expanded").as_deref(), Some("true"));
}
