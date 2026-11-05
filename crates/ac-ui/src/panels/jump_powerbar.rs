//! The jump power bar: the floating power meter in its jump state, shown
//! while a jump is charged, filled by the charge.

use crate::import::ImportedLayout;
use crate::root::UiRoot;
use crate::tree::ElementId;
use crate::widgets::Meter;

/// The floating power bar's layout.
pub const LAYOUT_ID: u32 = 0x2100_0072;
/// Its meter.
pub const METER_ID: u32 = 0x1000_0034;
/// The meter's jump state (its jump fill art).
pub const JUMP_MODE_STATE: u32 = 0x1000_0042;

/// The bar's controller.
#[derive(Debug)]
pub struct JumpPowerbarController {
    meter: ElementId,
    charging: bool,
    synced: bool,
    disposed: bool,
}

impl JumpPowerbarController {
    /// Bind to the imported bar: its meter enters the jump state (`None`
    /// when the meter or the state is missing).
    pub fn bind(ui: &mut UiRoot, layout: &ImportedLayout) -> Option<Self> {
        let meter = layout
            .find(METER_ID)
            .filter(|m| ui.widget::<Meter>(*m).is_some())?;
        if !ui.set_retail_state(meter, JUMP_MODE_STATE) {
            return None;
        }
        if let Some(m) = ui.widget_mut::<Meter>(meter) {
            m.fill = Some(0.0);
        }
        Some(Self {
            meter,
            charging: false,
            synced: false,
            disposed: false,
        })
    }

    /// The meter.
    #[must_use]
    pub fn meter(&self) -> ElementId {
        self.meter
    }

    /// Follow the charge (`Some(power)` while a jump is charged): the fill
    /// is the power clamped to 0..=1 (0 when not charging). Returns whether
    /// the bar's window should show, when that changed (or `force`, or the
    /// first time).
    pub fn update(&mut self, ui: &mut UiRoot, charge: Option<f32>, force: bool) -> Option<bool> {
        if self.disposed {
            return None;
        }
        let charging = charge.is_some();
        let power = charge.map_or(0.0, |p| p.clamp(0.0, 1.0));
        if let Some(m) = ui.widget_mut::<Meter>(self.meter) {
            m.fill = Some(power);
        }
        if force || !self.synced || charging != self.charging {
            self.charging = charging;
            self.synced = true;
            return Some(charging);
        }
        None
    }

    /// Stop following; the fill goes empty.
    pub fn dispose(&mut self, ui: &mut UiRoot) {
        if self.disposed {
            return;
        }
        self.disposed = true;
        if let Some(m) = ui.widget_mut::<Meter>(self.meter) {
            m.fill = None;
        }
    }
}
