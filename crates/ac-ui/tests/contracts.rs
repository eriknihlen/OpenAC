//! The journal's contracts page.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/JournalContractsPageControllerTests.cs`
//! on a synthetic page shaped like the retail one (the real page, journal
//! panel layout 0x2100006E, is imported in `real_dats`). OpenAC's
//! `IRuntimeContractView`, catalog and clock bindings are the
//! [`ContractsView`] handed to each update; its `Abandon` delegate is
//! `SessionCommand::AbandonContract`; its `Tick` is `update`.

mod common;

use std::collections::{BTreeMap, HashMap};
use std::sync::Arc;

use ac_core::quests::{ContractCatalog, ContractEntry};
use ac_proto::contract::{ContractTracker, ContractTrackerUpdate};
use ac_runtime::communication::{ContractState, TrackedContract};
use ac_runtime::session::SessionCommand;
use ac_ui::ElementId;
use ac_ui::draw::Color;
use ac_ui::element_info::{ElementInfo, TemplateEntry};
use ac_ui::event::kind;
use ac_ui::host::UiHost;
use ac_ui::panels::contracts::{self, ContractsPageController, ContractsView};
use ac_ui::root::UiRoot;
use ac_ui::tree::Element;
use ac_ui::widgets::{DatElement, ListBox, Text};
use common::items::*;
use common::panels::*;
use common::*;

/// 2026-08-21 12:00:00 UTC.
const NOW: i64 = 1_787_313_600_000;
const AUTHORED: Color = Color {
    r: 0.8,
    g: 0.8,
    b: 0.8,
    a: 1.0,
};
const WINDOW: &str = "journal";

fn text(ui: &mut UiRoot, parent: ElementId, id: u32) -> ElementId {
    let mut e = Element::at(0.0, 0.0, 200.0, 18.0);
    e.dat_element_id = id;
    Text::configure(&mut e);
    let mut t = Text::default();
    t.default_color = AUTHORED;
    ui.add(parent, e, t)
}

/// OpenAC's `RowTemplate`: a type-3 row holding the name and status.
fn row_template(ui: &mut UiRoot, layout: u32, element: u32) -> Option<ElementId> {
    if layout != contracts::ROW_TEMPLATE_LAYOUT_ID || element != contracts::ROW_TEMPLATE_ELEMENT_ID
    {
        return None;
    }
    let mut e = Element::at(0.0, 0.0, 270.0, 20.0);
    e.dat_element_id = element;
    DatElement::configure(&mut e);
    let info = Arc::new(ElementInfo {
        id: element,
        kind: 3,
        width: 270.0,
        height: 20.0,
        ..ElementInfo::default()
    });
    let row = ui.insert(e, DatElement::new(info));
    text(ui, row, contracts::ROW_NAME_ID);
    text(ui, row, contracts::ROW_STATUS_ID);
    Some(row)
}

struct Page {
    host: UiHost,
    page: ElementId,
    list: ElementId,
    state: ContractState,
    catalog: ContractCatalog,
    now: i64,
    builds: usize,
}

fn entry(id: u32, name: &str) -> ContractEntry {
    ContractEntry {
        contract_id: id,
        contract_name: name.to_owned(),
        ..ContractEntry::default()
    }
}

fn catalog(entries: Vec<ContractEntry>) -> ContractCatalog {
    ContractCatalog::new(entries.into_iter().map(|e| (e.contract_id, e)).collect())
}

fn track(state: &mut ContractState, id: u32, stage: u32, repeats: f64, done: f64, display: bool) {
    state.apply_update(
        &ContractTrackerUpdate {
            tracker: ContractTracker {
                version: 1,
                contract_id: id,
                stage,
                time_when_done: done,
                time_when_repeats: repeats,
            },
            delete: false,
            set_as_display: display,
        },
        NOW,
    );
}

