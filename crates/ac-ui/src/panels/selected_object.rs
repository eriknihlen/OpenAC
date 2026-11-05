//! The selected-object strip on the toolbar: the selected object's name on
//! up to two lines, the selection flash, its health bar (a creature whose
//! health the server streams) and an owned item's mana bar. The stack
//! split controls beside it are [`crate::panels::toolbar::StackSplitController`].
//!
//! The strip shows what the client hands [`SelectedObjectController::update`]
//! each frame: the items view (selection, objects, names) and the
//! selection's health and mana facts; each mana answer the server sends
//! comes through [`SelectedObjectController::on_item_mana`] (it shows the
//! bar or closes the query).

use std::sync::Arc;

use ac_dat::objects::DIRECT_STATE_ID;

use crate::UiFont;
use crate::draw::Color;
use crate::geometry::AnchorEdges;
use crate::import::ImportedLayout;
use crate::panels::PanelOutput;
use crate::panels::items::{ItemsView, emit};
use crate::root::UiRoot;
use crate::states;
use crate::text::Line;
use crate::tree::{Element, ElementId};
use crate::widgets::{Meter, Text};

/// The strip's container.
pub const CONTAINER_ID: u32 = 0x1000_019E;
/// The name element.
pub const NAME_ID: u32 = 0x1000_019F;
/// The selection flash (states ObjectSelected / StackedItemSelected).
pub const OVERLAY_ID: u32 = 0x1000_01A0;
/// The health bar.
pub const HEALTH_METER_ID: u32 = 0x1000_01A1;
/// The mana bar.
pub const MANA_METER_ID: u32 = 0x1000_01A2;
/// How long the selection flash shows, seconds.
pub const FLASH_SECONDS: f64 = 0.25;
/// The name floats over everything in the strip.
pub const NAME_Z_ORDER: i32 = 1_000_000;
/// The flash draws over the health bar, under the name.
pub const OVERLAY_Z_ORDER: i32 = NAME_Z_ORDER - 1;
/// The height of each name line, pixels.
pub const NAME_BAND_HEIGHT: f32 = 15.0;
/// The pyreal coin stack's weenie class.
pub const COINSTACK_WEENIE_CLASS: u32 = 273;
/// The player's coin total (property int `CoinValue`).
const COIN_VALUE_PROPERTY: u32 = 20;
/// The name's colour.
pub const NAME_COLOR: Color = Color::WHITE;

/// What the runtime knows about the selected object's health.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct SelectionHealth {
    /// The health stream follows it (a player, a pet or anything
    /// attackable).
    pub health_target: bool,
    /// Its health share, once the server sent one.
    pub percent: Option<f32>,
}

/// The facts the strip reads besides the items view.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct SelectionFacts {
    /// The selected object's health.
    pub health: SelectionHealth,
    /// The selected object's name when it is not in the item table (a
    /// creature or scenery from the world).
    pub world_name: Option<String>,
    /// The selected item's mana share, 0..=1, as the server last answered
    /// its mana query (0 when no answer is known): the mana bar's fill.
    pub mana: f32,
}

/// Split `name` onto two lines at the last space before it overflows
/// `width` (`font` measures; without a font, one line).
#[must_use]
pub fn wrap_name_two_lines(name: &str, width: f32, font: Option<&UiFont>) -> (String, String) {
    let Some(font) = font else {
        return (name.to_owned(), String::new());
    };
    if font.measure_width(name) <= width {
        return (name.to_owned(), String::new());
    }
    let mut break_at = None;
    for (i, c) in name.char_indices() {
        if c != ' ' {
            continue;
        }
        if font.measure_width(&name[..i]) <= width {
            break_at = Some(i);
        } else {
            break;
        }
    }
    match break_at {
        Some(i) if i > 0 => (name[..i].to_owned(), name[i + 1..].trim_start().to_owned()),
        _ => (name.to_owned(), String::new()),
    }
}

/// The strip's controller. See the module documentation.
#[derive(Debug)]
pub struct SelectedObjectController {
    name: Option<ElementId>,
    lines: [Option<ElementId>; 2],
    name_width: f32,
    font: Option<Arc<UiFont>>,
    overlay: Option<ElementId>,
    health: Option<ElementId>,
    mana: Option<ElementId>,
    current: Option<u32>,
    current_stack: u32,
    current_name: Option<String>,
    flash_remaining: f64,
    bound: bool,
}

