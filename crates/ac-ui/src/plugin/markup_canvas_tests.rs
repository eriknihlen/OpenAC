//! Canvases inside plugin windows (a markup `<canvas id="...">` hosting the
//! plugin's in-markup canvas) and resizing a canvas on the screen, through
//! the session, its window copy, the window builder, the canvas layer and
//! the draw path, the way the client mounts them. Behaviour from OpenAC
//! `UiMarkupCanvas`, `PluginMarkupCanvases` and `IPluginCanvas.TryResize`.

use std::cell::RefCell;
use std::collections::BTreeMap;
use std::rc::Rc;

use ac_plugin_ui::{
    BindingSchema, CanvasAnchor, CanvasDescriptor, PanelDescriptor, PluginUi, PluginUiView,
    PointerKind, UiEvent, UiInput, UiOwner,
};

use super::canvas::{CanvasHost, CanvasLayer, CanvasWidget};
use super::*;
use crate::event::MouseButton;
use crate::host::UiHost;
use crate::overlay::OverlayHost;

const OWNER: &str = "test.atlas";

struct NoIcons;

impl MarkupIcons for NoIcons {
    fn surface(&mut self, _id: u32) -> Option<Icon> {
        None
    }
    fn spell(&mut self, _spell: u32) -> Option<Icon> {
        None
    }
    fn item(&mut self, _object: u32) -> Option<Icon> {
        None
    }
}

fn descriptor(id: &str, in_markup: bool) -> CanvasDescriptor {
    CanvasDescriptor {
        canvas_id: id.into(),
        width: 50,
        height: 40,
        anchor: CanvasAnchor::TopLeft,
        offset: (0.0, 0.0),
        start_visible: true,
        accepts_pointer_input: true,
        in_markup,
    }
}

/// The session, its window's copy and the window, mounted and synced each
/// frame as the client does, with the session hearing what the window
/// reports.
struct Rig {
    state: PluginUi,
    view: PluginUiView,
    host: UiHost,
    band: OverlayHost,
    layer: CanvasLayer,
    outbox: PluginOutbox,
    /// The mounted windows by panel, with their window names and frames.
    windows: BTreeMap<u32, (String, ElementId)>,
    revision: u64,
    notices: Vec<String>,
    events: Vec<UiEvent>,
}

impl Rig {
    fn new() -> Self {
        let mut state = PluginUi::new();
        state.set_window_attached(true);
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let mut band = OverlayHost::mount(&mut host.root);
        let layer = CanvasLayer::mount(&mut host.root, &mut band);
        let mut view = PluginUiView::new();
        view.apply_all(state.snapshot());
        Self {
            state,
            view,
            host,
            band,
            layer,
            outbox: Rc::new(RefCell::new(Vec::new())),
            windows: BTreeMap::new(),
            revision: 0,
            notices: Vec::new(),
            events: Vec::new(),
        }
    }

    /// Registers a window `name` with `body` in a 300 x 200 panel at
    /// (100, 50), and opens it.
    fn window(&mut self, name: &str, panel: &str, body: &str) -> u32 {
        let markup =
            format!("<panel x=\"100\" y=\"50\" w=\"300\" h=\"200\" {panel}>{body}</panel>");
        let id = self
            .state
            .register_panel(
                &UiOwner::new(OWNER, "Atlas"),
                PanelDescriptor::new(name, name),
                &markup,
                BindingSchema::new("Atlas"),
            )
            .unwrap();
        assert!(self.state.show_panel(OWNER, name, true));
        id
    }

    fn canvas(&mut self, id: &str, in_markup: bool) -> u32 {
        let owner = UiOwner::new(OWNER, "Atlas");
        let canvas = self
            .state
            .register_canvas(&owner, descriptor(id, in_markup))
            .unwrap();
        assert!(self.state.listen_canvas(OWNER, canvas, true));
        canvas
    }

