//! The spellbook's components page.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/SpellbookWindowControllerTests.cs`
//! (`Components_UseAuthoredCategoryAndItemTemplates_WithRetailCountsAndSelection`),
//! which binds the retail spellbook layout: here against the dats
//! themselves. OpenAC's `setDesiredComponent` and `selectObject` are
//! `GameplayCommand::SetDesiredComponent` and `PanelOutput::SelectObject`.

mod common;

use std::collections::{BTreeMap, HashMap};
use std::sync::Arc;

use ac_core::items::{ClientObject, ItemType};
use ac_runtime::gameplay::GameplayCommand;
use ac_runtime::session::SessionCommand;
use ac_ui::host::UiHost;
use ac_ui::panels::PanelOutput;
use ac_ui::panels::spell_components::{
    self as comps, ComponentDescriptor, ComponentsView, owned_components,
};
use common::items::*;

fn pack(o: &mut Objects, guid: u32, wcid: u32, name: &str, icon: u32, stack: i32) {
    let mut c = ClientObject::new(guid);
    c.weenie_class_id = wcid;
    c.name = name.into();
    c.item_type = ItemType::SPELL_COMPONENTS;
    c.icon_id = icon;
    c.stack_size = stack;
    c.is_component_pack = true;
    o.add(c);
    o.mv(guid, PLAYER, 0);
}

fn descriptors() -> HashMap<u32, ComponentDescriptor> {
    let d = |name: &str, category, icon| ComponentDescriptor {
        name: name.into(),
        category,
        icon,
    };
    HashMap::from([
        (100, d("Copper Scarab", 0, 0x0600_0100)),
        (101, d("Diamond Scarab", 0, 0x0600_0101)),
        (200, d("Amaranth", 1, 0x0600_0200)),
    ])
}

fn objects() -> Objects {
    let mut o = Objects::new();
    o.player(102, 7);
    pack(&mut o, 10, 100, "Copper Scarab", 0x0600_0100, 2);
    pack(&mut o, 11, 101, "Diamond Scarab", 0x0600_0101, 1);
    pack(&mut o, 12, 200, "Amaranth", 0x0600_0200, 3);
    o
}

#[test]
fn the_carried_components_group_by_category_then_name() {
    let o = objects();
    let d = descriptors();
    let desired = BTreeMap::new();
    let view = ComponentsView {
        objects: &o.t,
        player: PLAYER,
        desired: &desired,
        components: &d,
        selected: None,
    };
    let owned = owned_components(&view);
    let names: Vec<(&str, u32, i32)> = owned
        .iter()
        .map(|c| (c.name.as_str(), c.category, c.quantity))
        .collect();
    assert_eq!(
        names,
        [
            ("Copper Scarab", 0, 2),
            ("Diamond Scarab", 0, 1),
            ("Amaranth", 1, 3)
        ]
    );
}

