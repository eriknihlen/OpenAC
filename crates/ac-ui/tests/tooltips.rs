//! Element and world tooltips.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/RetailTooltipPresenterTests.cs`.
//! OpenAC's presenter hears the root's tooltip events synchronously; here
//! the host routes them when it pumps, which every host input and tick
//! call does.

mod common;

use std::cell::{Cell, RefCell};
use std::rc::Rc;

use ac_ui::ElementId;
use ac_ui::MouseButton;
use ac_ui::element_info::ElementInfo;
use ac_ui::host::UiHost;
use ac_ui::import::{BuildContext, ImportedLayout, LayoutSource, build};
use ac_ui::root::UiRoot;
use ac_ui::tooltip::{
    SHARED_POPUP_SKIN_LAYOUT_DID, SHARED_POPUP_SKIN_ROOT_ELEMENT_ID, TooltipPresenter, WorldHover,
};
use ac_ui::tree::{Authored, Container, Element, Widget};
use ac_ui::widgets::{Text, TextContent};
use common::*;

const POPUP_ROOT_ID: u32 = 0x900;
const TEXT_CHILD_ID: u32 = 0x901;
const POPUP_LAYOUT_DID: u32 = 0x2100_0041;

#[derive(Debug)]
struct NoSource;

impl LayoutSource for NoSource {
    fn layout(&self, _id: u32) -> Option<std::sync::Arc<ac_dat::objects::LayoutDesc>> {
        None
    }
}

fn popup_infos(text_kind: u32, max_width: Option<i32>) -> ElementInfo {
    let mut root = ElementInfo {
        id: POPUP_ROOT_ID,
        kind: 3,
        width: 30.0,
        height: 30.0,
        tooltip_text_child_element_id: TEXT_CHILD_ID,
        max_width,
        ..ElementInfo::default()
    };
    root.children.push(ElementInfo {
        id: TEXT_CHILD_ID,
        kind: text_kind,
        x: 2.0,
        y: 2.0,
        width: 26.0,
        height: 26.0,
        ..ElementInfo::default()
    });
    root
}

type Requests = Rc<RefCell<Vec<(u32, u32)>>>;

fn build_popup(ui: &mut UiRoot, infos: &ElementInfo) -> ImportedLayout {
    let ctx = BuildContext {
        source: &NoSource,
        default_font: None,
        source_layout_did: 0,
        media_seconds: 0.0,
    };
    build(ui, infos, &ctx)
}

fn harness_with(
    infos: ElementInfo,
    adjust: impl Fn(&mut UiRoot, &ImportedLayout) + 'static,
) -> (UiHost, Requests) {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let requests: Requests = Rc::default();
    let log = requests.clone();
    host.set_tooltips(TooltipPresenter::new(Box::new(
        move |ui, layout, root_id| {
            log.borrow_mut().push((layout, root_id));
            let built = build_popup(ui, &infos);
            adjust(ui, &built);
            Some(built)
        },
    )));
    (host, requests)
}

fn harness() -> (UiHost, Requests) {
    harness_with(popup_infos(12, None), |_, _| {})
}

fn authored(text: Option<&str>) -> Element {
    Element {
        authored: Authored {
            tooltip_enabled: true,
            tooltip_text: text.map(str::to_owned),
            tooltip_root_element_id: POPUP_ROOT_ID,
            tooltip_layout_did: POPUP_LAYOUT_DID,
            ..Authored::default()
        },
        ..at(100.0, 100.0, 40.0, 20.0)
    }
}

fn add_target(host: &mut UiHost, text: &str) -> ElementId {
    let r = host.root.root();
    host.root.add(r, authored(Some(text)), Container)
}

fn count(host: &UiHost) -> usize {
    host.root.children(host.root.root()).len()
}

fn hover_and_dwell(host: &mut UiHost) {
    host.mouse_move(110, 110);
    host.tick(0.016, 0, None);
    let d = host.root.tooltip_delay_ms;
    host.tick(0.016, d, None);
}

fn popup(host: &UiHost, target: ElementId) -> ElementId {
    let r = host.root.root();
    host.root
        .children(r)
        .into_iter()
        .find(|c| *c != target)
        .expect("a popup")
}

