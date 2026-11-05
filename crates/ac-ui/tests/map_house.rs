//! The map and house panel.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/MapPageControllerTests.cs`,
//! `MapHousePanelControllerTests.cs`, `MapHousePanelLiveDatMountTests.cs`
//! and `MapNoteLiveDatTests.cs`. OpenAC binds its fixture tests to a
//! snapshot of the retail layout; here those run against the dats
//! themselves (`real_dats`).

mod common;

use std::collections::HashSet;

use ac_core::world::{CalendarDate, HourName, MonthName};
use ac_runtime::gameplay::HouseLineColor;
use ac_ui::ElementId;
use ac_ui::draw::Color;
use ac_ui::element_info::{ElementInfo, TemplateEntry};
use ac_ui::panels::map_house::{
    self, HousePageController, MAP_LOCATIONS, MapFacts, format_date_time, marker_position,
};
use ac_ui::radar::RadarCoordinates;
use ac_ui::root::UiRoot;
use ac_ui::tree::Element;
use ac_ui::widgets::{ListBox, Text};
use common::panels::text_of;
use common::*;

fn date(year: i32, month: MonthName, day: i32, hour: HourName) -> CalendarDate {
    CalendarDate {
        year,
        month,
        day,
        hour,
    }
}

#[test]
fn the_date_reads_month_day_year_and_the_hour() {
    assert_eq!(
        format_date_time(&date(119, MonthName::Frostfell, 27, HourName::Dawnsong)),
        "Date: Frostfell 27, 119 P.Y.\nTime: Dawnsong"
    );
    assert_eq!(
        format_date_time(&date(
            10,
            MonthName::Morningthaw,
            1,
            HourName::MorntideAndHalf
        )),
        "Date: Morningthaw 1, 10 P.Y.\nTime: Morntide-and-Half"
    );
    for (hour, text) in [
        (HourName::Darktide, "Darktide"),
        (HourName::DarktideAndHalf, "Darktide-and-Half"),
        (HourName::Gloaming, "Gloaming"),
        (HourName::GloamingAndHalf, "Gloaming-and-Half"),
        (HourName::WarmtideAndHalf, "Warmtide-and-Half"),
    ] {
        let t = format_date_time(&date(10, MonthName::Morningthaw, 1, hour));
        assert!(t.ends_with(&format!("Time: {text}")), "{t}");
    }
}

#[test]
fn the_town_table_has_53_unique_towns_within_the_map() {
    assert_eq!(MAP_LOCATIONS.len(), 53);
    let mut names = HashSet::new();
    for town in &MAP_LOCATIONS {
        assert!(names.insert(town.name), "{} twice", town.name);
        assert!((0..=260).contains(&town.x) && (0..=260).contains(&town.y));
        assert!((1..=20).contains(&town.width) && (1..=20).contains(&town.height));
    }
    let holtburg = MAP_LOCATIONS.iter().find(|t| t.name == "Holtburg").unwrap();
    assert_eq!(
        (holtburg.x, holtburg.y, holtburg.width, holtburg.height),
        (0xa4, 0x4d, 9, 8)
    );
}

#[test]
fn a_marker_lands_where_the_game_puts_it() {
    for (x, y, left, top) in [
        (0.0, 0.0, 122.0, 128.0),
        (-100.0, 0.0, 3.0, 128.0),
        (0.0, 100.0, 122.0, 5.0),
        (-88.300_000_000_000_01, 62.900_000_000_000_006, 17.0, 51.0),
    ] {
        assert_eq!(
            marker_position(6, 247, 8, 258, 10, 10, x, y),
            (left, top),
            "{x} {y}"
        );
    }
    let arwic = RadarCoordinates::from_cell(0x11CE_0001).unwrap();
    assert!((arwic.x - -88.300_000_000_000_01).abs() < 1e-12);
    assert!((arwic.y - 62.900_000_000_000_006).abs() < 1e-12);
}