fn stack_of(view: &ItemsView<'_>, id: u32) -> u32 {
    view.get(id)
        .map_or(0, |o| o.stack_size.max(0).unsigned_abs())
}

impl SelectedObjectController {
    /// Bind to the toolbar's strip; any element may be missing. The name
    /// gets two one-line texts in `font`, the bars start hidden.
    pub fn bind(ui: &mut UiRoot, layout: &ImportedLayout, font: Option<Arc<UiFont>>) -> Self {
        let name = layout.find(NAME_ID);
        let overlay = layout.find(OVERLAY_ID);
        let meter = |id| layout.find(id).filter(|m| ui.widget::<Meter>(*m).is_some());
        let health = meter(HEALTH_METER_ID);
        let mana = meter(MANA_METER_ID);
        if let Some(o) = overlay {
            ui.set_z_order(o, OVERLAY_Z_ORDER);
        }
        for m in [health, mana].into_iter().flatten() {
            ui.set_visible(m, false);
            if let Some(w) = ui.widget_mut::<Meter>(m) {
                w.fill = Some(0.0);
            }
        }
        let mut lines = [None, None];
        let mut name_width = 0.0;
        if let Some(n) = name {
            ui.set_z_order(n, NAME_Z_ORDER);
            name_width = ui.element(n).map_or(0.0, |e| e.width);
            for (i, slot) in lines.iter_mut().enumerate() {
                let mut e = Element::at(
                    0.0,
                    i as f32 * NAME_BAND_HEIGHT,
                    name_width,
                    NAME_BAND_HEIGHT,
                )
                .with_anchors(AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::RIGHT);
                Text::configure(&mut e);
                let mut t = Text::default();
                t.centered = true;
                t.one_line = true;
                t.font = font.clone();
                *slot = Some(ui.add(n, e, t));
            }
        }
        Self {
            name,
            lines,
            name_width,
            font,
            overlay,
            health,
            mana,
            current: None,
            current_stack: 0,
            current_name: None,
            flash_remaining: 0.0,
            bound: true,
        }
    }

    /// The selected object, as last applied.
    #[must_use]
    pub fn current(&self) -> Option<u32> {
        self.current
    }

    /// The name's two line elements.
    #[must_use]
    pub fn name_lines(&self) -> [Option<ElementId>; 2] {
        self.lines
    }

    /// The name as shown (the stack count and coin total composed in).
    #[must_use]
    pub fn current_name(&self) -> Option<&str> {
        self.current_name.as_deref()
    }

    fn set_overlay(&self, ui: &mut UiRoot, state: u32) {
        if let Some(o) = self.overlay {
            ui.set_retail_state(o, state);
        }
    }

    fn visible(ui: &UiRoot, id: Option<ElementId>) -> bool {
        id.and_then(|m| ui.element(m)).is_some_and(Element::visible)
    }

    fn show(ui: &mut UiRoot, id: Option<ElementId>, visible: bool) {
        if let Some(m) = id {
            ui.set_visible(m, visible);
        }
    }

    fn refresh_name(&mut self, view: &ItemsView<'_>, facts: &SelectionFacts, id: u32, stack: u32) {
        let object_name = match view.get(id) {
            Some(o) => Some(view.name_of(o)),
            None => facts.world_name.clone(),
        };
        let coinstack = view
            .get(id)
            .is_some_and(|o| o.weenie_class_id == COINSTACK_WEENIE_CLASS);
        let owned = view.objects.is_owned_by_object(id, view.player);
        self.current_name = if coinstack && owned {
            let total = view
                .get(view.player)
                .map_or(0, |p| p.properties.get_int(COIN_VALUE_PROPERTY, 0));
            Some(format!(
                "{stack} {} (of {total})",
                object_name.unwrap_or_default()
            ))
        } else if stack > 1 && object_name.as_deref().is_some_and(|n| !n.is_empty()) {
            Some(format!("{stack} {}", object_name.unwrap_or_default()))
        } else {
            object_name
        };
    }