fn shown_text(host: &UiHost, popup: ElementId) -> String {
    let t = host.root.find_descendant(popup, TEXT_CHILD_ID).unwrap();
    match host.root.widget::<Text>(t).unwrap().content() {
        TextContent::Lines(l) => l
            .iter()
            .map(|l| l.text.as_str())
            .collect::<Vec<_>>()
            .join(" "),
        other => panic!("{other:?}"),
    }
}

#[test]
fn nothing_shows_without_tooltip_properties() {
    let (mut host, requests) = harness();
    let r = host.root.root();
    host.root.add(r, at(100.0, 100.0, 40.0, 20.0), Container);
    let before = count(&host);
    hover_and_dwell(&mut host);
    host.tick(0.016, 300, None);
    assert!(requests.borrow().is_empty());
    assert_eq!(count(&host), before);
}

#[test]
fn the_popup_mounts_only_after_the_dwell() {
    let (mut host, requests) = harness();
    add_target(&mut host, "Rotate left.");
    let before = count(&host);
    host.mouse_move(110, 110);
    host.tick(0.016, 0, None);
    assert_eq!(count(&host), before);
    host.tick(0.016, 249, None);
    assert_eq!(count(&host), before);
    host.tick(0.016, 250, None);
    assert_eq!(count(&host), before + 1);
    assert_eq!(*requests.borrow(), [(POPUP_LAYOUT_DID, POPUP_ROOT_ID)]);
}

#[test]
fn the_global_gate_suppresses_presentation_but_the_dwell_still_fires() {
    let (mut host, requests) = harness();
    host.tooltips_mut().unwrap().enabled = false;
    add_target(&mut host, "Rotate left.");
    let before = count(&host);
    hover_and_dwell(&mut host);
    let fired = host
        .take_outputs()
        .iter()
        .any(|o| matches!(o, ac_ui::UiOutput::TooltipShow(_)));
    assert!(fired);
    assert!(requests.borrow().is_empty());
    assert_eq!(count(&host), before);
}

#[test]
fn the_elements_own_switch_and_missing_text_suppress_it() {
    let (mut host, requests) = harness();
    let t = add_target(&mut host, "Rotate left.");
    host.root.element_mut(t).unwrap().authored.tooltip_enabled = false;
    hover_and_dwell(&mut host);
    assert!(requests.borrow().is_empty());

    let (mut host, requests) = harness();
    let r = host.root.root();
    host.root.add(r, authored(None), Container);
    hover_and_dwell(&mut host);
    assert!(requests.borrow().is_empty());
}

#[test]
fn moving_to_another_element_dismisses_it() {
    let (mut host, _) = harness();
    add_target(&mut host, "Rotate left.");
    let r = host.root.root();
    host.root.add(r, at(400.0, 400.0, 40.0, 20.0), Container);
    let before = count(&host);
    hover_and_dwell(&mut host);
    assert_eq!(count(&host), before + 1);
    host.mouse_move(410, 410);
    assert_eq!(count(&host), before);
}

#[test]
fn removing_the_owner_dismisses_it() {
    let (mut host, _) = harness();
    let t = add_target(&mut host, "Rotate left.");
    let before = count(&host);
    hover_and_dwell(&mut host);
    assert_eq!(count(&host), before + 1);
    let r = host.root.root();
    host.root.remove_child(r, t);
    host.pump();
    assert_eq!(count(&host), before - 1);
}

#[test]
fn it_hides_after_the_duration() {
    let (mut host, _) = harness();
    add_target(&mut host, "Rotate left.");
    let before = count(&host);
    hover_and_dwell(&mut host);
    assert_eq!(count(&host), before + 1);
    let end = host.root.tooltip_delay_ms + host.root.tooltip_duration_ms;
    host.tick(0.016, end, None);
    assert_eq!(count(&host), before);
}

#[test]
fn a_per_element_delay_overrides_the_global_one() {
    let (mut host, requests) = harness();
    let t = add_target(&mut host, "Rotate left.");
    host.root
        .element_mut(t)
        .unwrap()
        .authored
        .tooltip_delay_seconds = Some(0.0);
    host.mouse_move(110, 110);
    host.tick(0.016, 0, None);
    assert_eq!(requests.borrow().len(), 1);
}