fn page(state: ContractState, catalog: ContractCatalog) -> Page {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let page = panel(ui, r, at(0.0, 0.0, 300.0, 500.0));
    let info = Arc::new(ElementInfo {
        id: contracts::LIST_ID,
        kind: 5,
        width: 270.0,
        height: 298.0,
        template_list: vec![TemplateEntry {
            layout: contracts::ROW_TEMPLATE_LAYOUT_ID,
            element: contracts::ROW_TEMPLATE_ELEMENT_ID,
        }],
        ..ElementInfo::default()
    });
    let mut e = Element::at(0.0, 0.0, 270.0, 298.0);
    e.dat_element_id = contracts::LIST_ID;
    let list = ui.add(page, e, ListBox::new(info));
    for id in [
        contracts::STATUS_VALUE_ID,
        contracts::CONTACT_VALUE_ID,
        contracts::CONTACT_LOCATION_VALUE_ID,
        contracts::QUEST_LOCATION_VALUE_ID,
        contracts::DESCRIPTION_ID,
        contracts::TIMED_VALUE_ID,
    ] {
        text(ui, page, id);
    }
    let mut ids = HashMap::new();
    add_std_button(ui, &mut ids, page, contracts::ABANDON_BUTTON_ID);
    ui.register_window(WINDOW, page, None, 0).unwrap();
    let c = ContractsPageController::bind(ui, page);
    host.attach_controller(WINDOW, Box::new(c)).unwrap();
    let mut p = Page {
        host,
        page,
        list,
        state,
        catalog,
        now: NOW,
        builds: 0,
    };
    p.tick();
    p
}

impl Page {
    fn tick(&mut self) {
        let contracts: Vec<TrackedContract> = self.state.contracts().copied().collect();
        let view = ContractsView {
            contracts: &contracts,
            display_contract_id: self.state.display_contract_id(),
            revision: self.state.revision(),
            catalog: &self.catalog,
            now_unix_ms: self.now,
        };
        let builds = &mut self.builds;
        let mut rows = |ui: &mut UiRoot, l: u32, e: u32| {
            *builds += 1;
            row_template(ui, l, e)
        };
        self.host
            .with_controller::<ContractsPageController, _>(WINDOW, |c, ui| {
                c.update(ui, &view, &mut rows);
            })
            .unwrap();
    }

    fn with<R>(&mut self, f: impl FnOnce(&mut ContractsPageController, &mut UiRoot) -> R) -> R {
        self.host
            .with_controller::<ContractsPageController, _>(WINDOW, f)
            .unwrap()
    }

    fn text(&self, id: u32) -> String {
        let t = self.host.root.find_descendant(self.page, id).unwrap();
        text_of(&self.host.root, t)
    }

    fn selected(&mut self) -> u32 {
        self.with(|c, _| c.selected_contract_id())
    }

    fn rows(&mut self) -> Vec<ElementId> {
        self.with(|c, _| c.row_elements())
    }

    fn names(&mut self) -> Vec<(String, Color)> {
        self.rows()
            .into_iter()
            .map(|r| {
                let n = self
                    .host
                    .root
                    .find_descendant(r, contracts::ROW_NAME_ID)
                    .unwrap();
                let l = self.host.root.widget::<Text>(n).unwrap().content_lines();
                (l[0].text.clone(), l[0].color)
            })
            .collect()
    }
}

// TheCountdownTurnsOverOnTheTrackersPhaseNotTheWallClock
#[test]
fn the_countdown_turns_over_on_the_trackers_phase_not_the_wall_clock() {
    let mut s = ContractState::new();
    let received = NOW + 500;
    s.apply_update(
        &ContractTrackerUpdate {
            tracker: ContractTracker {
                version: 1,
                contract_id: 0x10,
                stage: 3,
                time_when_done: 0.0,
                time_when_repeats: 600.0,
            },
            delete: false,
            set_as_display: false,
        },
        received,
    );
    let mut c = entry(0x10, "First");
    c.questflag_repeat_time = "flag".into();
    let mut p = page(s, catalog(vec![c]));
    p.now = received + 200;
    p.tick();
    let first = p.text(contracts::STATUS_VALUE_ID);
    assert!(!first.is_empty());
    p.now = received + 900;
    p.tick();
    assert_eq!(p.text(contracts::STATUS_VALUE_ID), first);
    p.now = received + 1_200;
    p.tick();
    assert_ne!(p.text(contracts::STATUS_VALUE_ID), first);
}

// RowsCarryTheAuthoredNameAndTheRetailProgressText
#[test]
fn rows_carry_the_authored_name() {
    let mut s = ContractState::new();
    track(&mut s, 0x10, 2, 0.0, 0.0, false);
    let mut p = page(s, catalog(vec![entry(0x10, "Aerlinthe Recall Ring")]));
    assert_eq!(p.with(|c, _| c.row_contract_ids().to_vec()), [0x10]);
    assert_eq!(p.names()[0].0, "Aerlinthe Recall Ring");
    let row = p.rows()[0];
    let status = p
        .host
        .root
        .find_descendant(row, contracts::ROW_STATUS_ID)
        .unwrap();
    assert_eq!(text_of(&p.host.root, status), "In Progress");
}

