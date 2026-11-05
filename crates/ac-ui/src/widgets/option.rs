//! Option-page widgets: the toggle slider (an on/off button paired with a
//! value slider) and the 64-bit checkbox bitfield (one checkbox row per
//! mask, the chat windows' text-type filters).

use crate::event::{UiEvent, kind};
use crate::root::{ElementMessage, UiRoot};
use crate::tree::{ElementId, Widget};
use crate::widgets::{Button, Scrollbar};

/// A toggle and a slider found among the element's children.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct OptionToggleSlider {
    /// The first button child.
    pub toggle: Option<ElementId>,
    /// The first scrollbar child.
    pub slider: Option<ElementId>,
}

impl Widget for OptionToggleSlider {
    fn on_children_attached(&mut self, ui: &mut UiRoot, me: ElementId) {
        for child in ui.children(me) {
            if self.toggle.is_none() && ui.widget::<Button>(child).is_some() {
                self.toggle = Some(child);
            } else if self.slider.is_none() && ui.widget::<Scrollbar>(child).is_some() {
                self.slider = Some(child);
            }
        }
    }
}

/// The checkbox inside a bitfield row template.
pub const TEMPLATE_CHECKBOX_ELEMENT_ID: u32 = 0x1000_0219;

/// One bitfield row.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BitfieldRow {
    /// Low mask.
    pub low_mask: u64,
    /// High mask.
    pub high_mask: u64,
    /// Caption.
    pub label: String,
    /// Tooltip.
    pub tooltip: Option<String>,
    /// The row's root element.
    pub row_root: ElementId,
    /// The row's checkbox.
    pub toggle: ElementId,
}

/// A block of mask checkboxes over a 128-bit value (two `u64`s).
#[derive(Debug, Clone, Default)]
pub struct Bitfield64 {
    /// The authored row templates.
    pub templates: Vec<crate::element_info::TemplateEntry>,
    /// LED image of a row whose whole mask is set.
    pub checked_led_sprite: u32,
    /// LED image of a row only partly set.
    pub unchecked_led_sprite: u32,
    rows: Vec<BitfieldRow>,
    content_height: f32,
    current_low: u64,
    current_high: u64,
    default_low: u64,
    default_high: u64,
}

impl Bitfield64 {
    /// The rows.
    #[must_use]
    pub fn rows(&self) -> &[BitfieldRow] {
        &self.rows
    }

    /// The value, low and high.
    #[must_use]
    pub fn current(&self) -> (u64, u64) {
        (self.current_low, self.current_high)
    }

    /// The default value (also the value while there are no rows).
    pub fn set_default_value(&mut self, low: u64, high: u64) {
        self.default_low = low;
        self.default_high = high;
        if self.rows.is_empty() {
            self.current_low = low;
            self.current_high = high;
        }
    }

    /// Back to the default value (reported).
    pub fn restore_default_value(&mut self, ui: &mut UiRoot, me: ElementId) {
        self.current_low = self.default_low;
        self.current_high = self.default_high;
        self.refresh_row_visuals(ui);
        ui.send_message(
            me,
            ElementMessage::BitfieldChanged {
                low: self.current_low,
                high: self.current_high,
            },
        );
    }

    /// Set the value (not reported).
    pub fn set_current_value(&mut self, ui: &mut UiRoot, low: u64, high: u64) {
        self.current_low = low;
        self.current_high = high;
        self.refresh_row_visuals(ui);
    }

    /// Add a row built from the row template: its checkbox gets the label
    /// and tooltip, the row stacks under the others. `None` (and nothing
    /// added) when the row has no checkbox.
    #[allow(clippy::too_many_arguments)]
    pub fn add_row(
        &mut self,
        ui: &mut UiRoot,
        me: ElementId,
        row_root: ElementId,
        low_mask: u64,
        high_mask: u64,
        label: &str,
        tooltip: Option<&str>,
    ) -> Option<ElementId> {
        let checkbox = ui
            .find_descendant(row_root, TEMPLATE_CHECKBOX_ELEMENT_ID)
            .filter(|c| ui.widget::<Button>(*c).is_some())?;
        if let Some(b) = ui.widget_mut::<Button>(checkbox) {
            b.label = Some(label.to_owned());
            b.tooltip_text = tooltip.map(str::to_owned);
        }
        let row_h = ui.element(row_root).map_or(0.0, |e| e.height);
        if let Some(e) = ui.element_mut(row_root) {
            e.left = 0.0;
            e.top = self.content_height;
        }
        self.content_height += row_h;
        ui.add_child(me, row_root);
        if let Some(e) = ui.element_mut(me) {
            e.height = self.content_height;
        }
        let row = BitfieldRow {
            low_mask,
            high_mask,
            label: label.to_owned(),
            tooltip: tooltip.map(str::to_owned),
            row_root,
            toggle: checkbox,
        };
        self.apply_row_visuals(ui, low_mask, high_mask, checkbox);
        self.rows.push(row);
        Some(checkbox)
    }

    /// True when any bit of the masks is set (a row is checked).
    #[must_use]
    pub fn is_any_set(&self, low_mask: u64, high_mask: u64) -> bool {
        self.current_low & low_mask != 0 || self.current_high & high_mask != 0
    }

    /// True when every bit of the masks is set.
    #[must_use]
    pub fn is_all_set(&self, low_mask: u64, high_mask: u64) -> bool {
        self.current_low & low_mask == low_mask && self.current_high & high_mask == high_mask
    }

    fn toggle_row(&mut self, ui: &mut UiRoot, me: ElementId, i: usize) {
        let (low, high, toggle) = (
            self.rows[i].low_mask,
            self.rows[i].high_mask,
            self.rows[i].toggle,
        );
        if self.is_any_set(low, high) {
            self.current_low &= !low;
            self.current_high &= !high;
        } else {
            self.current_low |= low;
            self.current_high |= high;
        }
        self.apply_row_visuals(ui, low, high, toggle);
        ui.send_message(
            me,
            ElementMessage::BitfieldChanged {
                low: self.current_low,
                high: self.current_high,
            },
        );
    }

    fn refresh_row_visuals(&self, ui: &mut UiRoot) {
        for r in &self.rows {
            self.apply_row_visuals(ui, r.low_mask, r.high_mask, r.toggle);
        }
    }

    fn apply_row_visuals(&self, ui: &mut UiRoot, low: u64, high: u64, toggle: ElementId) {
        let any = self.is_any_set(low, high);
        let led = if self.is_all_set(low, high) {
            self.checked_led_sprite
        } else {
            self.unchecked_led_sprite
        };
        ui.with::<Button, _>(toggle, |b, ui| {
            b.set_selected(ui, toggle, any);
            b.face_file_override = (any && led != 0).then_some(led);
        });
    }
}

impl Widget for Bitfield64 {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.kind == kind::CLICK
            && let Some(i) = self
                .rows
                .iter()
                .position(|r| ui.is_within(e.target, r.toggle))
        {
            self.toggle_row(ui, me, i);
            return true;
        }
        ui.dispatch_to_pointer_region(me, e)
    }
}
