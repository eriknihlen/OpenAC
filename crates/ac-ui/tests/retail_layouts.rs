//! The importer against the retail layouts.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/LayoutImporterInvisibleSweepTests.cs`,
//! `LayoutImporterMediaBearingChildSweepTests.cs`, `TooltipLiveDatTests.cs` and
//! `TooltipSkinLiveDatTests.cs` (the landmark ids and floors are theirs), plus
//! a whole-dat build-and-draw sweep. These tests need the dats: set
//! `OPENAC_DAT_DIR` or install them under `Documents\Asheron's Call`; run with
//! `cargo test -- --include-ignored`.

use std::path::PathBuf;
use std::sync::Arc;

use ac_dat::DatCollection;
use ac_dat::objects::PropertyValue;
use ac_ui::ElementId;
use ac_ui::content::DatContent;
use ac_ui::element_info::ElementInfo;
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::import::{BuildContext, LayoutSource, build, import, import_infos, import_rooted};
use ac_ui::root::UiRoot;
use ac_ui::widgets::Text;

fn content() -> DatContent {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatContent::new(Arc::new(
        DatCollection::open(dir).expect("open the retail dats"),
    ))
    .expect("master property table")
}

fn layout_ids(c: &DatContent) -> Vec<u32> {
    let mut ids: Vec<u32> = c
        .dats()
        .local
        .entries()
        .map(|e| e.unwrap().id)
        .filter(|id| id >> 24 == 0x21)
        .collect();
    ids.sort_unstable();
    ids
}

fn all<'a>(info: &'a ElementInfo, out: &mut Vec<&'a ElementInfo>) {
    out.push(info);
    for c in &info.children {
        all(c, out);
    }
}

fn descendants(info: &ElementInfo) -> Vec<&ElementInfo> {
    let mut v = Vec::new();
    all(info, &mut v);
    v
}