    /// One frame: the copy catches up, windows mount and go, they sync,
    /// the layer pairs and places the canvases, the frame is drawn (which
    /// lays each `<canvas>` element's canvas out), and the session hears
    /// what the window said.
    fn frame(&mut self) {
        self.view.apply_all(self.state.take_changes());
        let gone: Vec<u32> = self
            .windows
            .keys()
            .copied()
            .filter(|id| self.view.panel(*id).is_none())
            .collect();
        for id in gone {
            let (name, frame) = self.windows.remove(&id).unwrap();
            self.host.root.unregister_window(&name);
            self.host.pump();
            self.host.root.destroy(frame);
        }
        for panel in &self.view.panels {
            if self.windows.contains_key(&panel.id) {
                continue;
            }
            let controller = build_panel(&mut self.host.root, panel, None, Rc::clone(&self.outbox));
            let frame = controller.root;
            let name = controller.window_name.clone();
            let top = self.host.root.root();
            self.host.root.add_child(top, frame);
            self.host
                .root
                .register_window(&name, frame, Some(frame), controller.geometry_revision())
                .unwrap();
            self.host
                .attach_controller(&name, Box::new(controller))
                .unwrap();
            self.windows.insert(panel.id, (name, frame));
        }
        self.revision += 1;
        let mut hosts = Vec::new();
        for (id, (name, _)) in &self.windows {
            let panel = self.view.panel(*id).unwrap().clone();
            let revision = self.revision;
            let found = self
                .host
                .with_controller::<PluginPanelController, _>(name, |c, ui| {
                    c.sync(ui, &panel, revision, &mut NoIcons, PluginTheme::Classic);
                    c.canvas_hosts()
                })
                .unwrap();
            hosts.extend(found.into_iter().map(|(canvas_id, element)| CanvasHost {
                owner: panel.owner.id.clone(),
                canvas_id,
                element,
            }));
        }
        self.host.tick(0.0, 0, None);
        let mut no_image = |_: &str, _: u32| None;
        self.layer.sync(
            &mut self.host.root,
            &self.band,
            &self.view,
            &hosts,
            &mut no_image,
            &mut |_| unreachable!("no fonts"),
            None,
            &self.outbox,
        );
        let _ = self.host.draw((800.0, 600.0), &crate::draw::NoSprites);
        self.notices.extend(self.layer.take_notices());
        self.deliver();
    }

    /// The window's reports reach the session, and the session's events are
    /// kept.
    fn deliver(&mut self) {
        let inputs: Vec<UiInput> = self.outbox.borrow_mut().drain(..).collect();
        for input in inputs {
            self.state.input(input);
        }
        self.events
            .extend(self.state.take_events().into_iter().map(|(_, e)| e));
    }

    /// Two frames: the second sees the sizes the first laid out.
    fn settle(&mut self) {
        self.frame();
        self.frame();
    }

    fn find(&self, name: &str) -> ElementId {
        fn walk(ui: &UiRoot, start: ElementId, name: &str) -> Option<ElementId> {
            if ui.element(start)?.name.as_deref() == Some(name) {
                return Some(start);
            }
            ui.children(start)
                .into_iter()
                .find_map(|c| walk(ui, c, name))
        }
        let root = self.host.root.root();
        walk(&self.host.root, root, name).unwrap_or_else(|| panic!("no element {name}"))
    }

    /// An element's place on the screen and size.
    fn screen_rect(&self, id: ElementId) -> (f32, f32, f32, f32) {
        let (x, y) = self.host.root.screen_position(id);
        let e = self.host.root.element(id).unwrap();
        (x, y, e.width, e.height)
    }

    /// The pointer input the canvas was sent since the last call: kind, x,
    /// y.
    fn pointer(&mut self, canvas: u32) -> Vec<(PointerKind, f32, f32)> {
        self.deliver();
        let heard = self
            .events
            .iter()
            .filter_map(|e| match e {
                UiEvent::Pointer {
                    canvas: c, event, ..
                } if *c == canvas => Some((event.kind, event.x, event.y)),
                _ => None,
            })
            .collect();
        self.events
            .retain(|e| !matches!(e, UiEvent::Pointer { .. }));
        heard
    }

    /// The sizes the plugin heard its canvas laid out at, since the last
    /// call.
    fn resized(&mut self) -> Vec<(u32, i32, i32)> {
        let heard = self
            .events
            .iter()
            .filter_map(|e| match e {
                UiEvent::CanvasResized {
                    canvas,
                    width,
                    height,
                } => Some((*canvas, *width, *height)),
                _ => None,
            })
            .collect();
        self.events
            .retain(|e| !matches!(e, UiEvent::CanvasResized { .. }));
        heard
    }

