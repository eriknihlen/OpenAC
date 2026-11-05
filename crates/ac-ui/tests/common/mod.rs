//! Test widgets and helpers shared by the ac-ui integration tests.

#![allow(dead_code)]

pub mod items;
pub mod panels;

use ac_ui::event::{DragPayload, UiEvent, kind};
use ac_ui::geometry::{AnchorEdges, ResizeEdges};
use ac_ui::root::UiRoot;
use ac_ui::tree::{DragGhost, Element, ElementId, Widget};
use ac_ui::widgets::Panel;

/// A root of the given size.
pub fn root(w: f32, h: f32) -> UiRoot {
    let mut ui = UiRoot::new();
    ui.set_size(w, h);
    ui
}

/// Element geometry builder.
pub fn at(left: f32, top: f32, width: f32, height: f32) -> Element {
    Element::at(left, top, width, height)
}

/// A plain panel element (OpenAC's `UiPanel`) under `parent`.
pub fn panel(ui: &mut UiRoot, parent: ElementId, e: Element) -> ElementId {
    ui.add(parent, e, Panel::default())
}

/// A window panel with the usual flags.
pub fn window(left: f32, top: f32, width: f32, height: f32) -> Element {
    Element {
        draggable: true,
        ..Element::at(left, top, width, height)
    }
}

/// What a [`ClickRecorder`] runs on each click.
pub type ClickAction = Box<dyn FnMut(&mut UiRoot)>;

/// Element that records clicks.
#[derive(Default)]
pub struct ClickRecorder {
    pub handles_click: bool,
    pub clicks: u32,
    pub double_clicks: u32,
    pub action: Option<ClickAction>,
}

impl std::fmt::Debug for ClickRecorder {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ClickRecorder")
            .field("clicks", &self.clicks)
            .finish()
    }
}

impl Widget for ClickRecorder {
    fn on_event(&mut self, ui: &mut UiRoot, _me: ElementId, e: &UiEvent) -> bool {
        match e.kind {
            kind::CLICK => {
                self.clicks += 1;
                if let Some(a) = self.action.as_mut() {
                    a(ui);
                }
                true
            }
            kind::DOUBLE_CLICK => {
                self.double_clicks += 1;
                true
            }
            _ => false,
        }
    }

    fn handles_click(&self, _element: &Element) -> bool {
        self.handles_click
    }
}

/// Element that records the local coordinates of presses and moves.
#[derive(Debug, Default)]
pub struct CoordRecorder {
    pub down: Option<(i32, i32)>,
    pub moved: Option<(i32, i32)>,
}

impl Widget for CoordRecorder {
    fn on_event(&mut self, _ui: &mut UiRoot, _me: ElementId, e: &UiEvent) -> bool {
        match e.kind {
            kind::MOUSE_DOWN => {
                self.down = Some((e.data1, e.data2));
                true
            }
            kind::MOUSE_MOVE => {
                self.moved = Some((e.data1, e.data2));
                true
            }
            _ => false,
        }
    }
}

/// A text-entry stand-in: takes focus, records typed characters, counts
/// Enter presses as submissions.
#[derive(Debug, Default)]
pub struct CharField {
    pub text: String,
    pub submissions: u32,
}

impl CharField {
    pub fn element(width: f32, height: f32) -> Element {
        Element {
            accepts_focus: true,
            is_edit_control: true,
            ..Element::at(0.0, 0.0, width, height)
        }
    }
}

impl Widget for CharField {
    fn on_event(&mut self, _ui: &mut UiRoot, _me: ElementId, e: &UiEvent) -> bool {
        match e.kind {
            kind::CHAR => {
                let c = char::from_u32(e.data0 as u32).unwrap_or('\0');
                if c == '\r' {
                    self.submissions += 1;
                } else {
                    self.text.push(c);
                }
                true
            }
            kind::KEY_DOWN => true,
            _ => false,
        }
    }
}

/// Records tooltip and time-pulse order.
#[derive(Debug, Default)]
pub struct TimeOrderRecorder {
    pub events: Vec<&'static str>,
}