#[test]
fn the_popup_stays_on_the_display() {
    let (mut host, _) = harness();
    host.root.set_size(40.0, 40.0);
    let r = host.root.root();
    let mut e = authored(Some("hi"));
    e.left = 0.0;
    e.top = 0.0;
    e.width = 40.0;
    e.height = 40.0;
    let t = host.root.add(r, e, Container);
    host.mouse_move(38, 38);
    host.tick(0.016, 0, None);
    host.tick(0.016, 250, None);
    let p = host.root.element(popup(&host, t)).unwrap();
    assert!(p.left >= 0.0 && p.top >= 0.0);
    assert!(p.left + p.width <= 40.0 && p.top + p.height <= 40.0);
}

#[test]
fn it_grows_by_the_measured_text_and_sits_32_pixels_off_the_pointer() {
    let (mut host, _) = harness();
    let t = add_target(
        &mut host,
        "This is a much longer tooltip than the authored placeholder.",
    );
    hover_and_dwell(&mut host);
    let p = host.root.element(popup(&host, t)).unwrap();
    assert!(p.width > 30.0);
    assert_eq!((p.left, p.top), (142.0, 142.0));
}

#[test]
fn moving_within_the_element_restarts_the_dwell() {
    let (mut host, requests) = harness();
    add_target(&mut host, "Rotate left.");
    host.mouse_move(110, 110);
    host.tick(0.016, 0, None);
    host.tick(0.016, 240, None);
    assert!(requests.borrow().is_empty());
    host.mouse_move(111, 111);
    host.tick(0.016, 250, None);
    assert!(requests.borrow().is_empty());
    host.tick(0.016, 490, None);
    assert_eq!(requests.borrow().len(), 1);
}

#[test]
fn a_text_child_that_is_not_text_never_mounts() {
    let (mut host, requests) = harness_with(popup_infos(3, None), |_, _| {});
    add_target(&mut host, "Rotate left.");
    let before = count(&host);
    hover_and_dwell(&mut host);
    assert_eq!(requests.borrow().len(), 1);
    assert_eq!(count(&host), before);
}

#[test]
fn capture_elsewhere_holds_the_dwell_until_released() {
    let (mut host, requests) = harness();
    add_target(&mut host, "Rotate left.");
    let r = host.root.root();
    let other = host.root.add(r, at(400.0, 400.0, 40.0, 20.0), Container);
    host.mouse_move(110, 110);
    host.tick(0.016, 0, None);
    host.root.set_capture(other);
    host.tick(0.016, 300, None);
    assert!(requests.borrow().is_empty());
    host.root.release_capture();
    host.tick(0.016, 600, None);
    assert_eq!(requests.borrow().len(), 1);
}

#[test]
fn releasing_capture_while_shown_does_not_hide_and_reshow() {
    let (mut host, requests) = harness();
    let t = add_target(&mut host, "Rotate left.");
    let before = count(&host);
    hover_and_dwell(&mut host);
    assert_eq!(count(&host), before + 1);
    host.take_outputs();
    host.root.set_capture(t);
    host.root.release_capture();
    host.tick(0.016, 510, None);
    let hid = host
        .take_outputs()
        .iter()
        .any(|o| matches!(o, ac_ui::UiOutput::TooltipHide(_)));
    assert!(!hid);
    assert_eq!(count(&host), before + 1);
    assert_eq!(requests.borrow().len(), 1);
}

#[test]
fn the_authored_maximum_width_clamps_the_popup() {
    let (mut host, _) = harness_with(popup_infos(12, Some(40)), |_, _| {});
    let t = add_target(
        &mut host,
        "This is a much longer tooltip than the authored placeholder, long enough to want to grow well past forty pixels wide.",
    );
    hover_and_dwell(&mut host);
    assert_eq!(host.root.element(popup(&host, t)).unwrap().width, 40.0);
}

fn clamped(max_width: i32, margin_x: f32, margin_y: f32) -> impl Fn(&mut UiRoot, &ImportedLayout) {
    move |ui, built| {
        let t = built.find(TEXT_CHILD_ID).unwrap();
        ui.element_mut(t).unwrap().authored.resize_max_width = Some(max_width);
        let text = ui.widget_mut::<Text>(t).unwrap();
        text.margin_left = margin_x;
        text.margin_right = margin_x;
        text.margin_top = margin_y;
        text.margin_bottom = margin_y;
    }
}

fn long_text() -> String {
    vec!["description"; 40].join(" ")
}

