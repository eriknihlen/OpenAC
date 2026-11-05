//! The widgets: the generic imported element, buttons, text, text fields,
//! scrollbars, meters, menus, list boxes, item lists, tab panels, 3D
//! viewports, the radar, option widgets, panels, resize grips, the vitals
//! detail root and the dialog root.

pub mod button;
pub mod catalog_slot;
pub mod chrome;
pub mod dat_element;
pub mod field;
pub mod frame;
pub mod item_list;
pub mod item_slot;
pub mod list_box;
pub mod menu;
pub mod meter;
pub mod option;
pub mod panel;
pub mod radar;
pub mod resize_grip;
pub mod scrollbar;
pub mod tab_panel;
pub mod template_slot;
pub mod text;
pub mod viewport;

use crate::draw::RenderContext;
use crate::event::{Key, UiEvent, kind};
use crate::root::{ElementMessage, UiRoot};
use crate::states;
use crate::tree::{Element, ElementId, Widget};

pub use button::{Button, ButtonListeners, LabelAlign};
pub use catalog_slot::CatalogSlot;
pub use dat_element::DatElement;
pub use field::Field;
pub use frame::NineSliceFrame;
pub use item_list::{ItemList, ItemListFlow};
pub use item_slot::{ItemCell, ItemSlot, ShortcutDigits};
pub use list_box::{ListBox, ScrollablePanel};
pub use menu::{Menu, MenuItem};
pub use meter::{DetailOverlay, Meter, MeterLabelAlign};
pub use option::{Bitfield64, OptionToggleSlider};
pub use panel::Panel;
pub use radar::Radar;
pub use resize_grip::{Border, ResizeGrip};
pub use scrollbar::{ScrollSource, Scrollbar};
pub use tab_panel::TabPanel;
pub use template_slot::{Image, TemplateSlot};
pub use text::{Text, TextContent};
pub use viewport::Viewport;

/// Which dat children the importer builds under an element.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum ChildImport {
    /// Every child.
    #[default]
    All,
    /// None (the widget draws them itself).
    None,
    /// Every child but containers (a meter's track pieces).
    NonContainers,
    /// Only children with images (decoration around text).
    MediaBearing,
    /// Only children that are buttons (a button draws the rest itself).
    Buttons,
}

/// The vitals window root: a press anywhere but its move strip or resize
/// grips toggles between the compact and the detailed view.
#[derive(Debug, Clone)]
pub struct VitalsRoot {
    /// The element itself.
    pub inner: DatElement,
}

impl VitalsRoot {
    /// A vitals root over `inner`.
    #[must_use]
    pub fn new(inner: DatElement) -> Self {
        Self { inner }
    }
}

fn press_consumed_by_chrome(ui: &UiRoot, me: ElementId, target: Option<ElementId>) -> bool {
    let mut cur = target;
    while let Some(c) = cur {
        if c == me {
            break;
        }
        let Some(e) = ui.element(c) else {
            break;
        };
        let grip = ui
            .widget_dyn(c)
            .is_some_and(|w| w.resize_grip_edges().is_some());
        if e.window_move_handle || grip {
            return true;
        }
        cur = ui.parent(c);
    }
    false
}

impl Widget for VitalsRoot {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if (e.kind == kind::MOUSE_DOWN || e.kind == kind::RIGHT_DOWN)
            && !press_consumed_by_chrome(ui, me, e.target)
        {
            let next = if self.inner.active_retail_state_id() == states::HIDE_DETAIL {
                states::SHOW_DETAIL
            } else {
                states::HIDE_DETAIL
            };
            self.inner.set_retail_state(ui, me, next);
        }
        self.inner.on_event(ui, me, e)
    }

    fn handles_click(&self, element: &Element) -> bool {
        self.inner.handles_click(element)
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        self.inner.draw(ui, me, rc);
    }

    fn active_cursor_state_name(&self) -> &str {
        self.inner.active_cursor_state_name()
    }

    fn retail_state(&self) -> Option<u32> {
        self.inner.retail_state()
    }

    fn set_retail_state(&mut self, ui: &mut UiRoot, me: ElementId, state: u32) -> bool {
        self.inner.set_retail_state(ui, me, state)
    }
}

/// A dialog's root: takes every event it is offered (nothing behind a
/// dialog reacts), and Escape asks to cancel ([`ElementMessage::Cancel`]).
#[derive(Debug, Clone, Copy, Default)]
pub struct DialogRoot;

impl Widget for DialogRoot {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.kind == kind::KEY_DOWN && e.key == Key::Escape {
            ui.send_message(me, ElementMessage::Cancel);
        }
        true
    }
}