// TheDetailPaneShowsTheSelectedContract
#[test]
fn the_detail_pane_shows_the_selected_contract() {
    let mut s = ContractState::new();
    track(&mut s, 0x10, 2, 0.0, 0.0, false);
    track(&mut s, 0x20, 1, 0.0, 0.0, false);
    let mut first = entry(0x10, "First");
    first.description = "Kill the thing.".into();
    first.name_npc_start = "Bob".into();
    let mut second = entry(0x20, "Second");
    second.description = "Find the other thing.".into();
    second.name_npc_start = "Alice".into();
    let mut p = page(s, catalog(vec![first, second]));
    p.with(|c, ui| c.select(ui, 0x20));
    p.tick();
    assert_eq!(p.text(contracts::DESCRIPTION_ID), "Find the other thing.");
    assert_eq!(p.text(contracts::CONTACT_VALUE_ID), "Alice");
    assert_eq!(p.text(contracts::STATUS_VALUE_ID), "Available");
}

// TheServersDisplayContractIsWhatOpensSelected
#[test]
fn the_servers_display_contract_is_what_opens_selected() {
    let mut s = ContractState::new();
    track(&mut s, 0x10, 2, 0.0, 0.0, false);
    track(&mut s, 0x20, 2, 0.0, 0.0, true);
    let mut p = page(
        s,
        catalog(vec![entry(0x10, "First"), entry(0x20, "Second")]),
    );
    assert_eq!(p.selected(), 0x20);
}

// WithNoDisplayContractTheFirstRowStands
#[test]
fn with_no_display_contract_the_first_row_stands() {
    let mut s = ContractState::new();
    track(&mut s, 0x30, 2, 0.0, 0.0, false);
    track(&mut s, 0x10, 2, 0.0, 0.0, false);
    let mut p = page(s, catalog(vec![entry(0x10, "First"), entry(0x30, "Third")]));
    // Rows are in id order, so 0x10 is first.
    assert_eq!(p.selected(), 0x10);
}

// AnEmptyTrackerClearsTheDetailPaneRatherThanStrandingText
#[test]
fn an_empty_tracker_clears_the_detail_pane() {
    let mut s = ContractState::new();
    track(&mut s, 0x10, 2, 0.0, 0.0, false);
    let mut e = entry(0x10, "First");
    e.description = "Kill it.".into();
    let mut p = page(s, catalog(vec![e]));
    assert_eq!(p.text(contracts::DESCRIPTION_ID), "Kill it.");
    p.state.apply_table(&BTreeMap::new(), NOW);
    p.tick();
    assert_eq!(p.selected(), 0);
    assert_eq!(p.text(contracts::DESCRIPTION_ID), "");
    assert!(p.with(|c, _| c.row_contract_ids().is_empty()));
}

// TheListRebuildsOnlyWhenTheTrackerActuallyMoved
#[test]
fn the_list_rebuilds_only_when_the_tracker_moved() {
    let mut p = page(ContractState::new(), catalog(vec![entry(0x10, "First")]));
    track(&mut p.state, 0x10, 2, 0.0, 0.0, false);
    p.tick();
    let after_first_change = p.builds;
    for _ in 0..10 {
        p.tick();
    }
    assert_eq!(p.builds, after_first_change);
}

// AContractTheDatHasNeverHeardOfStillGetsARow
#[test]
fn a_contract_the_dat_never_heard_of_still_gets_a_row() {
    let mut s = ContractState::new();
    track(&mut s, 0xDEAD, 2, 0.0, 0.0, false);
    let mut p = page(s, ContractCatalog::default());
    assert_eq!(p.with(|c, _| c.row_contract_ids().to_vec()), [0xDEAD]);
    assert_eq!(p.text(contracts::STATUS_VALUE_ID), "In Progress");
}

