//! The catalog slot: a cell that shows an entry of a catalog rather than
//! an object (a spell in the spellbook or on a favourite bar, the wielded
//! caster's spell): its icon and overlay, an optional label beside it, the
//! row and selection art, the shortcut digit, and what a drag of it
//! carries.
//!
//! Like the item slot it tells its owner what happened with element
//! messages: a press ([`ElementMessage::Pressed`]), clicks, a right click
//! (examine), a drag lifted from it ([`ElementMessage::CatalogDragLifted`])
//! and a drop on it ([`ElementMessage::CatalogDropped`]). Whether a drag
//! hovering it would be taken is answered by its own `drag_accepts` rule.

use std::sync::Arc;

use crate::draw::{Color, RenderContext, TextureRef};
use crate::event::{DragAcceptance, DragPayload, UiEvent, kind};
use crate::root::{ElementMessage, UiRoot};
use crate::tooltip::{SHARED_POPUP_SKIN_LAYOUT_DID, SHARED_POPUP_SKIN_ROOT_ELEMENT_ID};
use crate::tree::{DragGhost, Element, ElementId, Widget};
use crate::widgets::ShortcutDigits;
use crate::widgets::item_slot::{
    DEFAULT_DRAG_ACCEPT_SPRITE, DEFAULT_DRAG_REJECT_SPRITE, DEFAULT_EMPTY_SPRITE, stretch,
};

/// Decides how a drag hovering a catalog slot would be received.
pub type CatalogDragRule = fn(&DragPayload) -> DragAcceptance;

/// A catalog slot. See the module documentation.
#[derive(Debug, Clone)]
pub struct CatalogSlot {
    /// The entry (a spell id; 0: empty).
    pub entry_id: u32,
    /// The entry's icon.
    pub icon: Option<TextureRef>,
    /// An icon drawn over it (the caster of an endowment).
    pub overlay: Option<TextureRef>,
    /// The label (also the hover caption).
    pub label: String,
    /// Draw the label beside the icon.
    pub show_label: bool,
    /// The row art.
    pub background_sprite: u32,
    /// The selection art.
    pub selected_sprite: u32,
    /// The entry is selected.
    pub selected: bool,
    /// The selection draws under the icon and label (spellbook rows).
    pub selection_behind_content: bool,
    /// The label's font.
    pub label_font: Option<Arc<crate::UiFont>>,
    /// The label's colour.
    pub label_color: Color,
    /// Icon left, pixels.
    pub icon_left: f32,
    /// Icon top, pixels.
    pub icon_top: f32,
    /// Icon width (0: 32, or the cell without a label).
    pub icon_width: f32,
    /// Icon height (0: as the width).
    pub icon_height: f32,
    /// Label left (0: after the icon).
    pub label_left: f32,
    /// Label width (0: the rest of the row).
    pub label_width: f32,
    /// The image of an empty cell.
    pub empty_sprite: u32,
    /// What a drag of the entry carries (`None`: not draggable).
    pub drag_payload: Option<DragPayload>,
    /// Whether a drag hovering the slot would be taken.
    pub drag_accepts: Option<CatalogDragRule>,
    /// The shortcut number images.
    pub digits: Option<Arc<ShortcutDigits>>,
    /// The shortcut number stamped (-1: none).
    pub shortcut_num: i32,
    /// The drag answer showing (kept by the slot as drags pass over it).
    pub drag_accept: DragAcceptance,
}

impl Default for CatalogSlot {
    fn default() -> Self {
        Self {
            entry_id: 0,
            icon: None,
            overlay: None,
            label: String::new(),
            show_label: false,
            background_sprite: 0,
            selected_sprite: crate::widgets::item_slot::DEFAULT_SELECTED_SPRITE,
            selected: false,
            selection_behind_content: false,
            label_font: None,
            label_color: Color::rgba(0.92, 0.88, 0.70, 1.0),
            icon_left: 0.0,
            icon_top: 0.0,
            icon_width: 0.0,
            icon_height: 0.0,
            label_left: 0.0,
            label_width: 0.0,
            empty_sprite: DEFAULT_EMPTY_SPRITE,
            drag_payload: None,
            drag_accepts: None,
            digits: None,
            shortcut_num: -1,
            drag_accept: DragAcceptance::None,
        }
    }
}

/// The element a fresh catalog slot sits in (tooltips in the shared skin).
#[must_use]
pub fn catalog_element(w: f32, h: f32) -> Element {
    let mut e = Element::at(0.0, 0.0, w, h);
    e.authored.tooltip_root_element_id = SHARED_POPUP_SKIN_ROOT_ELEMENT_ID;
    e.authored.tooltip_layout_did = SHARED_POPUP_SKIN_LAYOUT_DID;
    e
}

/// `text` cut with "..." to fit `width` pixels.
#[must_use]
pub fn fit_text(text: &str, width: f32, measure: impl Fn(&str) -> f32) -> String {
    if width <= 0.0 || text.is_empty() || measure(text) <= width {
        return text.to_owned();
    }
    let ellipsis = "...";
    let ellipsis_width = measure(ellipsis);
    if ellipsis_width >= width {
        return String::new();
    }
    let chars: Vec<char> = text.chars().collect();
    let (mut low, mut high) = (0, chars.len());
    while low < high {
        let mid = (low + high).div_ceil(2);
        let head: String = chars[..mid].iter().collect();
        if measure(&head) + ellipsis_width <= width {
            low = mid;
        } else {
            high = mid - 1;
        }
    }
    let head: String = chars[..low].iter().collect();
    head + ellipsis
}

