//! The item hover caption: the composed name (material prefix) with the
//! stack count in front.
//! Ported from OpenAC `src/AcDream.App/UI/Layout/ItemTooltipCaption.cs` and
//! its test helper `tests/AcDream.App.Tests/UI/ItemTooltipCaptionNames.cs`
//! (one-entry material table: Pyreal, Silver, and an unnamed material), as
//! the trade and vendor tooltip tests use it.

use ac_core::items::{ClientObject, ClientObjectTable};
use ac_ui::panels::appraisal::{AppraisalNames, NameTables};
use ac_ui::panels::items::{ItemsView, NameResolver};

const PYREAL: u32 = 1;
const SILVER: u32 = 60;
const UNNAMED: u32 = 61;

fn names() -> NameTables {
    NameTables {
        materials: [(PYREAL, "Pyreal".to_owned()), (SILVER, "Silver".to_owned())]
            .into_iter()
            .collect(),
        ..NameTables::default()
    }
}

fn material(guid: u32, material: u32, stack: i32) -> ClientObject {
    let mut o = ClientObject::new(guid);
    o.name = "Scarab".into();
    o.plural_name = "Scarabs".into();
    o.material_type = Some(material);
    o.stack_size = stack;
    o
}

fn caption(table: &ClientObjectTable, guid: u32) -> Option<String> {
    let tables = names();
    let resolve = |o: &ClientObject| tables.appropriate_name(o);
    let resolver: NameResolver<'_> = &resolve;
    ItemsView {
        names: Some(resolver),
        ..ItemsView::new(table, 0x5000_0001)
    }
    .tooltip(guid)
}

#[test]
fn the_caption_is_the_composed_name_with_the_stack_count() {
    let mut table = ClientObjectTable::new();
    let mut ev = Vec::new();
    table.add_or_update(material(1, PYREAL, 1), &mut ev);
    table.add_or_update(material(2, PYREAL, 3), &mut ev);
    table.add_or_update(material(3, UNNAMED, 1), &mut ev);
    let mut bread = ClientObject::new(4);
    bread.name = "Bread Loaf".into();
    table.add_or_update(bread, &mut ev);
    table.add_or_update(material(5, SILVER, 1), &mut ev);
    assert_eq!(caption(&table, 1).as_deref(), Some("Pyreal Scarab"));
    assert_eq!(caption(&table, 2).as_deref(), Some("3 Pyreal Scarabs"));
    assert_eq!(
        caption(&table, 3).as_deref(),
        Some("Scarab"),
        "a material with no name"
    );
    assert_eq!(caption(&table, 4).as_deref(), Some("Bread Loaf"));
    assert_eq!(caption(&table, 5).as_deref(), Some("Silver Scarab"));
    assert_eq!(caption(&table, 99), None, "an object not tracked");
}

#[test]
fn a_name_already_holding_its_material_is_not_prefixed_twice() {
    let n = names();
    let mut o = material(1, SILVER, 1);
    o.name = "Silver Chainmail Basinet".into();
    assert_eq!(n.appropriate_name(&o), "Silver Chainmail Basinet");
    o.name = "Chainmail Basinet".into();
    assert_eq!(n.appropriate_name(&o), "Silver Chainmail Basinet");
}

mod real_dats {
    use ac_ui::panels::appraisal::load_material_names;
    use std::path::PathBuf;

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_material_names_come_from_the_dual_enum_map() {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        let dats = ac_dat::DatCollection::open(dir).unwrap();
        let names = load_material_names(&dats);
        assert_eq!(names.get(&62).map(String::as_str), Some("Pyreal"));
        assert_eq!(names.get(&63).map(String::as_str), Some("Silver"));
        assert!(names.values().all(|n| !n.contains('_')));
    }
}