// ALocationWithNoOutdoorCoordinatesReadsAsIndoors / AnUnsetLocationIsBlankRatherThanIndoors
#[test]
fn locations_read_as_coordinates_indoors_or_blank() {
    let mut s = ContractState::new();
    track(&mut s, 0x10, 2, 0.0, 0.0, false);
    let mut e = entry(0x10, "First");
    e.location_npc_start_cell = 0x0102_0304;
    let p = page(s, catalog(vec![e]));
    assert_eq!(p.text(contracts::CONTACT_LOCATION_VALUE_ID), "Indoors");
    assert_eq!(p.text(contracts::QUEST_LOCATION_VALUE_ID), "");
    // An outdoor cell reads as the radar's coordinates.
    assert_eq!(
        contracts::location_text(0xA9B4_0001),
        ac_ui::radar::RadarCoordinates::from_cell(0xA9B4_0001)
            .unwrap()
            .combined_text()
    );
}

// TheTimedRowUsesTimeWhenDoneNotTimeWhenRepeats
#[test]
fn the_timed_row_uses_time_when_done() {
    let mut s = ContractState::new();
    track(&mut s, 0x10, 2, 90.0, 3661.0, false);
    let p = page(s, catalog(vec![entry(0x10, "First")]));
    assert_eq!(p.text(contracts::TIMED_VALUE_ID), "1h 1m 1s");
}

// TheRepeatCountdownTicksWithoutRebuildingTheList
#[test]
fn the_repeat_countdown_ticks_without_rebuilding_the_list() {
    let mut s = ContractState::new();
    track(&mut s, 0x10, 3, 600.0, 0.0, false);
    let mut e = entry(0x10, "First");
    e.questflag_repeat_time = "f".into();
    let mut p = page(s, catalog(vec![e]));
    assert_eq!(
        p.text(contracts::STATUS_VALUE_ID),
        "Done (10m 0s to Repeat)"
    );
    let builds = p.builds;
    p.now = NOW + 5 * 60 * 1000;
    p.tick();
    assert_eq!(p.text(contracts::STATUS_VALUE_ID), "Done (5m 0s to Repeat)");
    assert_eq!(p.builds, builds);
}

// RowsOptOutOfClickThroughOrTheListIsDead / ClickingARowSelectsIt
#[test]
fn clicking_a_row_selects_it() {
    let mut s = ContractState::new();
    track(&mut s, 0x10, 2, 0.0, 0.0, false);
    track(&mut s, 0x20, 2, 0.0, 0.0, false);
    let mut second = entry(0x20, "Second");
    second.description = "Second desc.".into();
    let mut p = page(s, catalog(vec![entry(0x10, "First"), second]));
    assert_eq!(p.selected(), 0x10);
    let rows = p.rows();
    assert!(!p.host.root.element(rows[1]).unwrap().click_through);
    p.host.root.fire_event(kind::CLICK, rows[1], None);
    p.host.pump();
    assert_eq!(p.selected(), 0x20);
    p.tick();
    assert_eq!(p.text(contracts::DESCRIPTION_ID), "Second desc.");
}

// TheSelectedRowIsHighlightedAndTheOthersAreNot
#[test]
fn the_selected_row_is_highlighted_and_the_others_are_not() {
    let mut s = ContractState::new();
    track(&mut s, 0x10, 2, 0.0, 0.0, false);
    track(&mut s, 0x20, 2, 0.0, 0.0, false);
    let mut p = page(
        s,
        catalog(vec![entry(0x10, "First"), entry(0x20, "Second")]),
    );
    let names = p.names();
    assert_eq!(names[0].1, contracts::SELECTED_NAME_COLOR);
    assert_eq!(names[1].1, AUTHORED);
    p.with(|c, ui| c.select(ui, 0x20));
    let names = p.names();
    assert_eq!(names[0].1, AUTHORED);
    assert_eq!(names[1].1, contracts::SELECTED_NAME_COLOR);
}

// TheHighlightSurvivesARebuild
#[test]
fn the_highlight_survives_a_rebuild() {
    let mut s = ContractState::new();
    track(&mut s, 0x10, 2, 0.0, 0.0, false);
    track(&mut s, 0x20, 2, 0.0, 0.0, false);
    let mut p = page(
        s,
        catalog(vec![entry(0x10, "First"), entry(0x20, "Second")]),
    );
    p.with(|c, ui| c.select(ui, 0x20));
    track(&mut p.state, 0x30, 2, 0.0, 0.0, false);
    p.tick();
    assert_eq!(p.selected(), 0x20);
    let names = p.names();
    assert_eq!(names[1].1, contracts::SELECTED_NAME_COLOR);
    assert_eq!(names[0].1, AUTHORED);
}