/// The list made in a plain host takes its margins when it is made, so a
/// spellbook that grew while hidden shows the components at its full
/// height. OpenAC `SpellbookWindowController.Create` captures the
/// baseline right after adding the components list.
#[test]
fn a_list_made_in_its_host_follows_a_page_that_grew_while_hidden() {
    use ac_ui::import::ImportedLayout;
    use ac_ui::tree::Element;
    use ac_ui::widgets::Panel;
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let window = ui.add(r, Element::at(0.0, 0.0, 300.0, 100.0), Panel::default());
    let list_host = ui.add(window, Element::at(0.0, 0.0, 200.0, 64.0), Panel::default());
    let layout =
        ImportedLayout::from_parts(window, [(comps::LIST_ID, list_host)].into_iter().collect());
    ui.element_mut(window).unwrap().visible = false;
    let page = comps::ComponentsPageController::bind(ui, &layout, Vec::new()).unwrap();
    let list = page.list();
    assert_ne!(list, list_host, "the list is made in the host");
    ui.element_mut(list_host).unwrap().height = 300.0;
    ui.element_mut(window).unwrap().visible = true;
    host.draw((800.0, 600.0), &ac_ui::draw::NoSprites);
    assert_eq!(host.root.element(list).unwrap().height, 300.0);
}

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import};
    use ac_ui::panels::DatRowTemplates;
    use ac_ui::panels::magic;
    use ac_ui::widgets::{CatalogSlot, Field, ItemList, Scrollbar, Text};
    use std::path::PathBuf;

    fn content() -> DatContent {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        DatContent::new(Arc::new(
            ac_dat::DatCollection::open(dir).expect("open the retail dats"),
        ))
        .expect("master property table")
    }

    /// A header row is a text itself.
    fn own_text(host: &UiHost, row: ac_ui::ElementId) -> String {
        host.root.widget::<Text>(row).unwrap().content_lines()[0]
            .text
            .clone()
    }

    fn text(host: &UiHost, row: ac_ui::ElementId, id: u32) -> String {
        let t = host.root.find_descendant(row, id).unwrap();
        host.root.widget::<Text>(t).unwrap().content_lines()[0]
            .text
            .clone()
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_page_lists_counts_desired_amounts_and_selection_from_the_templates() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import(
            &mut host.root,
            &c,
            magic::SPELLBOOK_LAYOUT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("spellbook layout");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        let names = comps::CATEGORY_STRINGS
            .iter()
            .map(|(_, n)| (*n).to_owned())
            .collect();
        let mut page =
            comps::ComponentsPageController::bind(&mut host.root, &layout, names).expect("binds");
        let list = page.list();
        assert!(host.root.widget::<ItemList>(list).is_some());
        let bar = layout.find(comps::SCROLLBAR_ID).unwrap();
        assert_eq!(
            host.root.widget::<Scrollbar>(bar).unwrap().model,
            ac_ui::widgets::ScrollSource::Element(list)
        );
        let mut o = objects();
        let d = descriptors();
        let mut desired = BTreeMap::from([(100, 7)]);
        let mut rows = DatRowTemplates::new(&c, c.font(DEFAULT_FONT_ID));
        let mut icons = TestIcons::default();
        let mut show = |host: &mut UiHost,
                        page: &mut comps::ComponentsPageController,
                        o: &Objects,
                        desired: &BTreeMap<u32, u32>,
                        selected: Option<u32>| {
            let view = ComponentsView {
                objects: &o.t,
                player: PLAYER,
                desired,
                components: &d,
                selected,
            };
            page.update(&mut host.root, &view, &mut rows, &mut icons);
        };
        show(&mut host, &mut page, &o, &desired, None);
        let rows_now = page.rows();
        assert_eq!(rows_now.len(), 5, "two headers and three components");
        assert_eq!(own_text(&host, rows_now[0].0), "SCARABS");
        let copper = rows_now[1].0;
        assert_eq!(rows_now[1].1, Some(100));
        assert_eq!(text(&host, copper, comps::NAME_ID), "Copper Scarab");
        assert_eq!(text(&host, copper, comps::OWNED_COUNT_ID), "2");
        let field = host
            .root
            .find_descendant(copper, comps::DESIRED_COUNT_ID)
            .unwrap();
        assert_eq!(host.root.widget::<Field>(field).unwrap().text(), "7");
        let icon_host = host.root.find_descendant(copper, comps::ICON_ID).unwrap();
        let icon = host.root.children(icon_host)[0];
        assert_eq!(
            host.root.widget::<CatalogSlot>(icon).unwrap().icon,
            Some(ac_ui::draw::TextureRef::Runtime(0x0600_0100))
        );
        // A click selects the component's pack.
        host.take_outputs();
        page.on_message(
            &mut host.root,
            copper,
            &ac_ui::root::ElementMessage::Clicked { x: 1, y: 1 },
        );
        host.pump();
        let out = panel_outputs(&mut host);
        assert!(
            matches!(out.as_slice(), [PanelOutput::SelectObject(10)]),
            "{out:?}"
        );
        assert_eq!(page.selected(), Some(100));
        assert_eq!(
            host.root.retail_state(copper),
            Some(ac_ui::states::HIGHLIGHT)
        );
        // A typed amount goes out; one past 5000 is put back.
        host.root.widget_mut::<Field>(field).unwrap().set_text("21");
        page.on_message(
            &mut host.root,
            field,
            &ac_ui::root::ElementMessage::Submitted("21".into()),
        );
        host.root
            .widget_mut::<Field>(field)
            .unwrap()
            .set_text("5001");
        page.on_message(
            &mut host.root,
            field,
            &ac_ui::root::ElementMessage::Submitted("5001".into()),
        );
        host.pump();
        let sent: Vec<_> = sessions(&panel_outputs(&mut host));
        assert_eq!(sent.len(), 1, "{sent:?}");
        assert!(matches!(
            sent[0],
            SessionCommand::Gameplay(GameplayCommand::SetDesiredComponent {
                component: 100,
                amount: 21
            })
        ));
        assert_eq!(host.root.widget::<Field>(field).unwrap().text(), "21");
        assert_eq!(own_text(&host, page.rows()[3].0), "HERBS");
        // The world's selection picks the row.
        desired.insert(100, 21);
        show(&mut host, &mut page, &o, &desired, Some(12));
        assert_eq!(page.selected(), Some(200));
        show(&mut host, &mut page, &o, &desired, Some(0xDEAD_BEEF));
        assert_eq!(page.selected(), None);
        // A stack change rebuilds the counts, keeping the desired amount.
        o.get_mut(10).stack_size = 1;
        show(&mut host, &mut page, &o, &desired, None);
        let copper = page.rows()[1].0;
        assert_eq!(text(&host, copper, comps::OWNED_COUNT_ID), "1");
        let field = host
            .root
            .find_descendant(copper, comps::DESIRED_COUNT_ID)
            .unwrap();
        assert_eq!(host.root.widget::<Field>(field).unwrap().text(), "21");
        assert!(c.take_errors().is_empty());
    }
}