    fn apply_selection(
        &mut self,
        ui: &mut UiRoot,
        view: &ItemsView<'_>,
        facts: &SelectionFacts,
        selected: Option<u32>,
    ) {
        let changed = self.current != selected;
        if changed && Self::visible(ui, self.mana) {
            // Close the open mana query.
            emit(ui, PanelOutput::QueryItemMana(0));
        }
        if changed {
            Self::show(ui, self.health, false);
            Self::show(ui, self.mana, false);
        }
        self.current_name = None;
        self.current = selected;
        let Some(id) = selected else {
            self.current_stack = 0;
            self.set_overlay(ui, DIRECT_STATE_ID);
            self.flash_remaining = 0.0;
            return;
        };
        let stack = stack_of(view, id);
        self.current_stack = stack;
        self.refresh_name(view, facts, id, stack);
        self.set_overlay(
            ui,
            if stack > 1 {
                states::STACKED_ITEM_SELECTED
            } else {
                states::OBJECT_SELECTED
            },
        );
        self.flash_remaining = FLASH_SECONDS;
        if stack <= 1 && facts.health.health_target {
            if facts.health.percent.is_some() {
                Self::show(ui, self.health, true);
            }
        } else if stack <= 1 && view.objects.is_owned_by_object(id, view.player) && changed {
            emit(ui, PanelOutput::QueryItemMana(id));
        }
    }

    fn render_name(&self, ui: &mut UiRoot) {
        let (first, second) = match self.current_name.as_deref() {
            Some(n) if !n.is_empty() => {
                wrap_name_two_lines(n, self.name_width, self.font.as_deref())
            }
            _ => (String::new(), String::new()),
        };
        for (slot, text) in self.lines.iter().zip([first, second]) {
            let Some(id) = *slot else {
                continue;
            };
            let lines = if text.is_empty() {
                Vec::new()
            } else {
                vec![Line::new(text, NAME_COLOR)]
            };
            if let Some(t) = ui.widget_mut::<Text>(id)
                && t.content_lines() != lines
            {
                t.set_lines(lines);
            }
        }
    }

    /// Follow the selection: a new selection (or a changed stack size)
    /// renames the strip, sets the flash and decides the bars; the same
    /// selection refreshes its name (the object may have been updated);
    /// health that arrives for the selection shows its bar.
    pub fn update(&mut self, ui: &mut UiRoot, view: &ItemsView<'_>, facts: &SelectionFacts) {
        if !self.bound {
            return;
        }
        let selected = view.selected;
        if selected != self.current
            || selected.is_some_and(|id| stack_of(view, id) != self.current_stack)
        {
            self.apply_selection(ui, view, facts, selected);
        } else if let Some(id) = selected {
            self.refresh_name(view, facts, id, self.current_stack);
        }
        if self.current.is_some()
            && self.current_stack <= 1
            && facts.health.health_target
            && facts.health.percent.is_some()
        {
            Self::show(ui, self.health, true);
        }
        let health_fill = match self.current {
            Some(_) => facts.health.percent.unwrap_or(1.0),
            None => 0.0,
        };
        if let Some(w) = self.health.and_then(|m| ui.widget_mut::<Meter>(m)) {
            w.fill = Some(health_fill);
        }
        let mana_fill = if self.current.is_some() {
            facts.mana
        } else {
            0.0
        };
        if let Some(w) = self.mana.and_then(|m| ui.widget_mut::<Meter>(m)) {
            w.fill = Some(mana_fill);
        }
        self.render_name(ui);
    }

    /// The server answered a mana query: a valid answer for the selection
    /// shows its bar (filled from the facts' mana by [`Self::update`]), an
    /// invalid one closes the query.
    pub fn on_item_mana(&mut self, ui: &mut UiRoot, item: u32, valid: bool) {
        if !self.bound || self.current != Some(item) {
            return;
        }
        if !valid {
            emit(ui, PanelOutput::QueryItemMana(0));
            return;
        }
        Self::show(ui, self.mana, true);
    }

    /// One frame: the flash goes back to blank after
    /// [`FLASH_SECONDS`].
    pub fn tick(&mut self, ui: &mut UiRoot, dt: f64) {
        if self.flash_remaining <= 0.0 {
            return;
        }
        self.flash_remaining -= dt;
        if self.flash_remaining <= 0.0 {
            self.set_overlay(ui, DIRECT_STATE_ID);
        }
    }

    /// Stop following (the window goes); idempotent.
    pub fn dispose(&mut self) {
        self.bound = false;
    }

    /// The name element.
    #[must_use]
    pub fn name(&self) -> Option<ElementId> {
        self.name
    }
}