impl CatalogSlot {
    /// The drag answer showing.
    #[must_use]
    pub fn drag_acceptance(&self) -> DragAcceptance {
        self.drag_accept
    }

    /// The digit set the number draws from: regular for an entry, the
    /// empty digits otherwise.
    #[must_use]
    pub fn active_digits(&self) -> Option<&[u32]> {
        let d = self.digits.as_deref()?;
        if self.entry_id != 0 {
            Some(&d.regular)
        } else {
            d.empty.as_deref()
        }
    }

    fn icon_size(&self, w: f32, h: f32) -> (f32, f32) {
        let iw = if self.icon_width > 0.0 {
            self.icon_width
        } else if self.show_label {
            h.min(32.0)
        } else {
            w
        };
        let ih = if self.icon_height > 0.0 {
            self.icon_height
        } else if self.show_label {
            h.min(32.0)
        } else {
            h
        };
        (iw, ih)
    }

    fn draw_selection(&self, rc: &mut RenderContext<'_>, w: f32, h: f32) {
        if self.selected {
            stretch(rc, self.selected_sprite, 0.0, 0.0, w, h);
        }
    }
}

impl Widget for CatalogSlot {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.target != Some(me) {
            return false;
        }
        match e.kind {
            kind::MOUSE_DOWN => {
                if self.entry_id != 0 {
                    ui.send_message(me, ElementMessage::Pressed);
                }
                true
            }
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
            kind::DOUBLE_CLICK => {
                ui.send_message(me, ElementMessage::DoubleClicked);
                true
            }
            kind::RIGHT_CLICK => {
                if self.entry_id == 0 {
                    return false;
                }
                ui.send_message(me, ElementMessage::RightClicked);
                true
            }
            kind::DRAG_BEGIN => {
                if let Some(p) = &e.payload {
                    ui.send_message(me, ElementMessage::CatalogDragLifted(p.clone()));
                }
                true
            }
            kind::DRAG_ENTER => {
                self.drag_accept = match (&e.payload, self.drag_accepts) {
                    (Some(p), Some(rule)) => rule(p),
                    _ => DragAcceptance::None,
                };
                true
            }
            kind::DRAG_OVER => {
                self.drag_accept = DragAcceptance::None;
                true
            }
            kind::DROP_RELEASED => {
                self.drag_accept = DragAcceptance::None;
                if let Some(p) = &e.payload {
                    ui.send_message(
                        me,
                        ElementMessage::CatalogDropped {
                            payload: p.clone(),
                            x: e.data1,
                            y: e.data2,
                        },
                    );
                }
                true
            }
            _ => false,
        }
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        stretch(rc, self.background_sprite, 0.0, 0.0, w, h);
        if self.selection_behind_content {
            self.draw_selection(rc, w, h);
        }
        let (iw, ih) = self.icon_size(w, h);
        match self.icon {
            Some(t) => rc.sprite(
                t,
                self.icon_left,
                self.icon_top,
                iw,
                ih,
                (0.0, 0.0, 1.0, 1.0),
                Color::WHITE,
            ),
            None => stretch(rc, self.empty_sprite, self.icon_left, self.icon_top, iw, ih),
        }
        if let Some(t) = self.overlay {
            rc.sprite(
                t,
                self.icon_left,
                self.icon_top,
                iw,
                ih,
                (0.0, 0.0, 1.0, 1.0),
                Color::WHITE,
            );
        }
        if self.shortcut_num >= 0
            && let Some(d) = self
                .active_digits()
                .and_then(|d| d.get(self.shortcut_num as usize).copied())
        {
            stretch(rc, d, 0.0, 0.0, w, h);
        }
        if !self.selection_behind_content {
            self.draw_selection(rc, w, h);
        }
        if self.show_label
            && let Some(font) = self.label_font.clone()
        {
            let left = if self.label_left > 0.0 {
                self.label_left
            } else {
                self.icon_left + iw + 4.0
            };
            let width = if self.label_width > 0.0 {
                self.label_width
            } else {
                (w - left).max(0.0)
            };
            let text = fit_text(&self.label, width, |s| font.measure_width(s));
            let y = ((h - font.line_height) * 0.5).max(0.0);
            rc.string_dat(&font, &text, left, y, self.label_color, false, Color::BLACK);
        }
        match self.drag_accept {
            DragAcceptance::Accept => stretch(rc, DEFAULT_DRAG_ACCEPT_SPRITE, 0.0, 0.0, w, h),
            DragAcceptance::Reject => stretch(rc, DEFAULT_DRAG_REJECT_SPRITE, 0.0, 0.0, w, h),
            DragAcceptance::None => {}
        }
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn is_drag_source(&self, _element: &Element) -> bool {
        self.drag_payload.is_some()
    }

    fn drag_payload(&mut self, _ui: &mut UiRoot, _me: ElementId) -> Option<DragPayload> {
        self.drag_payload.clone()
    }

    fn drag_ghost(&mut self, _ui: &mut UiRoot, _me: ElementId) -> Option<DragGhost> {
        self.drag_payload.as_ref()?;
        self.icon.map(|t| DragGhost {
            texture: t,
            width: 32,
            height: 32,
        })
    }

    fn tooltip_text(&self, _element: &Element) -> Option<String> {
        (!self.label.trim().is_empty()).then(|| self.label.clone())
    }

    fn consumes_dat_children(&self) -> bool {
        true
    }

    fn is_empty_slot(&self) -> bool {
        self.entry_id == 0
    }
}
