//! Tooltips: the authored popup an element names, filled with its text,
//! sized to it and placed by the pointer; and the hover tooltip over world
//! objects.

use crate::geometry::AnchorEdges;
use crate::import::ImportedLayout;
use crate::root::{UiOutput, UiRoot};
use crate::text::{Line, char_len, wrap_words};
use crate::tree::ElementId;
use crate::widgets::text::{Text, TextContent};

/// Builds a popup: layout id and root element id → an imported subtree.
pub type PopupFactory = Box<dyn FnMut(&mut UiRoot, u32, u32) -> Option<ImportedLayout>>;

/// The pointer offset of a tooltip, pixels on both axes.
pub const MOUSE_OFFSET: f32 = 32.0;
/// The shared popup skin world tooltips use: root element.
pub const SHARED_POPUP_SKIN_ROOT_ELEMENT_ID: u32 = 0x1000_0395;
/// The shared popup skin world tooltips use: layout.
pub const SHARED_POPUP_SKIN_LAYOUT_DID: u32 = 0x2100_0041;

/// What the world reports about the object under the pointer.
pub trait WorldHover {
    /// The object under the pointer (none over no object).
    fn hovered(&self) -> Option<u32>;
    /// The name to show for an object.
    fn name(&self, guid: u32) -> Option<String>;
    /// Whether world tooltips are switched on.
    fn tooltips_enabled(&self) -> bool;
}

/// Shows element and world tooltips.
pub struct TooltipPresenter {
    create: PopupFactory,
    popup: Option<ElementId>,
    owner: Option<ElementId>,
    /// Tooltips are shown at all (the user setting).
    pub enabled: bool,
    world_hover_guid: u32,
    world_showing: bool,
    world_staged: Option<String>,
    world_shown_ms: i64,
    world_rearm_needs_move: bool,
    world_last_mouse: (i32, i32),
}

impl std::fmt::Debug for TooltipPresenter {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("TooltipPresenter")
            .field("popup", &self.popup)
            .field("owner", &self.owner)
            .field("enabled", &self.enabled)
            .finish_non_exhaustive()
    }
}

impl TooltipPresenter {
    /// A presenter building popups with `create`.
    pub fn new(create: PopupFactory) -> Self {
        Self {
            create,
            popup: None,
            owner: None,
            enabled: true,
            world_hover_guid: 0,
            world_showing: false,
            world_staged: None,
            world_shown_ms: 0,
            world_rearm_needs_move: false,
            world_last_mouse: (i32::MIN, i32::MIN),
        }
    }

    /// The mounted popup.
    #[must_use]
    pub fn popup(&self) -> Option<ElementId> {
        self.popup
    }

    /// React to the root's tooltip outputs; other outputs are ignored.
    pub fn handle(&mut self, ui: &mut UiRoot, output: &UiOutput) {
        match output {
            UiOutput::TooltipShow(widget) => self.on_show(ui, *widget),
            UiOutput::TooltipHide(widget) if self.owner == Some(*widget) => {
                self.remove_popup(ui);
            }
            _ => {}
        }
    }

    fn on_show(&mut self, ui: &mut UiRoot, widget: ElementId) {
        self.remove_popup(ui);
        if !self.enabled {
            return;
        }
        let Some(element) = ui.element(widget) else {
            return;
        };
        let runtime = ui
            .widget_dyn(widget)
            .and_then(|w| w.tooltip_text(element))
            .filter(|t| !t.is_empty());
        let from_runtime = runtime.is_some();
        let Some(text) = runtime.or_else(|| element.authored.tooltip_text.clone()) else {
            return;
        };
        if text.is_empty() {
            return;
        }
        if !from_runtime && !element.authored.tooltip_enabled {
            return;
        }
        let root_id = element.authored.tooltip_root_element_id;
        if root_id == 0 {
            return;
        }
        let layout = if element.authored.tooltip_layout_did != 0 {
            element.authored.tooltip_layout_did
        } else {
            element.source_layout_did
        };
        if layout == 0 {
            return;
        }
        if self.build_and_mount(ui, root_id, layout, &text) {
            self.owner = Some(widget);
        }
    }

