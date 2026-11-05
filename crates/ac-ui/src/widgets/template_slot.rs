//! A list cell built from an authored row template: the template's
//! imported content fills the cell, a click on it is reported, and
//! selection switches the content root between two dat states (Normal and
//! Highlight for the effects rows). Also a plain image element (a runtime
//! texture stretched over the element), which row templates use for icons.

use crate::draw::{Color, RenderContext, TextureRef};
use crate::event::{UiEvent, kind};
use crate::geometry::AnchorEdges;
use crate::root::{ElementMessage, UiRoot};
use crate::tree::{Element, ElementId, Widget};

/// A row-template cell. See the module documentation.
#[derive(Debug, Clone)]
pub struct TemplateSlot {
    /// The catalog entry the row stands for (a spell id).
    pub entry_id: u32,
    /// The imported template content (the cell's only child).
    pub content: ElementId,
    /// The row is selected.
    pub selected: bool,
    normal_state: u32,
    selected_state: Option<u32>,
    applied: Option<bool>,
}

impl TemplateSlot {
    /// Wrap `content` (a detached imported row) in a new cell the size of
    /// the content, its content at the origin filling it. The cell comes
    /// back detached.
    pub fn create(
        ui: &mut UiRoot,
        content: ElementId,
        entry_id: u32,
        normal_state: u32,
        selected_state: Option<u32>,
    ) -> ElementId {
        let (w, h) = ui
            .element(content)
            .map_or((0.0, 0.0), |e| (e.width, e.height));
        let cell = ui.insert(
            Element::at(0.0, 0.0, w, h),
            Self {
                entry_id,
                content,
                selected: false,
                normal_state,
                selected_state,
                applied: None,
            },
        );
        if let Some(e) = ui.element_mut(content) {
            e.left = 0.0;
            e.top = 0.0;
        }
        ui.add_child(cell, content);
        ui.set_anchors(content, AnchorEdges::ALL);
        cell
    }

    /// Select or deselect the row (its content's state follows at once).
    pub fn set_selected(ui: &mut UiRoot, cell: ElementId, selected: bool) {
        let content = ui.with::<Self, _>(cell, |s, _| {
            s.selected = selected;
            s.content
        });
        if content.is_some() {
            Self::apply_selection_state(ui, cell);
        }
    }

    fn apply_selection_state(ui: &mut UiRoot, cell: ElementId) {
        let Some((content, state)) = ui.widget_mut::<Self>(cell).and_then(|s| {
            if s.applied == Some(s.selected) {
                return None;
            }
            s.applied = Some(s.selected);
            let state = match (s.selected, s.selected_state) {
                (true, Some(state)) => state,
                _ => s.normal_state,
            };
            Some((s.content, state))
        }) else {
            return;
        };
        ui.set_retail_state(content, state);
    }
}

impl Widget for TemplateSlot {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        match e.kind {
            kind::MOUSE_DOWN => true,
            kind::CLICK => {
                ui.send_message(
                    me,
                    ElementMessage::Clicked {
                        x: e.data1,
                        y: e.data2,
                    },
                );
                true
            }
            _ => false,
        }
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, _rc: &mut RenderContext<'_>) {
        // The content's state is settled before it draws (OpenAC applies it
        // in the slot's draw).
        if self.applied != Some(self.selected) {
            self.applied = Some(self.selected);
            let state = match (self.selected, self.selected_state) {
                (true, Some(state)) => state,
                _ => self.normal_state,
            };
            ui.set_retail_state(self.content, state);
        }
        let _ = me;
    }
}

/// A runtime texture stretched over the element; it takes no input.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct Image {
    /// The texture (none: nothing is drawn).
    pub texture: Option<TextureRef>,
}

impl Image {
    /// The element flags an image starts with: it takes no input.
    pub fn configure(element: &mut Element) {
        element.click_through = true;
    }
}

impl Widget for Image {
    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (Some(texture), Some((w, h))) =
            (self.texture, ui.element(me).map(|e| (e.width, e.height)))
        else {
            return;
        };
        rc.sprite(texture, 0.0, 0.0, w, h, (0.0, 0.0, 1.0, 1.0), Color::WHITE);
    }

    fn hit_test(&self, _element: &Element, _x: f32, _y: f32) -> bool {
        false
    }
}