#[test]
fn a_long_tooltip_rewraps_at_the_clamped_width_and_grows_in_height() {
    let (mut host, _) = harness_with(popup_infos(12, None), clamped(200, 0.0, 0.0));
    let t = add_target(&mut host, &long_text());
    hover_and_dwell(&mut host);
    let p = popup(&host, t);
    let pe = host.root.element(p).unwrap();
    assert!(pe.width <= 230.0);
    let text_id = host.root.find_descendant(p, TEXT_CHILD_ID).unwrap();
    let text = host.root.widget::<Text>(text_id).unwrap();
    assert!(!text.centered && !text.right_aligned);
    let TextContent::Lines(lines) = text.content() else {
        panic!()
    };
    assert!(lines.len() >= 3);
    assert!(
        lines
            .iter()
            .all(|l| l.text.chars().count() as f32 * 8.0 <= 208.0)
    );
    assert!(pe.height >= lines.len() as f32 * 14.0);
}

#[test]
fn authored_margins_shrink_the_wrap_and_add_height() {
    let (mut host, _) = harness_with(popup_infos(12, None), clamped(200, 40.0, 8.0));
    let t = add_target(&mut host, &long_text());
    hover_and_dwell(&mut host);
    let p = popup(&host, t);
    let text_id = host.root.find_descendant(p, TEXT_CHILD_ID).unwrap();
    let text = host.root.widget::<Text>(text_id).unwrap();
    let TextContent::Lines(lines) = text.content() else {
        panic!()
    };
    assert!(lines.len() >= 3);
    assert!(
        lines
            .iter()
            .all(|l| l.text.chars().count() as f32 * 8.0 <= 128.0)
    );
    assert_eq!(text.margin_left, 40.0);
    assert!(host.root.element(p).unwrap().height >= lines.len() as f32 * 14.0 + 16.0);
}

/// An element whose tooltip text is set at run time.
fn runtime_target(
    host: &mut UiHost,
    runtime: Option<&str>,
    on: bool,
    layout: u32,
    source: u32,
) -> ElementId {
    let r = host.root.root();
    let mut e = authored(None);
    e.authored.tooltip_enabled = on;
    e.authored.tooltip_layout_did = layout;
    e.source_layout_did = source;
    e.runtime_tooltip_text = runtime.map(str::to_owned);
    host.root.add(r, e, Container)
}

#[test]
fn runtime_text_shows_without_authored_text_and_wins_over_it() {
    let (mut host, requests) = harness();
    let t = runtime_target(
        &mut host,
        Some("When this option is chosen, you will always appear as offline."),
        true,
        POPUP_LAYOUT_DID,
        0,
    );
    hover_and_dwell(&mut host);
    assert_eq!(*requests.borrow(), [(POPUP_LAYOUT_DID, POPUP_ROOT_ID)]);
    assert!(shown_text(&host, popup(&host, t)).contains("appear as offline"));

    let (mut host, _) = harness();
    let t = runtime_target(&mut host, Some("runtime wins"), true, POPUP_LAYOUT_DID, 0);
    host.root.element_mut(t).unwrap().authored.tooltip_text = Some("authored loses".into());
    hover_and_dwell(&mut host);
    let shown = shown_text(&host, popup(&host, t));
    assert!(shown.contains("runtime wins") && !shown.contains("authored loses"));
}

#[test]
fn runtime_text_skips_the_authored_switch_authored_text_does_not() {
    let (mut host, requests) = harness();
    runtime_target(
        &mut host,
        Some("runtime text, authored bit off"),
        false,
        POPUP_LAYOUT_DID,
        0,
    );
    hover_and_dwell(&mut host);
    assert_eq!(requests.borrow().len(), 1);

    let (mut host, requests) = harness();
    let t = runtime_target(&mut host, None, false, POPUP_LAYOUT_DID, 0);
    host.root.element_mut(t).unwrap().authored.tooltip_text = Some("authored, but off".into());
    hover_and_dwell(&mut host);
    assert!(requests.borrow().is_empty());
}