    fn size(&self, canvas: u32) -> (i32, i32) {
        self.state.canvas(OWNER, canvas).unwrap().size
    }
}

const FLEX: &str = r#"layout="column""#;
const MAP_BELOW_A_LABEL: &str = r#"<label text="Route"/><canvas id="map" grow="1"/>"#;

#[test]
fn a_canvas_element_is_laid_out_like_any_flex_item_and_its_canvas_fills_it() {
    let mut rig = Rig::new();
    rig.window("atlas", FLEX, MAP_BELOW_A_LABEL);
    let canvas = rig.canvas("map", true);
    rig.settle();
    let el = rig.layer.element(canvas).expect("hosted");
    let host = rig.layer.host_of(canvas).expect("an element hosts it");
    assert_eq!(rig.host.root.parent(el), Some(host));
    assert!(rig.host.root.widget::<canvas::MarkupCanvas>(host).is_some());
    let (hx, hy, hw, hh) = rig.screen_rect(host);
    let (cx, cy, cw, ch) = rig.screen_rect(el);
    assert_eq!((cx, cy), (hx, hy), "at the element's top-left");
    assert_eq!((cw, ch), (hw.round(), hh.round()));
    // Grown below the label, across the content area.
    assert!(hw > 250.0 && hh > 100.0, "{hw} x {hh}");
    // The plugin's size is the laid-out one, and it heard it once.
    let laid_out = (cw as i32, ch as i32);
    assert_eq!(rig.size(canvas), laid_out);
    assert_eq!(rig.resized(), vec![(canvas, laid_out.0, laid_out.1)]);
    assert!(rig.state.canvas_available(OWNER, canvas));
    rig.settle();
    assert!(rig.resized().is_empty(), "the same size again is no news");
    // The window outside the canvas is not on the overlay band.
    assert!(!rig.host.root.element(rig.layer.layer()).unwrap().visible);
}

#[test]
fn a_fractional_element_hosts_its_canvas_in_whole_pixels_and_a_new_size_repaints() {
    let mut rig = Rig::new();
    let panel = rig.window(
        "atlas",
        "",
        r#"<canvas id="map" x="10" y="30" w="120.6" h="80.4"/>"#,
    );
    let canvas = rig.canvas("map", true);
    rig.settle();
    let el = rig.layer.element(canvas).unwrap();
    let (_, _, w, h) = rig.screen_rect(el);
    assert_eq!((w, h), (121.0, 80.0));
    assert_eq!(rig.resized(), vec![(canvas, 121, 80)]);
    let _ = panel;
}

#[test]
fn resizing_the_window_lays_its_canvas_out_again_and_the_plugin_hears_the_new_size() {
    let mut rig = Rig::new();
    rig.window("atlas", FLEX, MAP_BELOW_A_LABEL);
    let canvas = rig.canvas("map", true);
    rig.settle();
    let el = rig.layer.element(canvas).unwrap();
    let (_, _, w, h) = rig.screen_rect(el);
    rig.resized();
    // The player drags the window 100.4 wider and 40 taller.
    let (_, frame) = rig.windows.values().next().cloned().unwrap();
    if let Some(e) = rig.host.root.element_mut(frame) {
        e.width += 100.4;
        e.height += 40.0;
    }
    rig.settle();
    let (_, _, w2, h2) = rig.screen_rect(el);
    assert_eq!((w2, h2), ((w + 100.4).round(), h + 40.0));
    assert_eq!(rig.resized(), vec![(canvas, w2 as i32, h2 as i32)]);
    assert_eq!(rig.size(canvas), (w2 as i32, h2 as i32));
}

#[test]
fn a_canvas_with_no_room_shows_nothing_and_keeps_its_size() {
    let mut rig = Rig::new();
    rig.window(
        "atlas",
        "",
        r#"<canvas id="map" x="10" y="30" w="0" h="0"/>"#,
    );
    let canvas = rig.canvas("map", true);
    rig.settle();
    let el = rig.layer.element(canvas).unwrap();
    assert_eq!(
        rig.screen_rect(el).2,
        50.0,
        "its registered size, clipped away"
    );
    assert!(rig.resized().is_empty());
    assert_eq!(rig.size(canvas), (50, 40));
    // Hosted all the same: a press where it would be reaches nothing.
    assert!(rig.state.canvas_available(OWNER, canvas));
    let (x, y, _, _) = rig.screen_rect(el);
    rig.host
        .mouse_down(MouseButton::Left, x as i32 + 5, y as i32 + 5, 0);
    rig.host
        .mouse_up(MouseButton::Left, x as i32 + 5, y as i32 + 5, 0);
    assert!(rig.pointer(canvas).is_empty());
}