    fn build_and_mount(&mut self, ui: &mut UiRoot, root_id: u32, layout: u32, text: &str) -> bool {
        self.remove_popup(ui);
        let Some(imported) = (self.create)(ui, layout, root_id) else {
            return false;
        };
        let root = imported.root;
        let text_child_id = ui
            .element(root)
            .map_or(0, |e| e.authored.tooltip_text_child_element_id);
        let text_child = (text_child_id != 0)
            .then(|| imported.find(text_child_id))
            .flatten()
            .filter(|t| ui.widget::<Text>(*t).is_some());
        let Some(text_child) = text_child else {
            ui.destroy(root);
            return false;
        };
        for id in [root, text_child] {
            if let Some(e) = ui.element_mut(id) {
                e.set_anchors(AnchorEdges::NONE);
            }
        }
        apply_tooltip_text(ui, root, text_child, text);
        set_click_through_recursive(ui, root);
        self.position_at_mouse(ui, root);
        let parent = ui.root();
        ui.add_child(parent, root);
        ui.bring_to_front(root);
        self.popup = Some(root);
        true
    }

    fn remove_popup(&mut self, ui: &mut UiRoot) {
        let Some(popup) = self.popup.take() else {
            return;
        };
        ui.destroy(popup);
        self.owner = None;
        self.world_showing = false;
    }

    fn position_at_mouse(&self, ui: &mut UiRoot, root: ElementId) {
        let (cw, ch) = ui.effective_canvas_size();
        let (mx, my) = ui.mouse();
        let Some(e) = ui.element_mut(root) else {
            return;
        };
        e.left = crate::geometry::clamp(mx as f32 + MOUSE_OFFSET, 0.0, (cw - e.width).max(0.0));
        e.top = crate::geometry::clamp(my as f32 + MOUSE_OFFSET, 0.0, (ch - e.height).max(0.0));
    }

    /// Hide whatever is showing and forget the world hover.
    pub fn hide_current(&mut self, ui: &mut UiRoot) {
        self.remove_popup(ui);
        self.world_hover_guid = 0;
        self.world_staged = None;
        self.world_rearm_needs_move = false;
    }

    /// Per frame: keep the popup on top and run the world hover tooltip.
    pub fn tick(&mut self, ui: &mut UiRoot, world: Option<&dyn WorldHover>) {
        if let Some(p) = self.popup {
            ui.bring_to_front(p);
        }
        if let Some(world) = world {
            self.update_world_hover(ui, world);
        }
    }

    fn update_world_hover(&mut self, ui: &mut UiRoot, world: &dyn WorldHover) {
        let mouse = ui.mouse();
        if mouse != self.world_last_mouse {
            self.world_last_mouse = mouse;
            self.world_rearm_needs_move = false;
        }
        let found = if ui.pick(mouse.0, mouse.1).is_none() {
            world.hovered().unwrap_or(0)
        } else {
            0
        };
        if found != self.world_hover_guid {
            self.world_hover_guid = found;
            let mut staged = self.world_staged.clone();
            if found == 0 || !world.tooltips_enabled() {
                staged = None;
            } else if let Some(name) = world.name(found).filter(|n| !n.is_empty()) {
                staged = Some(name);
            }
            if staged != self.world_staged {
                self.world_staged = staged.clone();
                if self.world_showing {
                    self.remove_popup(ui);
                }
                if let Some(text) = staged
                    && ui.drag_source().is_some()
                {
                    if self.build_and_mount(
                        ui,
                        SHARED_POPUP_SKIN_ROOT_ELEMENT_ID,
                        SHARED_POPUP_SKIN_LAYOUT_DID,
                        &text,
                    ) {
                        self.world_showing = true;
                        self.world_shown_ms = ui.now_ms();
                    }
                    return;
                }
            }
        }
        if self.world_showing {
            if ui.now_ms() - self.world_shown_ms >= ui.tooltip_duration_ms {
                self.remove_popup(ui);
                self.world_rearm_needs_move = true;
            }
            return;
        }
        let Some(text) = self.world_staged.clone() else {
            return;
        };
        if ui.captured().is_some()
            || self.world_rearm_needs_move
            || ui.mouse_idle_ms() < ui.tooltip_delay_ms
            || !self.enabled
        {
            return;
        }
        if self.build_and_mount(
            ui,
            SHARED_POPUP_SKIN_ROOT_ELEMENT_ID,
            SHARED_POPUP_SKIN_LAYOUT_DID,
            &text,
        ) {
            self.world_showing = true;
            self.world_shown_ms = ui.now_ms();
        }
    }
}