/// A house list with one text-row template whose palette is white, green,
/// red.
fn house_page(ui: &mut UiRoot) -> (ElementId, HousePageController) {
    let r = ui.root();
    let page = panel(ui, r, at(0.0, 0.0, 300.0, 300.0));
    let info = ElementInfo {
        id: map_house::HOUSE_TEXT_BOX_ID,
        kind: 7,
        ..ElementInfo::default()
    };
    let mut e = Element::at(0.0, 0.0, 280.0, 280.0);
    e.dat_element_id = map_house::HOUSE_TEXT_BOX_ID;
    let mut list = ListBox::new(std::sync::Arc::new(info));
    list.templates = vec![TemplateEntry {
        layout: 1,
        element: 2,
    }];
    let l = ui.add(page, e, list);
    let c = HousePageController::bind(ui, page).expect("binds");
    (l, c)
}

fn row_template(ui: &mut UiRoot, _layout: u32, _element: u32) -> Option<ElementId> {
    let mut e = Element::at(0.0, 0.0, 280.0, 28.0);
    Text::configure(&mut e);
    let mut t = Text::default();
    t.default_color = Color::rgba(0.1, 0.1, 0.1, 1.0);
    t.font_color_palette = vec![
        Color::rgba(1.0, 1.0, 1.0, 1.0),
        Color::rgba(0.0, 1.0, 0.0, 1.0),
        Color::rgba(1.0, 0.0, 0.0, 1.0),
    ];
    Some(ui.insert(e, t))
}

fn rows_of(ui: &UiRoot, list: ElementId) -> Vec<(String, Color)> {
    let mut out = Vec::new();
    let mut stack = vec![list];
    while let Some(id) = stack.pop() {
        if id != list
            && let Some(t) = ui.widget::<Text>(id)
        {
            for l in t.content_lines() {
                out.push((l.text.clone(), l.color));
            }
        }
        let mut children = ui.children(id);
        children.reverse();
        stack.extend(children);
    }
    out
}

#[test]
fn the_house_list_starts_empty_and_shows_its_lines_in_their_palette_colours() {
    let mut ui = root(800.0, 600.0);
    let (list, mut house) = house_page(&mut ui);
    assert!(rows_of(&ui, list).is_empty());
    let mut templates = row_template;
    house.update(
        &mut ui,
        &[(
            "You may buy another house immediately.".into(),
            HouseLineColor::Normal,
        )],
        &mut templates,
    );
    assert_eq!(
        rows_of(&ui, list),
        [(
            "You may buy another house immediately.".to_owned(),
            Color::rgba(1.0, 1.0, 1.0, 1.0)
        )]
    );
    house.update(
        &mut ui,
        &[
            ("paid".into(), HouseLineColor::RentPaid),
            ("unpaid".into(), HouseLineColor::RentNotPaid),
        ],
        &mut templates,
    );
    assert_eq!(
        rows_of(&ui, list),
        [
            ("paid".to_owned(), Color::rgba(0.0, 1.0, 0.0, 1.0)),
            ("unpaid".to_owned(), Color::rgba(1.0, 0.0, 0.0, 1.0)),
        ]
    );
}

