//! The key map against the retail dats: OpenAC
//! `tests/AcDream.Core.Tests/Input/RetailActionMapReaderTests.cs`
//! (`RetailActionMapReader_LiveDatTests`) and
//! `RetailActionIdentityRoundTripTests.cs`: every bindable action of the
//! dat's action map has a retail identity, every character-option row
//! names a distinct option, and the union of the two master maps' default
//! keys is exactly the retail default bindings. These tests need the dats:
//! set `OPENAC_DAT_DIR` or install them under `Documents\Asheron's Call`;
//! run with `cargo test -- --include-ignored`.

use std::collections::{BTreeMap, HashMap, HashSet};
use std::path::PathBuf;

use ac_dat::DatCollection;
use ac_ui::input::action_map::{self, RetailActionClass};
use ac_ui::input::{InputAction, KeyBindings, KeyChord, identity, scan_codes};

fn dats() -> DatCollection {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatCollection::open(dir).expect("open the retail dats")
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_action_map_reads_with_the_pinned_shape() {
    let s = action_map::read(&dats()).unwrap().expect("the action map");
    assert_eq!(s.rows.len(), 306);
    let mut by_class: HashMap<RetailActionClass, usize> = HashMap::new();
    for row in &s.rows {
        assert_ne!(row.class, RetailActionClass::None);
        *by_class.entry(row.class).or_default() += 1;
    }
    assert_eq!(by_class[&RetailActionClass::Movement], 14);
    assert_eq!(by_class[&RetailActionClass::Camera], 22);
    assert_eq!(by_class[&RetailActionClass::Ui], 103);
    assert_eq!(by_class[&RetailActionClass::Combat], 32);
    assert_eq!(by_class[&RetailActionClass::Emote], 87);
    assert_eq!(by_class[&RetailActionClass::CharacterSettings], 48);
    let forward = s
        .rows
        .iter()
        .find(|r| r.input_map == 4 && r.action_id == 0x29)
        .unwrap();
    let scans: HashSet<u32> = forward.default_bindings.iter().map(|c| c.scan).collect();
    assert_eq!(scans, HashSet::from([0x11, 0xC8]));
    assert!(!s.input_maps_conflict(0x1000_0003, 0x1000_0004));
    assert!(!s.input_maps_conflict(0x1000_0003, 0x1000_0005));
    assert!(!s.input_maps_conflict(0x1000_0004, 0x1000_0005));
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_dat_default_keys_are_exactly_the_retail_default_bindings() {
    let s = action_map::read(&dats()).unwrap().expect("the action map");
    assert_eq!(s.rows.len(), 306);
    let unresolved: Vec<String> = s
        .rows
        .iter()
        .filter(|r| identity::resolve(r.input_map, r.action_id).is_none())
        .map(|r| format!("{:#010x}/{:#010x}", r.input_map, r.action_id))
        .collect();
    assert!(unresolved.is_empty(), "{unresolved:?}");
    assert_eq!(identity::all().len(), 306);

    let mut option_ids = HashSet::new();
    for row in s.rows.iter().filter(|r| r.input_map == 0x1000_0008) {
        let action = identity::resolve(row.input_map, row.action_id).unwrap();
        let option = identity::character_option_id(action)
            .unwrap_or_else(|| panic!("{:#010x} has no option id", row.action_id));
        assert!(
            ac_runtime::character::option(option).is_some(),
            "{option:#x}"
        );
        assert!(option_ids.insert(option), "duplicate option {option:#x}");
    }
    assert_eq!(option_ids.len(), 48);

    let defaults = KeyBindings::retail_defaults();
    let mut dat_chords: BTreeMap<InputAction, HashSet<KeyChord>> = BTreeMap::new();
    let mut unresolved_scans = Vec::new();
    for row in &s.rows {
        let action = identity::resolve(row.input_map, row.action_id).unwrap();
        let set = dat_chords.entry(action).or_default();
        for raw in &row.default_bindings {
            let Some(key) = scan_codes::to_key(raw.scan, raw.device) else {
                unresolved_scans.push(format!(
                    "{action}: scan {:#04x} device {}",
                    raw.scan, raw.device
                ));
                continue;
            };
            set.insert(KeyChord {
                key,
                modifiers: scan_codes::to_modifier_mask(raw.modifier),
                device: raw.device as u8,
            });
        }
    }
    assert_eq!(dat_chords.len(), 306);
    assert!(unresolved_scans.is_empty(), "{unresolved_scans:?}");
    let mismatches: Vec<String> = dat_chords
        .iter()
        .filter_map(|(action, dat)| {
            let ours: HashSet<KeyChord> = defaults.for_action(*action).map(|b| b.chord).collect();
            (ours != *dat).then(|| format!("{action}: dat {dat:?} vs ours {ours:?}"))
        })
        .collect();
    assert!(mismatches.is_empty(), "{}", mismatches.join("\n"));
}