#[test]
fn the_popup_layout_falls_back_to_the_source_layout_and_needs_a_root() {
    let (mut host, requests) = harness();
    runtime_target(&mut host, Some("no P0x48 authored"), true, 0, 0x2100_0099);
    hover_and_dwell(&mut host);
    assert_eq!(*requests.borrow(), [(0x2100_0099, POPUP_ROOT_ID)]);

    let (mut host, requests) = harness();
    runtime_target(&mut host, Some("nowhere to build the popup"), true, 0, 0);
    hover_and_dwell(&mut host);
    assert!(requests.borrow().is_empty());

    let (mut host, requests) = harness();
    let t = runtime_target(
        &mut host,
        Some("text but no popup root"),
        true,
        POPUP_LAYOUT_DID,
        0,
    );
    host.root
        .element_mut(t)
        .unwrap()
        .authored
        .tooltip_root_element_id = 0;
    hover_and_dwell(&mut host);
    assert!(requests.borrow().is_empty());
}

// ── World hover ─────────────────────────────────────────────────────────

const WORLD_GUID: u32 = 0x8000_0123;

type NameLookup = Box<dyn Fn(u32) -> Option<String>>;

#[derive(Default)]
struct World {
    found: Cell<Option<u32>>,
    names: RefCell<Option<NameLookup>>,
    enabled: Cell<bool>,
    gate_reads: Cell<u32>,
    name_reads: Cell<u32>,
}

impl World {
    fn new(found: Option<u32>) -> Self {
        let w = Self::default();
        w.found.set(found);
        w.enabled.set(true);
        w
    }
}

impl WorldHover for World {
    fn hovered(&self) -> Option<u32> {
        self.found.get()
    }
    fn name(&self, guid: u32) -> Option<String> {
        self.name_reads.set(self.name_reads.get() + 1);
        match self.names.borrow().as_ref() {
            Some(f) => f(guid),
            None => Some("A Drudge".into()),
        }
    }
    fn tooltips_enabled(&self) -> bool {
        self.gate_reads.set(self.gate_reads.get() + 1);
        self.enabled.get()
    }
}

fn world_tick(host: &mut UiHost, world: &World) {
    host.tick_tooltips(Some(world));
}

#[test]
fn world_hover_stages_on_the_found_edge_and_mounts_after_the_dwell() {
    let (mut host, requests) = harness();
    let world = World::new(Some(WORLD_GUID));
    let before = count(&host);
    host.root.tick(0.016, 0);
    world_tick(&mut host, &world);
    assert!(requests.borrow().is_empty());
    host.root.tick(0.016, 249);
    world_tick(&mut host, &world);
    assert!(requests.borrow().is_empty());
    host.root.tick(0.016, 250);
    world_tick(&mut host, &world);
    assert_eq!(
        *requests.borrow(),
        [(
            SHARED_POPUP_SKIN_LAYOUT_DID,
            SHARED_POPUP_SKIN_ROOT_ELEMENT_ID
        )]
    );
    assert_eq!(count(&host), before + 1);
}

#[test]
fn world_hover_waits_for_the_pointer_to_rest() {
    let (mut host, requests) = harness();
    let world = World::new(Some(WORLD_GUID));
    for t in (0..=2000).step_by(100) {
        host.root.tick(0.016, t);
        host.mouse_move(100 + (t / 10) as i32, 100);
        world_tick(&mut host, &world);
    }
    assert!(requests.borrow().is_empty());
    host.root.tick(0.016, 2250);
    world_tick(&mut host, &world);
    assert_eq!(requests.borrow().len(), 1);
}

#[test]
fn world_hover_hides_when_nothing_is_found_and_obeys_the_gates() {
    let (mut host, _) = harness();
    let world = World::new(Some(WORLD_GUID));
    let before = count(&host);
    host.root.tick(0.016, 250);
    world_tick(&mut host, &world);
    assert_eq!(count(&host), before + 1);
    world.found.set(None);
    world_tick(&mut host, &world);
    assert_eq!(count(&host), before);

    let (mut host, requests) = harness();
    let world = World::new(Some(WORLD_GUID));
    world.enabled.set(false);
    host.root.tick(0.016, 300);
    world_tick(&mut host, &world);
    world_tick(&mut host, &world);
    assert!(requests.borrow().is_empty());

    let (mut host, requests) = harness();
    let world = World::new(Some(WORLD_GUID));
    *world.names.borrow_mut() = Some(Box::new(|_| None));
    host.root.tick(0.016, 300);
    world_tick(&mut host, &world);
    world_tick(&mut host, &world);
    assert!(requests.borrow().is_empty());

    let (mut host, requests) = harness();
    host.tooltips_mut().unwrap().enabled = false;
    let world = World::new(Some(WORLD_GUID));
    host.root.tick(0.016, 300);
    world_tick(&mut host, &world);
    world_tick(&mut host, &world);
    assert!(requests.borrow().is_empty());
}

