//! The vitals window: health, stamina and mana bars with their current/max
//! numbers, and the switch between the stacked and side-by-side windows.
//! (The press that toggles the detail icons is the window root's own
//! behaviour, [`crate::widgets::VitalsRoot`].)

use ac_core::player::{LocalPlayerState, VitalKind};

use crate::draw::Color;
use crate::host::PanelController;
use crate::import::ImportedLayout;
use crate::root::UiRoot;
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::{Meter, Text};

/// The stacked vitals window's layout.
pub const LAYOUT_ID: u32 = 0x2100_006C;
/// The side-by-side vitals window's layout.
pub const SIDE_BY_SIDE_LAYOUT_ID: u32 = 0x2100_0075;
/// Health bar.
pub const HEALTH: u32 = 0x1000_00E6;
/// Stamina bar.
pub const STAMINA: u32 = 0x1000_00EC;
/// Mana bar.
pub const MANA: u32 = 0x1000_00EE;
/// Health numbers.
pub const HEALTH_TEXT: u32 = 0x1000_00EB;
/// Stamina numbers.
pub const STAMINA_TEXT: u32 = 0x1000_00ED;
/// Mana numbers.
pub const MANA_TEXT: u32 = 0x1000_00EF;

/// The colour of the current/max numbers.
pub const NUMBER_COLOR: Color = Color::WHITE;

/// One vital as the window shows it.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct VitalView {
    /// Current share of the maximum, 0..=1 (`None`: unknown, drawn empty).
    pub percent: Option<f32>,
    /// Current value.
    pub current: Option<u32>,
    /// Maximum (buffs and vitae included).
    pub max: Option<u32>,
}

impl VitalView {
    /// The bar's fill.
    #[must_use]
    pub fn fill(&self) -> f32 {
        self.percent.unwrap_or(0.0)
    }

    /// "current/max", or "" while either is unknown.
    #[must_use]
    pub fn text(&self) -> String {
        match (self.current, self.max) {
            (Some(c), Some(m)) => format!("{c}/{m}"),
            _ => String::new(),
        }
    }
}

/// The three vitals.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct VitalsView {
    /// Health.
    pub health: VitalView,
    /// Stamina.
    pub stamina: VitalView,
    /// Mana.
    pub mana: VitalView,
}

impl From<ac_runtime::world::VitalReading> for VitalView {
    fn from(reading: ac_runtime::world::VitalReading) -> Self {
        Self {
            percent: reading.percent,
            current: reading.current,
            max: reading.max,
        }
    }
}

impl VitalsView {
    /// The vitals of the player view a session sends its window.
    #[must_use]
    pub fn from_player_view(view: &ac_runtime::world::PlayerView) -> Self {
        Self {
            health: view.health.into(),
            stamina: view.stamina.into(),
            mana: view.mana.into(),
        }
    }

    /// The vitals of the local player's numbers.
    #[must_use]
    pub fn from_player(player: &LocalPlayerState) -> Self {
        let vital = |kind: VitalKind, percent: Option<f32>| VitalView {
            percent,
            current: player.get(kind).map(|v| v.current),
            max: player.get_max_approx(kind),
        };
        Self {
            health: vital(VitalKind::Health, player.health_percent()),
            stamina: vital(VitalKind::Stamina, player.stamina_percent()),
            mana: vital(VitalKind::Mana, player.mana_percent()),
        }
    }
}

/// Binds a vitals window's bars and numbers to a [`VitalsView`].
#[derive(Debug, Clone, Default)]
pub struct VitalsController {
    bars: [(Option<ElementId>, Option<ElementId>); 3],
    view: VitalsView,
}

impl VitalsController {
    /// Bind to an imported vitals window (missing bars are skipped).
    pub fn bind(ui: &mut UiRoot, layout: &ImportedLayout) -> Self {
        let mut bars = [(None, None); 3];
        for (slot, (bar, text)) in bars.iter_mut().zip([
            (HEALTH, HEALTH_TEXT),
            (STAMINA, STAMINA_TEXT),
            (MANA, MANA_TEXT),
        ]) {
            let Some(m) = layout
                .find(bar)
                .filter(|m| ui.widget::<Meter>(*m).is_some())
            else {
                continue;
            };
            // The meter's own caption stays empty; the authored number
            // text under it shows current/max.
            let font = ui.widget_mut::<Meter>(m).and_then(|w| {
                w.label = None;
                w.font.clone()
            });
            let number = layout
                .find(text)
                .filter(|t| ui.widget::<Text>(*t).is_some());
            if let Some(t) = number
                && let Some(w) = ui.widget_mut::<Text>(t)
            {
                w.centered = true;
                w.right_aligned = false;
                w.one_line = true;
                if w.font.is_none() {
                    w.font = font;
                }
            }
            if let Some(t) = number {
                ui.with::<Text, _>(t, |w, ui| {
                    if let Some(e) = ui.element_mut(t) {
                        w.set_selectable(e, false);
                    }
                });
            }
            *slot = (Some(m), number);
        }
        Self {
            bars,
            view: VitalsView::default(),
        }
    }

    /// The view shown.
    #[must_use]
    pub fn view(&self) -> &VitalsView {
        &self.view
    }

    /// The bar and number elements of health, stamina and mana.
    #[must_use]
    pub fn elements(&self) -> [(Option<ElementId>, Option<ElementId>); 3] {
        self.bars
    }

    /// Show `view`.
    pub fn apply(&mut self, ui: &mut UiRoot, view: &VitalsView) {
        self.view = *view;
        for ((bar, number), v) in self.bars.iter().zip([view.health, view.stamina, view.mana]) {
            if let Some(m) = bar
                && let Some(w) = ui.widget_mut::<Meter>(*m)
            {
                w.fill = Some(v.fill());
            }
            if let Some(t) = number
                && let Some(w) = ui.widget_mut::<Text>(*t)
            {
                let s = v.text();
                w.set_lines(if s.is_empty() {
                    Vec::new()
                } else {
                    vec![Line::new(s, NUMBER_COLOR)]
                });
            }
        }
    }
}

impl PanelController for VitalsController {}

/// Shows the stacked or the side-by-side vitals window as the character
/// option says.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VitalsSideBySide {
    stacked_window: String,
    side_window: String,
    applied: Option<bool>,
}

impl VitalsSideBySide {
    /// Switch between the two registered windows.
    #[must_use]
    pub fn new(stacked_window: &str, side_window: &str) -> Self {
        Self {
            stacked_window: stacked_window.to_owned(),
            side_window: side_window.to_owned(),
            applied: None,
        }
    }

    /// The option last applied (`None` before the first).
    #[must_use]
    pub fn applied(&self) -> Option<bool> {
        self.applied
    }

    /// Apply the option (only a change does anything).
    pub fn tick(&mut self, ui: &mut UiRoot, side_by_side: bool) {
        if self.applied == Some(side_by_side) {
            return;
        }
        self.applied = Some(side_by_side);
        if side_by_side {
            ui.hide_window(&self.stacked_window);
            ui.show_window(&self.side_window);
        } else {
            ui.show_window(&self.stacked_window);
            ui.hide_window(&self.side_window);
        }
    }
}