fn set_click_through_recursive(ui: &mut UiRoot, id: ElementId) {
    if let Some(e) = ui.element_mut(id) {
        e.click_through = true;
    }
    for child in ui.children(id) {
        set_click_through_recursive(ui, child);
    }
}

/// Fill the popup's text element with `text` and size the popup to it: wrap
/// to the authored maximum width (or the canvas), grow the root by how much
/// the measured text differs from the authored text box, clamp to the
/// authored limits, then re-wrap at the final width and grow for any extra
/// lines.
fn apply_tooltip_text(ui: &mut UiRoot, root: ElementId, text_id: ElementId, tooltip: &str) {
    let canvas_w = ui.effective_canvas_size().0;
    let Some(t) = ui.element(text_id) else {
        return;
    };
    let (authored_tw, authored_th) = (t.width, t.height);
    let text_max_width = t.authored.resize_max_width;
    let Some(r) = ui.element(root) else {
        return;
    };
    let (root_w, root_h) = (r.width, r.height);
    let root_limits = (
        r.authored.resize_max_width,
        r.authored.resize_min_width,
        r.authored.resize_max_height,
        r.authored.resize_min_height,
    );
    let Some(widget) = ui.widget_mut::<Text>(text_id) else {
        return;
    };
    widget.padding = 0.0;
    let font = widget.font.clone();
    let measure = |s: &str| match font.as_deref() {
        Some(f) => f.measure_width(s),
        None => char_len(s) as f32 * 8.0,
    };
    let line_height = font.as_deref().map_or(14.0, |f| f.line_height);
    let margins_x = widget.margin_left + widget.margin_right;
    let margins_y = widget.margin_top + widget.margin_bottom;

    let wrap_bound = match text_max_width {
        Some(m) => (m as f32).max(1.0),
        None => canvas_w.max(1.0),
    };
    let measured = wrap_words(tooltip, measure, (wrap_bound - margins_x).max(1.0));
    let measured_w = measured.iter().map(|l| measure(l)).fold(0.0, f32::max) + margins_x;
    let measured_h = measured.len() as f32 * line_height + margins_y;

    let mut requested_w = root_w + (measured_w - authored_tw);
    let mut requested_h = root_h + (measured_h - authored_th);
    let (max_w, min_w, max_h, min_h) = root_limits;
    if let Some(m) = max_h
        && requested_h > m as f32
    {
        requested_h = m as f32;
    }
    if let Some(m) = min_h
        && requested_h < m as f32
    {
        requested_h = m as f32;
    }
    if let Some(m) = max_w
        && requested_w > m as f32
    {
        requested_w = m as f32;
    }
    if let Some(m) = min_w
        && requested_w < m as f32
    {
        requested_w = m as f32;
    }

    let mut text_final_w = (authored_tw + (requested_w - root_w)).max(1.0);
    if let Some(m) = text_max_width {
        text_final_w = text_final_w.min(m as f32);
    }
    let text_final_h = authored_th + (requested_h - root_h);
    let wrapped = wrap_words(tooltip, measure, (text_final_w - margins_x).max(1.0));
    let color = widget.default_color;
    widget.set_content(TextContent::Lines(
        wrapped
            .iter()
            .map(|l| Line::new(l.clone(), color))
            .collect(),
    ));
    let text_w = if wrapped.is_empty() {
        0.0
    } else {
        text_final_w.min(wrapped.iter().map(|l| measure(l)).fold(0.0, f32::max) + margins_x)
    };
    let rewrapped_h = wrapped.len() as f32 * line_height + margins_y;

    let mut final_root_h = requested_h;
    if rewrapped_h > text_final_h {
        let mut grown = requested_h + (rewrapped_h - text_final_h);
        if let Some(m) = max_h
            && grown > m as f32
        {
            grown = m as f32;
        }
        if let Some(m) = min_h
            && grown < m as f32
        {
            grown = m as f32;
        }
        final_root_h = grown;
    }
    if let Some(r) = ui.element_mut(root) {
        r.width = requested_w;
        r.height = final_root_h;
    }
    if let Some(t) = ui.element_mut(text_id) {
        t.width = text_w;
        t.height = rewrapped_h;
    }
}