impl Widget for TimeOrderRecorder {
    fn on_event(&mut self, _ui: &mut UiRoot, _me: ElementId, e: &UiEvent) -> bool {
        if e.kind != kind::TOOLTIP {
            return false;
        }
        self.events.push("tooltip");
        true
    }

    fn global_time(&mut self, _ui: &mut UiRoot, _me: ElementId, _now: f64) {
        self.events.push("global");
    }
}

/// Runs an action on the time pulse.
pub struct GlobalTimeAction(pub Box<dyn FnMut(&mut UiRoot)>);

impl std::fmt::Debug for GlobalTimeAction {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("GlobalTimeAction")
    }
}

impl Widget for GlobalTimeAction {
    fn global_time(&mut self, ui: &mut UiRoot, _me: ElementId, _now: f64) {
        (self.0)(ui);
    }
}

/// A drag source: lifts `payload` (if any) with an optional ghost, and
/// records when it is told the drag started and ended.
#[derive(Debug, Default)]
pub struct DragSource {
    pub payload: Option<u32>,
    pub ghost: Option<DragGhost>,
    pub activations: Vec<bool>,
}

impl Widget for DragSource {
    fn is_drag_source(&self, _element: &Element) -> bool {
        true
    }

    fn drag_payload(&mut self, _ui: &mut UiRoot, _me: ElementId) -> Option<DragPayload> {
        self.payload.map(DragPayload::new)
    }

    fn drag_ghost(&mut self, _ui: &mut UiRoot, _me: ElementId) -> Option<DragGhost> {
        self.ghost
    }

    fn set_drag_source_active(
        &mut self,
        _ui: &mut UiRoot,
        _me: ElementId,
        active: bool,
        _payload: Option<&DragPayload>,
    ) {
        self.activations.push(active);
    }
}

/// Records drag-enter, drag-leave and drops with their payloads.
#[derive(Debug, Default)]
pub struct DropTarget {
    pub entered: Vec<u32>,
    pub left: u32,
    pub dropped: Vec<(u32, i32, i32)>,
}

impl Widget for DropTarget {
    fn on_event(&mut self, _ui: &mut UiRoot, _me: ElementId, e: &UiEvent) -> bool {
        let payload = e.payload.as_ref().and_then(|p| p.get::<u32>()).copied();
        match e.kind {
            kind::DRAG_ENTER => {
                self.entered.extend(payload);
                true
            }
            kind::DRAG_OVER => {
                self.left += 1;
                true
            }
            kind::DROP_RELEASED => {
                if let Some(p) = payload {
                    self.dropped.push((p, e.data1, e.data2));
                }
                true
            }
            _ => false,
        }
    }
}

/// A window with resize grips in its four corners.
pub fn window_with_corner_grips(ui: &mut UiRoot) -> (ElementId, [ElementId; 4]) {
    use ac_ui::widgets::{Border, ResizeGrip};
    let r = ui.root();
    let w = ui.add(
        r,
        Element {
            draggable: true,
            resizable: true,
            min_width: 40.0,
            min_height: 40.0,
            max_width: 2000.0,
            max_height: 2000.0,
            ..Element::at(100.0, 100.0, 200.0, 100.0)
        },
        Panel::default(),
    );
    let grip = |ui: &mut UiRoot, border: Border, x: f32, y: f32| {
        ui.add(
            w,
            Element::at(x, y, 5.0, 5.0),
            ResizeGrip { border, sprite: 0 },
        )
    };
    let ul = grip(ui, Border::UpperLeft, 0.0, 0.0);
    let ur = grip(ui, Border::UpperRight, 195.0, 0.0);
    let ll = grip(ui, Border::LowerLeft, 0.0, 95.0);
    let lr = grip(ui, Border::LowerRight, 195.0, 95.0);
    (w, [ul, ur, ll, lr])
}

pub const ALL_EDGES: ResizeEdges = ResizeEdges::ALL;
pub const NO_ANCHORS: AnchorEdges = AnchorEdges::NONE;