#[test]
fn a_canvas_registered_before_its_window_waits_and_says_so_once() {
    let mut rig = Rig::new();
    let canvas = rig.canvas("map", true);
    rig.settle();
    rig.settle();
    assert_eq!(rig.layer.element(canvas), None);
    assert!(!rig.state.canvas_available(OWNER, canvas));
    assert_eq!(rig.notices.len(), 1, "{:?}", rig.notices);
    assert!(
        rig.notices[0].contains(
            "'test.atlas/map' is registered in markup and waits for a <canvas id=\"map\">"
        ),
        "{}",
        rig.notices[0]
    );
    rig.window("atlas", FLEX, MAP_BELOW_A_LABEL);
    rig.settle();
    assert!(rig.layer.host_of(canvas).is_some());
    assert!(rig.state.canvas_available(OWNER, canvas));
    assert_eq!(rig.resized().len(), 1);
    assert_eq!(rig.notices.len(), 1, "nothing more to say");
}

#[test]
fn a_canvas_element_draws_nothing_until_its_canvas_is_registered() {
    let mut rig = Rig::new();
    rig.window("atlas", FLEX, MAP_BELOW_A_LABEL);
    rig.settle();
    let host = rig.find("map");
    assert!(rig.host.root.children(host).is_empty());
    assert!(
        rig.notices.is_empty(),
        "a canvas not yet registered just waits"
    );
    let canvas = rig.canvas("map", true);
    rig.settle();
    assert_eq!(rig.layer.host_of(canvas), Some(host));
    assert_eq!(rig.host.root.children(host).len(), 1);
    assert!(rig.notices.is_empty(), "{:?}", rig.notices);
}

#[test]
fn closing_the_window_stops_input_and_opening_it_again_brings_it_back() {
    let mut rig = Rig::new();
    rig.window("atlas", FLEX, MAP_BELOW_A_LABEL);
    let canvas = rig.canvas("map", true);
    rig.settle();
    let el = rig.layer.element(canvas).unwrap();
    let (x, y, _, _) = rig.screen_rect(el);
    let (px, py) = (x as i32 + 12, y as i32 + 7);
    rig.host.mouse_down(MouseButton::Left, px, py, 0);
    rig.host.mouse_up(MouseButton::Left, px, py, 0);
    assert_eq!(
        rig.pointer(canvas),
        vec![(PointerKind::Down, 12.0, 7.0), (PointerKind::Up, 12.0, 7.0)]
    );
    // Closed mid-press: the press is cancelled, and nothing more arrives.
    rig.host.mouse_down(MouseButton::Left, px, py, 0);
    assert!(rig.state.show_panel(OWNER, "atlas", false));
    rig.frame();
    rig.host.mouse_up(MouseButton::Left, px, py, 0);
    rig.host.mouse_down(MouseButton::Left, px, py, 0);
    rig.host.mouse_up(MouseButton::Left, px, py, 0);
    let kinds: Vec<PointerKind> = rig.pointer(canvas).into_iter().map(|p| p.0).collect();
    assert_eq!(kinds, vec![PointerKind::Down, PointerKind::Cancelled]);
    // Still hosted while closed, at the same size, and the same element.
    assert!(rig.state.canvas_available(OWNER, canvas));
    assert_eq!(rig.layer.element(canvas), Some(el));
    // Opened again: the pointer reaches it.
    assert!(rig.state.show_panel(OWNER, "atlas", true));
    rig.settle();
    rig.host.mouse_down(MouseButton::Left, px, py, 0);
    rig.host.mouse_up(MouseButton::Left, px, py, 0);
    assert_eq!(rig.pointer(canvas).len(), 2);
    assert!(rig.resized().len() == 1, "only the first layout was news");
}