fn widgets(ui: &UiRoot, id: ElementId, out: &mut Vec<ElementId>) {
    out.push(id);
    for c in ui.children(id) {
        widgets(ui, c, out);
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_layout_imports_builds_and_draws() {
    let c = content();
    let font = c.font(DEFAULT_FONT_ID);
    assert!(font.is_some(), "default font");
    let ids = layout_ids(&c);
    assert_eq!(ids.len(), 101);
    let (mut infos, mut built) = (0usize, 0usize);
    for id in &ids {
        let info = import_infos(&c, *id).unwrap_or_else(|| panic!("{id:#010x} imports"));
        infos += descendants(&info).len();
        let mut ui = UiRoot::new();
        ui.set_size(800.0, 600.0);
        let layout = import(&mut ui, &c, *id, font.clone()).unwrap();
        let r = ui.root();
        ui.add_child(r, layout.root);
        let mut w = Vec::new();
        widgets(&ui, layout.root, &mut w);
        built += w.len();
        ui.tick(0.016, 16);
        let list = ui.draw((800.0, 600.0), &c);
        assert_eq!(list.canvas_scale, (1.0, 1.0));
    }
    assert!(c.take_errors().is_empty());
    assert!(built <= infos, "consumed children are not built");
    assert!(built > 2000, "built {built} of {infos} described elements");
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_authored_invisible_widget_starts_hidden() {
    let c = content();
    let (mut authored, mut built, mut visible) = (Vec::new(), Vec::new(), Vec::new());
    for id in layout_ids(&c) {
        let Some(tree) = import_infos(&c, id) else {
            continue;
        };
        authored.extend(
            descendants(&tree)
                .iter()
                .filter(|e| e.invisible)
                .map(|e| e.id),
        );
        let mut ui = UiRoot::new();
        let layout = import(&mut ui, &c, id, None).unwrap();
        let mut w = Vec::new();
        widgets(&ui, layout.root, &mut w);
        for e in w {
            let el = ui.element(e).unwrap();
            if el.authored.invisible {
                built.push(el.dat_element_id);
                if el.visible() {
                    visible.push(el.dat_element_id);
                }
            }
        }
    }
    assert!(authored.len() >= 1000, "found {}", authored.len());
    for id in [0x1000_0403, 0x1000_0494, 0x1000_06A4, 0x1000_048C] {
        assert!(authored.contains(&id), "{id:#x}");
    }
    assert!(built.contains(&0x1000_06A4) && built.contains(&0x1000_048C));
    assert!(visible.is_empty(), "{visible:x?}");
}

#[test]
#[ignore = "needs the retail dat files"]
fn media_bearing_children_of_text_are_found_and_built() {
    let c = content();
    let mut findings = Vec::new();
    let mut invisible_children = Vec::new();
    for id in layout_ids(&c) {
        let Some(tree) = import_infos(&c, id) else {
            continue;
        };
        for e in descendants(&tree) {
            if e.kind != 12 || e.states.iter().any(|(_, s)| s.pass_to_children) {
                continue;
            }
            let media: Vec<u32> = e
                .children
                .iter()
                .filter(|c| !c.state_media.is_empty())
                .map(|c| c.id)
                .collect();
            if !media.is_empty() {
                findings.push((id, e.id));
            }
            invisible_children.extend(
                e.children
                    .iter()
                    .filter(|c| !c.state_media.is_empty() && c.invisible)
                    .map(|c| c.id),
            );
        }
    }
    assert!(findings.contains(&(0x2100_0005, 0x1000_059A)));
    assert!(findings.contains(&(0x2100_006F, 0x1000_0011)));
    for id in [0x1000_03E0, 0x1000_0409, 0x1000_0404] {
        assert!(findings.iter().any(|f| f.1 == id), "{id:#x}");
    }
    let gold_frame = [
        0x1000_02DE,
        0x1000_02DF,
        0x1000_02E0,
        0x1000_02E1,
        0x1000_00E8,
        0x1000_02E2,
        0x1000_02E3,
        0x1000_00EA,
    ];
    assert!(gold_frame.iter().all(|p| !invisible_children.contains(p)));

    for (layout, element, children, hidden) in [
        (0x2100_0005, 0x1000_059A, gold_frame.to_vec(), vec![]),
        (
            0x2100_006F,
            0x1000_0011,
            vec![0x1000_048C],
            vec![0x1000_048C],
        ),
    ] {
        // The element may come from a base layout, so it is found in the
        // resolved tree of the whole layout and built from there.
        let tree = import_infos(&c, layout).unwrap();
        let info = descendants(&tree)
            .into_iter()
            .find(|e| e.id == element)
            .unwrap_or_else(|| panic!("{element:#x}"));
        let mut ui = UiRoot::new();
        let ctx = BuildContext {
            source: &c,
            default_font: None,
            source_layout_did: 0,
            media_seconds: 0.0,
        };
        let built = build(&mut ui, info, &ctx);
        assert!(ui.widget::<Text>(built.root).is_some());
        for child in children {
            let id = ui
                .find_descendant(built.root, child)
                .unwrap_or_else(|| panic!("{child:#x}"));
            assert_eq!(
                ui.element(id).unwrap().visible(),
                !hidden.contains(&child),
                "{child:#x}"
            );
        }
    }
}

const TOOLTIP_CATALOG: u32 = 0x2100_0041;
const TOOLTIP_TEXT_CHILD: u32 = 0x1000_0396;

#[test]
#[ignore = "needs the retail dat files"]
fn every_popup_skin_shares_the_text_child_and_its_wrap_bound() {
    let c = content();
    let root = import_infos(&c, TOOLTIP_CATALOG).unwrap();
    assert_eq!((root.id, root.width, root.height), (0, 800.0, 600.0));
    for skin_id in [0x1000_0487, 0x1000_0395, 0x1000_0397, 0x1000_0398] {
        let skin = root.children.iter().find(|s| s.id == skin_id).unwrap();
        assert_eq!(skin.tooltip_text_child_element_id, TOOLTIP_TEXT_CHILD);
        let text: Vec<_> = descendants(skin)
            .into_iter()
            .filter(|e| e.id == TOOLTIP_TEXT_CHILD)
            .collect();
        assert_eq!(text.len(), 1);
        let t = text[0];
        assert_eq!(t.kind, 12);
        assert_eq!(t.max_width, Some(256));
        assert_eq!((t.margin_left, t.margin_right), (2, 2));
        let vertical = if skin_id == 0x1000_0398 { 0 } else { 2 };
        assert_eq!((t.margin_top, t.margin_bottom), (vertical, vertical));
    }

    let mut ui = UiRoot::new();
    let popup = import_rooted(&mut ui, &c, TOOLTIP_CATALOG, 0x1000_0487, None).unwrap();
    let e = ui.element(popup.root).unwrap();
    assert_eq!((e.width, e.height), (30.0, 30.0));
    assert_eq!(e.authored.tooltip_text_child_element_id, TOOLTIP_TEXT_CHILD);
    assert!(
        ui.widget::<Text>(popup.find(TOOLTIP_TEXT_CHILD).unwrap())
            .is_some()
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_known_element_authors_all_five_tooltip_properties() {
    let c = content();
    let tree = import_infos(&c, 0x2100_0005).unwrap();
    let rotate = descendants(&tree)
        .into_iter()
        .find(|e| e.id == 0x1000_05A4)
        .unwrap();
    assert!(rotate.tooltip_enabled);
    assert_eq!(rotate.tooltip_root_element_id, 0x1000_0487);
    assert_eq!(rotate.tooltip_layout_did, TOOLTIP_CATALOG);
    let text = rotate.tooltip_text.unwrap();
    assert_eq!(c.string(&text).as_deref(), Some("Rotate left."));
}

#[test]
#[ignore = "needs the retail dat files"]
fn tooltip_properties_are_widespread() {
    let c = content();
    let (mut any, mut with_text, mut showable) = (0, 0, 0);
    let mut landmark = false;
    let mut smart_box_wrappers = 0;
    for id in layout_ids(&c) {
        let Some(tree) = import_infos(&c, id) else {
            continue;
        };
        for e in descendants(&tree) {
            smart_box_wrappers += usize::from(e.kind == 0x1000_0030);
            if e.tooltip_root_element_id != 0
                || e.tooltip_layout_did != 0
                || e.tooltip_text.is_some()
                || e.tooltip_enabled
                || e.tooltip_delay_seconds.is_some()
            {
                any += 1;
                with_text += usize::from(e.tooltip_text.is_some());
                showable += usize::from(
                    e.tooltip_enabled
                        && e.tooltip_text.is_some()
                        && e.tooltip_layout_did != 0
                        && e.tooltip_root_element_id != 0,
                );
                landmark |= id == 0x2100_0005 && e.id == 0x1000_05A4;
            }
        }
    }
    assert!(landmark);
    assert!(
        any >= 400 && with_text >= 200 && showable >= 200,
        "{any} {with_text} {showable}"
    );
    assert_eq!(smart_box_wrappers, 0);
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_options_toggle_row_authors_the_popup_locator_without_text() {
    let c = content();
    let mut ui = UiRoot::new();
    let row = import_rooted(&mut ui, &c, 0x2100_002B, 0x1000_0218, None).unwrap();
    let checkbox = ui.find_descendant(row.root, 0x1000_0219).unwrap();
    let e = ui.element(checkbox).unwrap();
    assert_eq!(e.authored.tooltip_root_element_id, 0x1000_0397);
    assert_eq!(e.authored.tooltip_layout_did, TOOLTIP_CATALOG);
    assert!(e.authored.tooltip_enabled);
    assert!(e.authored.tooltip_text.as_deref().is_none_or(str::is_empty));
    assert!(!e.click_through);
    assert_eq!(e.source_layout_did, 0x2100_002B);
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_item_prototype_shares_the_popup_locator() {
    let c = content();
    let catalog = import_infos(&c, 0x2100_0037).unwrap();
    assert!(catalog.children.len() >= 40);
    let prototypes: Vec<_> = catalog
        .children
        .iter()
        .filter(|p| p.kind == 0x1000_0032)
        .collect();
    assert!(prototypes.len() >= 30);
    for p in prototypes {
        assert_eq!(p.tooltip_root_element_id, 0x1000_0395, "{:#x}", p.id);
        assert_eq!(p.tooltip_layout_did, TOOLTIP_CATALOG);
        assert!(p.tooltip_text.is_none());
    }
    let proto = |layout: u32, element: u32| -> u32 {
        let tree = import_infos(&c, layout).unwrap();
        let e = descendants(&tree)
            .into_iter()
            .find(|x| x.id == element)
            .unwrap()
            .clone();
        match e.effective_property(0x1000_000E, None) {
            Some(PropertyValue::Enum(v) | PropertyValue::DataId(v)) => *v,
            other => panic!("{other:?}"),
        }
    };
    let inventory = proto(0x2100_0023, 0x1000_01C6);
    let toolbar = proto(0x2100_0016, 0x1000_01A7);
    assert!(inventory != 0 && toolbar != 0 && inventory != toolbar);
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_chat_window_imports_with_its_authored_text() {
    let c = content();
    let font = c.font(DEFAULT_FONT_ID);
    let mut ui = UiRoot::new();
    ui.set_size(800.0, 600.0);
    let chat = import(&mut ui, &c, 0x2100_0006, font).unwrap();
    let root = ui.element(chat.root).unwrap();
    assert_eq!((root.width, root.height), (800.0, 600.0));
    let main = chat.find(0x1000_000E).unwrap();
    let e = ui.element(main).unwrap();
    assert_eq!((e.width, e.height, e.min_height), (800.0, 100.0, 40.0));
    assert!(
        !e.visible(),
        "the chat frame authors 0x3B (hidden until shown)"
    );
    assert_eq!(e.authored.resize_max_height, Some(360));
    // A top element: inheritance from the shared button prototype layout.
    let caption = chat.find(0x1000_001E).unwrap();
    assert_eq!(ui.element(caption).unwrap().width, 191.0);
}