#[test]
fn world_hover_is_suppressed_over_ui() {
    let (mut host, requests) = harness();
    let world = World::new(Some(WORLD_GUID));
    let r = host.root.root();
    host.root.add(r, at(100.0, 100.0, 40.0, 20.0), Container);
    host.mouse_move(110, 110);
    host.root.tick(0.016, 300);
    world_tick(&mut host, &world);
    assert!(requests.borrow().is_empty());
}

#[test]
fn swapping_world_objects_replaces_the_popup_without_stacking() {
    let (mut host, requests) = harness();
    let world = World::new(Some(WORLD_GUID));
    *world.names.borrow_mut() = Some(Box::new(|g| {
        Some(
            if g == WORLD_GUID {
                "A Drudge"
            } else {
                "A Door"
            }
            .to_owned(),
        )
    }));
    let before = count(&host);
    host.root.tick(0.016, 250);
    world_tick(&mut host, &world);
    assert_eq!(count(&host), before + 1);
    world.found.set(Some(0x8000_0456));
    world_tick(&mut host, &world);
    assert_eq!(count(&host), before + 1);
    assert_eq!(requests.borrow().len(), 2);
    for g in [WORLD_GUID, 0x8000_0456, WORLD_GUID] {
        world.found.set(Some(g));
        world_tick(&mut host, &world);
    }
    assert_eq!(count(&host), before + 1);
}

#[test]
fn a_world_tooltip_hides_after_its_duration_and_remounts_after_a_move_and_dwell() {
    let (mut host, requests) = harness();
    let world = World::new(Some(WORLD_GUID));
    let before = count(&host);
    host.root.tick(0.016, 250);
    world_tick(&mut host, &world);
    assert_eq!(count(&host), before + 1);
    let expiry = 250 + host.root.tooltip_duration_ms;
    host.root.tick(0.016, expiry);
    world_tick(&mut host, &world);
    assert_eq!(count(&host), before);
    host.root.tick(0.016, expiry + 500);
    world_tick(&mut host, &world);
    assert_eq!(count(&host), before);
    assert_eq!(requests.borrow().len(), 1);
    let move_at = expiry + 600;
    host.root.tick(0.016, move_at);
    host.mouse_move(5, 5);
    world_tick(&mut host, &world);
    assert_eq!(requests.borrow().len(), 1);
    host.root.tick(0.016, move_at + 250);
    world_tick(&mut host, &world);
    assert_eq!(requests.borrow().len(), 2);
    assert_eq!(count(&host), before + 1);
}

#[test]
fn during_a_drag_the_world_tooltip_mounts_at_once() {
    let (mut host, requests) = harness();
    let world = World::new(None);
    let r = host.root.root();
    host.root.add(
        r,
        at(100.0, 100.0, 40.0, 20.0),
        DragSource {
            payload: Some(1),
            ..DragSource::default()
        },
    );
    let before = count(&host);
    host.root.tick(0.016, 0);
    host.mouse_down(MouseButton::Left, 110, 110, 0);
    host.mouse_move(130, 130);
    assert!(host.root.drag_source().is_some());
    host.mouse_move(300, 300);
    world.found.set(Some(WORLD_GUID));
    world_tick(&mut host, &world);
    assert_eq!(requests.borrow().len(), 1);
    assert_eq!(count(&host), before + 1);
}

#[test]
fn ui_and_world_tooltips_replace_each_other() {
    let (mut host, _) = harness();
    let world = World::new(Some(WORLD_GUID));
    let before = count(&host);
    host.root.tick(0.016, 250);
    world_tick(&mut host, &world);
    assert_eq!(count(&host), before + 1);
    let t = add_target(&mut host, "Rotate left.");
    host.tick(0.016, 260, Some(&world));
    host.mouse_move(110, 110);
    host.tick(0.016, 510, Some(&world));
    assert_eq!(count(&host), before + 2, "the target and one popup");
    let p = popup(&host, t);
    world_tick(&mut host, &world);
    assert_eq!(count(&host), before + 2);
    assert_eq!(popup(&host, t), p);
}