#[test]
fn a_hidden_tab_page_hides_its_canvas_from_the_pointer() {
    let mut rig = Rig::new();
    rig.window(
        "atlas",
        "",
        r#"<group name="page" x="10" y="30" w="200" h="120"><canvas id="map" x="0" y="0" w="200" h="120"/></group>"#,
    );
    let canvas = rig.canvas("map", true);
    rig.settle();
    let el = rig.layer.element(canvas).unwrap();
    let (x, y, _, _) = rig.screen_rect(el);
    let page = rig.find("page");
    rig.host.root.set_visible(page, false);
    rig.host
        .mouse_down(MouseButton::Left, x as i32 + 3, y as i32 + 3, 0);
    rig.host
        .mouse_up(MouseButton::Left, x as i32 + 3, y as i32 + 3, 0);
    assert!(rig.pointer(canvas).is_empty());
    rig.host.root.set_visible(page, true);
    rig.host
        .mouse_down(MouseButton::Left, x as i32 + 3, y as i32 + 3, 0);
    rig.host
        .mouse_up(MouseButton::Left, x as i32 + 3, y as i32 + 3, 0);
    assert_eq!(rig.pointer(canvas).len(), 2);
}

#[test]
fn of_two_elements_naming_a_canvas_the_first_mounted_hosts_it_and_the_other_takes_over() {
    let mut rig = Rig::new();
    let first = rig.window("first", FLEX, MAP_BELOW_A_LABEL);
    rig.window("second", FLEX, r#"<canvas id="map" grow="1"/>"#);
    let canvas = rig.canvas("map", true);
    rig.settle();
    let names: Vec<String> = rig.windows.values().map(|(n, _)| n.clone()).collect();
    let hosts: Vec<ElementId> = names
        .iter()
        .filter_map(|name| {
            rig.host
                .with_controller::<PluginPanelController, _>(name, |c, _| c.canvas_hosts())
        })
        .flatten()
        .map(|(_, e)| e)
        .collect();
    assert_eq!(hosts.len(), 2, "window names: {:?}", rig.windows);
    assert_eq!(rig.layer.host_of(canvas), Some(hosts[0]));
    assert!(rig.host.root.children(hosts[1]).is_empty());
    assert_eq!(rig.notices.len(), 1, "{:?}", rig.notices);
    assert!(
        rig.notices[0]
            .contains("<canvas id=\"map\"> of plugin 'test.atlas' shows nothing: another window"),
        "{}",
        rig.notices[0]
    );
    rig.settle();
    assert_eq!(rig.notices.len(), 1, "said once");
    let first_size = rig.size(canvas);
    rig.resized();
    // The first window goes: the second takes the canvas, at its own size.
    assert!(rig.state.remove_panel(OWNER, first));
    rig.settle();
    assert_eq!(rig.layer.host_of(canvas), Some(hosts[1]));
    assert!(rig.state.canvas_available(OWNER, canvas));
    let second_size = rig.size(canvas);
    assert_ne!(second_size, first_size, "the second window has no label");
    assert_eq!(rig.resized(), vec![(canvas, second_size.0, second_size.1)]);
    assert_eq!(rig.notices.len(), 1, "moving says nothing");
}

#[test]
fn a_canvas_whose_window_goes_waits_quietly_and_comes_back_with_a_new_window() {
    let mut rig = Rig::new();
    let panel = rig.window("atlas", FLEX, MAP_BELOW_A_LABEL);
    let canvas = rig.canvas("map", true);
    rig.settle();
    assert!(rig.state.remove_panel(OWNER, panel));
    rig.settle();
    assert_eq!(rig.layer.element(canvas), None);
    assert!(!rig.state.canvas_available(OWNER, canvas));
    assert!(rig.notices.is_empty(), "{:?}", rig.notices);
    rig.window("atlas", FLEX, MAP_BELOW_A_LABEL);
    rig.settle();
    assert!(rig.state.canvas_available(OWNER, canvas));
}

#[test]
fn an_element_naming_a_canvas_on_the_screen_says_why_it_shows_nothing_once() {
    let mut rig = Rig::new();
    rig.window("atlas", FLEX, MAP_BELOW_A_LABEL);
    let canvas = rig.canvas("map", false);
    rig.settle();
    rig.settle();
    assert_eq!(rig.layer.host_of(canvas), None);
    assert_eq!(rig.notices.len(), 1, "{:?}", rig.notices);
    assert!(
        rig.notices[0].contains(
            "registered canvas 'map' without in_markup, so it sits on the screen by itself"
        ),
        "{}",
        rig.notices[0]
    );
    // It is on the band, at its anchor.
    let el = rig.layer.element(canvas).unwrap();
    assert_eq!(rig.host.root.parent(el), Some(rig.layer.layer()));
}

#[test]
fn a_canvas_hosted_in_a_window_hears_the_pointer_in_its_own_pixels() {
    let mut rig = Rig::new();
    rig.window("atlas", FLEX, MAP_BELOW_A_LABEL);
    let canvas = rig.canvas("map", true);
    rig.settle();
    let el = rig.layer.element(canvas).unwrap();
    let (x, y, _, _) = rig.screen_rect(el);
    let (x, y) = (x as i32, y as i32);
    rig.host.mouse_move(x + 20, y + 10);
    rig.host.mouse_down(MouseButton::Left, x + 20, y + 10, 0);
    rig.host.mouse_move(x + 40, y + 30);
    rig.host.mouse_up(MouseButton::Left, x + 40, y + 30, 0);
    rig.host.root.on_scroll_xy(0, 1, false);
    rig.host.pump();
    assert_eq!(
        rig.pointer(canvas),
        vec![
            (PointerKind::Hover, 20.0, 10.0),
            (PointerKind::Down, 20.0, 10.0),
            (PointerKind::Move, 40.0, 30.0),
            (PointerKind::Up, 40.0, 30.0),
            (PointerKind::Wheel, 40.0, 30.0),
            (PointerKind::Hover, 40.0, 30.0),
        ]
    );
    // A press on the canvas does not drag the window.
    let (_, frame) = rig.windows.values().next().cloned().unwrap();
    let before = rig.screen_rect(frame);
    rig.host.mouse_down(MouseButton::Left, x + 20, y + 10, 0);
    rig.host.mouse_move(x + 80, y + 60);
    rig.host.mouse_up(MouseButton::Left, x + 80, y + 60, 0);
    assert_eq!(rig.screen_rect(frame), before);
    // The window does not know where on the band it is: no place.
    let placed =
        rig.outbox.borrow().iter().any(
            |i| matches!(i, UiInput::Pointer { place: Some(_), canvas: c, .. } if *c == canvas),
        );
    assert!(!placed);
}

#[test]
fn a_canvas_on_the_screen_resized_keeps_its_right_edge() {
    let mut rig = Rig::new();
    let owner = UiOwner::new(OWNER, "Atlas");
    let canvas = rig
        .state
        .register_canvas(
            &owner,
            CanvasDescriptor {
                anchor: CanvasAnchor::TopRight,
                offset: (-20.0, 30.0),
                width: 100,
                height: 50,
                ..descriptor("hud", false)
            },
        )
        .unwrap();
    assert!(rig.state.listen_canvas(OWNER, canvas, true));
    rig.frame();
    let el = rig.layer.element(canvas).unwrap();
    assert_eq!(rig.screen_rect(el), (680.0, 30.0, 100.0, 50.0));
    assert!(rig.state.resize_canvas(OWNER, canvas, 200, 80));
    rig.frame();
    assert_eq!(rig.layer.element(canvas), Some(el), "the same canvas");
    assert_eq!(rig.screen_rect(el), (580.0, 30.0, 200.0, 80.0));
    assert!(rig.host.root.element(el).unwrap().visible);
    assert!(
        rig.host
            .root
            .widget::<CanvasWidget>(el)
            .unwrap()
            .canvas
            .listening
    );
}

#[test]
fn a_new_window_attaching_hears_nothing_was_told_yet() {
    // The window that attaches after another left tells the session the
    // hosting again: the session forgot it when the first one left.
    let mut rig = Rig::new();
    rig.window("atlas", FLEX, MAP_BELOW_A_LABEL);
    let canvas = rig.canvas("map", true);
    rig.settle();
    rig.state.set_window_attached(false);
    assert!(!rig.state.canvas_available(OWNER, canvas));
    rig.state.set_window_attached(true);
    rig.view.apply_all(rig.state.snapshot());
    rig.settle();
    assert!(rig.state.canvas_available(OWNER, canvas));
}