// ── Real dats ───────────────────────────────────────────────────────────

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::host::UiHost;
    use ac_ui::import::{BuildContext, LayoutSource, build, import_infos_rooted};
    use ac_ui::panels::DatRowTemplates;
    use ac_ui::panels::map_house::MapHousePanelController;
    use ac_ui::panels::{PanelOutput, panel_id};
    use ac_ui::widgets::{Button, TabPanel};
    use std::path::PathBuf;
    use std::sync::Arc;

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

    struct Panel {
        host: UiHost,
        c: DatContent,
        root: ElementId,
    }

    fn mounted() -> Panel {
        let c = content();
        let info = import_infos_rooted(&c, map_house::HOST_LAYOUT_ID, map_house::SLOT_ELEMENT_ID)
            .expect("slot 0x1000018C");
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let ctx = BuildContext {
            source: &c,
            default_font: c.font(DEFAULT_FONT_ID),
            source_layout_did: map_house::HOST_LAYOUT_ID,
            media_seconds: 0.0,
        };
        let layout = build(&mut host.root, &info, &ctx);
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        host.root
            .register_window("map", layout.root, None, 0)
            .unwrap();
        let mut rows = DatRowTemplates::new(&c, c.font(DEFAULT_FONT_ID));
        let mut ctl = MapHousePanelController::bind(&mut host.root, &info, &layout, &mut rows)
            .expect("a tab panel");
        ctl.activate_tabs(&mut host.root);
        let root = layout.root;
        host.attach_controller("map", Box::new(ctl)).unwrap();
        Panel { host, c, root }
    }

    impl Panel {
        fn with<R>(&mut self, f: impl FnOnce(&mut MapHousePanelController, &mut UiRoot) -> R) -> R {
            self.host
                .with_controller::<MapHousePanelController, _>("map", f)
                .unwrap()
        }

        fn find(&self, id: u32) -> ElementId {
            self.host.root.find_descendant(self.root, id).unwrap()
        }
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_slot_is_a_tab_panel_with_the_map_by_default() {
        let mut p = mounted();
        let tabs = p.host.root.widget::<TabPanel>(p.root).unwrap();
        assert_eq!(tabs.tabs().len(), 2);
        assert!(
            tabs.tabs()
                .iter()
                .any(|t| t.button == map_house::MAP_BUTTON_ID
                    && t.page == map_house::MAP_PAGE_ID
                    && t.is_default)
        );
        assert!(
            tabs.tabs()
                .iter()
                .any(|t| t.button == map_house::HOUSE_BUTTON_ID
                    && t.page == map_house::HOUSE_PAGE_ID
                    && !t.is_default)
        );
        assert!(tabs.unresolved().is_empty());
        assert!(p.with(|c, ui| c.is_showing_map(ui)));
        p.with(|c, ui| c.show_house(ui));
        assert!(p.with(|c, ui| c.is_showing_house(ui)));
        p.with(|c, ui| c.show_map(ui));
        assert!(p.with(|c, ui| c.is_showing_map(ui) && !c.is_showing_house(ui)));
        // The close button closes the panel.
        let close = p.find(map_house::CLOSE_BUTTON_ID);
        assert!(p.host.root.widget::<Button>(close).is_some());
        p.host.take_outputs();
        p.host
            .root
            .fire_event(ac_ui::event::kind::CLICK, close, None);
        p.host.pump();
        let out = p.host.take_outputs();
        assert!(
            out.iter().any(|o| matches!(
                o,
                ac_ui::root::UiOutput::Panel(PanelOutput::ClosePanel(panel_id::MAP_HOUSE))
            )),
            "{out:?}"
        );
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_map_carries_53_named_town_hotspots_and_both_markers() {
        let mut p = mounted();
        let map = p.find(map_house::MAP_WIDGET_ID);
        let children = p.host.root.children(map);
        let towns: Vec<ElementId> = p.with(|c, _| c.map().unwrap().markers().to_vec());
        assert_eq!(towns.len(), 53);
        assert_eq!(children.len(), 55, "53 towns and the two markers");
        let ui = &p.host.root;
        let mut names = Vec::new();
        for t in &towns {
            let e = ui.element(*t).unwrap();
            assert!(
                MAP_LOCATIONS
                    .iter()
                    .any(|l| l.width as f32 == e.width && l.height as f32 == e.height)
            );
            let b = ui.widget::<Button>(*t).expect("a hotspot is a button");
            names.push(b.tooltip_text.clone().unwrap());
        }
        assert!(names.iter().any(|n| n == "Holtburg"));
        // The rollover highlight: hidden, shown while hovered.
        let first = towns[0];
        let highlight = ui.find_descendant(first, 0x1000_01F1).expect("highlight");
        assert!(!ui.element(highlight).unwrap().visible());
        p.host
            .root
            .fire_event(ac_ui::event::kind::HOVER_ENTER, first, None);
        assert!(p.host.root.element(highlight).unwrap().visible());
        p.host
            .root
            .fire_event(ac_ui::event::kind::HOVER_LEAVE, first, None);
        assert!(!p.host.root.element(highlight).unwrap().visible());
        let ui = &p.host.root;
        for icon in [map_house::PLAYER_ICON_ID, map_house::HOUSE_ICON_ID] {
            let e = p.find(icon);
            assert_eq!(ui.parent(e), Some(map));
            let el = ui.element(e).unwrap();
            assert!(el.width > 0.0 && el.height > 0.0);
            assert!(!el.visible());
        }
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_players_marker_follows_his_cell_and_hides_indoors() {
        let mut p = mounted();
        let icon = p.find(map_house::PLAYER_ICON_ID);
        let (w, h) = {
            let e = p.host.root.element(icon).unwrap();
            (e.width as i32, e.height as i32)
        };
        let mut lines = Vec::new();
        let mut rows = DatRowTemplates::new(&p.c, None);
        let facts = MapFacts {
            calendar: Some(date(119, MonthName::Frostfell, 27, HourName::Dawnsong)),
            player_cell: 0x11CE_0001,
            house_cell: None,
        };
        let (host, root) = (&mut p.host, p.root);
        host.with_controller::<MapHousePanelController, _>("map", |c, ui| {
            c.update(ui, &facts, 0.016, &lines, &mut rows);
        });
        let e = host.root.element(icon).unwrap();
        assert!(e.visible());
        let expected = marker_position(
            6,
            247,
            8,
            258,
            w,
            h,
            -88.300_000_000_000_01,
            62.900_000_000_000_006,
        );
        assert_eq!((e.left, e.top), expected);
        let coordinates = host
            .root
            .find_descendant(root, map_house::COORDINATE_TEXT_ID)
            .unwrap();
        assert_eq!(text_of(&host.root, coordinates), "62.9N,88.3W");
        let date_text = host
            .root
            .find_descendant(root, map_house::DATE_TIME_TEXT_ID)
            .unwrap();
        assert_eq!(
            text_of(&host.root, date_text),
            "Date: Frostfell 27, 119 P.Y.Time: Dawnsong"
        );
        // Indoors: the marker hides and the coordinates clear (after the
        // five-second refresh).
        let indoors = MapFacts {
            player_cell: 0x0012_0100,
            ..facts
        };
        lines.push(("x".to_owned(), HouseLineColor::Normal));
        host.with_controller::<MapHousePanelController, _>("map", |c, ui| {
            c.update(ui, &indoors, 1.0, &lines, &mut rows);
        });
        assert!(host.root.element(icon).unwrap().visible(), "not yet");
        host.with_controller::<MapHousePanelController, _>("map", |c, ui| {
            c.update(ui, &indoors, 5.0, &lines, &mut rows);
        });
        assert!(!host.root.element(icon).unwrap().visible());
        assert_eq!(text_of(&host.root, coordinates), "");
        let house = host
            .root
            .find_descendant(root, map_house::HOUSE_ICON_ID)
            .unwrap();
        assert!(!host.root.element(house).unwrap().visible());
        host.tick(0.016, 16, None);
        assert!(!host.draw((1024.0, 768.0), &p.c).main.is_empty());
        assert!(p.c.take_errors().is_empty());
    }

    /// OpenAC `MapNoteLiveDatTests`: the hotspot template's tooltip
    /// locator, rollover and highlight child.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_hotspot_template_has_its_popup_rollover_and_green_frame() {
        let c = content();
        let template = import_infos_rooted(&c, 0x2100_0026, 0x1000_01F0).expect("template");
        assert_eq!(template.effective_bool(0x13, None), Some(true), "rollover");
        let normal = template.state(1).expect("Normal");
        let rollover = template.state(2).expect("Normal_rollover");
        assert_eq!(normal.name, "Normal");
        assert_eq!(rollover.name, "Normal_rollover");
        let highlight = template
            .children
            .iter()
            .find(|ch| ch.id == 0x1000_01F1)
            .expect("highlight");
        let invisible = |s: u32| {
            highlight
                .state(s)
                .and_then(|st| st.properties.get(0x3B))
                .map(|p| p.value.clone())
        };
        assert_eq!(
            invisible(1),
            Some(ac_dat::objects::PropertyValue::Bool(true))
        );
        assert_eq!(
            invisible(2),
            Some(ac_dat::objects::PropertyValue::Bool(false))
        );
        assert_eq!(highlight.children.len(), 4);
        for edge in &highlight.children {
            assert_eq!(
                edge.state_media.get("").map(|m| m.file),
                Some(0x0600_4CC9),
                "the green frame"
            );
        }
    }
}