#[test]
fn a_modal_stealing_the_pointer_lets_the_world_tooltip_replace_the_ui_one() {
    let (mut host, _) = harness();
    let t = add_target(&mut host, "Rotate left.");
    let before = count(&host);
    hover_and_dwell(&mut host);
    assert_eq!(count(&host), before + 1);
    let modal = host.root.insert(at(0.0, 0.0, 10.0, 10.0), Container);
    host.root.modal = Some(modal);
    let world = World::new(Some(WORLD_GUID));
    world_tick(&mut host, &world);
    assert_eq!(count(&host), before + 1);
    let _ = popup(&host, t);
}

#[test]
fn the_world_gate_and_name_are_read_only_on_the_found_edge() {
    let (mut host, requests) = harness();
    let world = World::new(Some(WORLD_GUID));
    host.root.tick(0.016, 250);
    for _ in 0..3 {
        world_tick(&mut host, &world);
    }
    assert_eq!((world.gate_reads.get(), world.name_reads.get()), (1, 1));
    assert_eq!(requests.borrow().len(), 1);
}

#[test]
fn the_world_popup_has_its_text_element() {
    let (mut host, _) = harness();
    let world = World::new(Some(WORLD_GUID));
    *world.names.borrow_mut() = Some(Box::new(|_| Some("Iron Bars".into())));
    host.root.tick(0.016, 250);
    world_tick(&mut host, &world);
    let r = host.root.root();
    let popups = host.root.children(r);
    assert_eq!(popups.len(), 1);
    let t = host.root.find_descendant(popups[0], TEXT_CHILD_ID).unwrap();
    assert!(host.root.widget::<Text>(t).is_some());
    let _: &dyn Widget = host.root.widget_dyn(t).unwrap();
}

#[test]
fn a_tooltip_over_an_open_menu_list_is_drawn_under_the_list() {
    // OpenAC draws the same way: `RetailTooltipPresenter` mounts the popup
    // as a child of the root (`AddChild`, `BringToFront`), drawn by
    // `UiRoot.DrawSelfAndChildren`, while an open `UiMenu` paints its list
    // in `OnDrawOverlay`, after every window (`DrawCore`). Hovering the open
    // list hovers the menu (`PopupHit` in `UpdateHover`), so the menu's own
    // tooltip shows beneath its list (smoke 3, finding 1: kept as OpenAC).
    use ac_ui::draw::NoSprites;
    use ac_ui::widgets::{Menu, MenuItem, Panel};
    use ac_ui::{Color, DrawCmd};
    const MARK: Color = Color::rgba(1.0, 0.0, 1.0, 1.0);
    let (mut host, _) = harness_with(popup_infos(12, None), |ui, built| {
        let panel = Panel {
            background_color: MARK,
            ..Panel::default()
        };
        ui.add(built.root, at(0.0, 0.0, 30.0, 30.0), panel);
    });
    let mut m = Menu::default();
    m.open_upward = false;
    m.retail_button_art = false;
    m.items = vec![MenuItem::new("Off", 0), MenuItem::new("High", 1)];
    m.tooltip_text = Some("The game's own picture, unchanged.".into());
    let list_color = m.plain_background_color;
    let mut e = authored(None);
    Menu::configure(&mut e);
    let r = host.root.root();
    let menu = host.root.add(r, e, m);
    host.mouse_move(110, 110);
    host.mouse_down(MouseButton::Left, 110, 110, 0);
    host.mouse_up(MouseButton::Left, 110, 110, 0);
    assert!(host.root.widget::<Menu>(menu).unwrap().is_open());
    // The pointer rests on the list's first row.
    host.mouse_move(110, 128);
    host.tick(0.016, 0, None);
    let d = host.root.tooltip_delay_ms;
    host.tick(0.016, d, None);
    assert_eq!(count(&host), 2, "the tooltip is mounted");
    let list = host.draw((800.0, 600.0), &NoSprites);
    let fills = |cmds: &[DrawCmd], c: Color| {
        cmds.iter()
            .filter(|d| matches!(d, DrawCmd::Fill { color, .. } if *color == c))
            .count()
    };
    assert!(
        fills(&list.main, MARK) > 0,
        "the tooltip is in the window layer"
    );
    assert_eq!(fills(&list.overlay, MARK), 0);
    assert!(
        fills(&list.overlay, list_color) > 0,
        "the list is in the overlay"
    );
}