// AbandonSendsTheSelectedContractAndRemovesNothingLocally / AbandonWithNothingSelectedSendsNothing
#[test]
fn abandon_sends_the_selected_contract_and_nothing_without_one() {
    let mut p = page(ContractState::new(), ContractCatalog::default());
    let abandon = p
        .host
        .root
        .find_descendant(p.page, contracts::ABANDON_BUTTON_ID)
        .unwrap();
    p.host.root.fire_event(kind::CLICK, abandon, None);
    p.host.pump();
    assert!(sessions(&panel_outputs(&mut p.host)).is_empty());
    track(&mut p.state, 0x10, 2, 0.0, 0.0, false);
    p.tick();
    p.host.root.fire_event(kind::CLICK, abandon, None);
    p.host.pump();
    let out = sessions(&panel_outputs(&mut p.host));
    assert_eq!(
        format!("{out:?}"),
        format!("{:?}", [SessionCommand::AbandonContract(0x10)])
    );
    assert_eq!(p.with(|c, _| c.row_contract_ids().len()), 1);
}

// AProgressCounterRendersThroughTheAuthoredFormat
#[test]
fn a_progress_counter_renders_through_the_authored_format() {
    let mut s = ContractState::new();
    track(&mut s, 0x10, 9, 0.0, 0.0, false);
    let mut e = entry(0x10, "First");
    e.description_progress = "%d/20 Tuskers".into();
    let p = page(s, catalog(vec![e]));
    assert_eq!(p.text(contracts::STATUS_VALUE_ID), "5/20 Tuskers");
}

#[test]
fn the_list_rows_sit_in_the_list() {
    let mut s = ContractState::new();
    track(&mut s, 0x10, 2, 0.0, 0.0, false);
    let p = page(s, catalog(vec![entry(0x10, "First")]));
    let l = p.host.root.widget::<ListBox>(p.list).unwrap();
    assert_eq!(l.item_count(&p.host.root), 1);
}

// ── Real dats ───────────────────────────────────────────────────────────

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import_rooted};
    use ac_ui::panels::DatRowTemplates;
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

    /// The journal panel: host layout 0x2100006E, slot 0x10000559.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_contracts_page_imports_binds_and_lists_a_contract() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import_rooted(
            &mut host.root,
            &c,
            0x2100_006E,
            0x1000_0559,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("journal layout");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        host.root
            .register_window(WINDOW, layout.root, None, 0)
            .unwrap();
        let page = layout.find(contracts::PAGE_ID).expect("contracts page");
        let list = layout.find(contracts::LIST_ID).expect("contract list");
        let templates = host.root.widget::<ListBox>(list).unwrap().templates.clone();
        assert_eq!(
            templates.first().copied(),
            Some(TemplateEntry {
                layout: contracts::ROW_TEMPLATE_LAYOUT_ID,
                element: contracts::ROW_TEMPLATE_ELEMENT_ID,
            })
        );
        let ctl = ContractsPageController::bind(&mut host.root, page);
        host.attach_controller(WINDOW, Box::new(ctl)).unwrap();
        let mut s = ContractState::new();
        track(&mut s, 0x10, 2, 0.0, 0.0, false);
        let contracts: Vec<TrackedContract> = s.contracts().copied().collect();
        let cat = catalog(vec![entry(0x10, "Aerlinthe Recall Ring")]);
        let view = ContractsView {
            contracts: &contracts,
            display_contract_id: 0,
            revision: s.revision(),
            catalog: &cat,
            now_unix_ms: NOW,
        };
        let mut rows = DatRowTemplates::new(&c, c.font(DEFAULT_FONT_ID));
        host.with_controller::<ContractsPageController, _>(WINDOW, |w, ui| {
            w.update(ui, &view, &mut rows);
        });
        let row = host
            .with_controller::<ContractsPageController, _>(WINDOW, |w, _| w.row_elements())
            .unwrap()[0];
        let name = host
            .root
            .find_descendant(row, contracts::ROW_NAME_ID)
            .expect("row name");
        assert_eq!(text_of(&host.root, name), "Aerlinthe Recall Ring");
        assert!(!host.root.element(row).unwrap().click_through);
        let status = layout.find(contracts::STATUS_VALUE_ID).unwrap();
        assert_eq!(text_of(&host.root, status), "In Progress");
        let drawn = host.draw((1024.0, 768.0), &c);
        assert!(!drawn.main.is_empty());
        assert!(c.take_errors().is_empty());
    }
}
