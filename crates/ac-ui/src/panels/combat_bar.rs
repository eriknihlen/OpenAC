//! The combat window's attack controls (layout 0x21000073): in melee and
//! missile modes the basic panel (its melee or missile look), in magic the
//! spell bars; the window shows in those three modes only. The height
//! buttons charge an attack while held and swing on release; the power
//! slider sets the bar's level and fills while an attack charges; Repeat
//! Attacks, Auto Target and Keep in View are the character options of
//! those names; the power label reads Accuracy in missile mode.

use ac_core::combat::{AttackHeight, CombatMode};
use ac_runtime::character::option_id;
use ac_runtime::gameplay::{CombatAttackCommand, GameplayCommand, InputActivation};
use ac_runtime::session::SessionCommand;
use ac_runtime::world::AttackBarView;

use crate::element_info::ElementInfo;
use crate::import::ImportedLayout;
use crate::panels::PanelOutput;
use crate::panels::items::bind_click;
use crate::root::{ElementMessage, UiOutput, UiRoot};
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::{Button, Scrollbar, Text};

/// The combat window's layout.
pub const LAYOUT_ID: u32 = 0x2100_0073;
/// The melee and missile controls.
pub const BASIC_PANEL_ID: u32 = 0x1000_005C;
/// The spell bars.
pub const SPELLCASTING_PANEL_ID: u32 = 0x1000_0061;
/// The power slider.
pub const POWER_CONTROL_ID: u32 = 0x1000_004F;
/// "Speed", at the slider's left.
pub const SPEED_LABEL_ID: u32 = 0x1000_0051;
/// "Power" (melee) or "Accuracy" (missile), at its right.
pub const POWER_LABEL_ID: u32 = 0x1000_0052;
/// Repeat Attacks.
pub const REPEAT_ATTACKS_ID: u32 = 0x1000_0053;
/// Auto Target.
pub const AUTO_TARGET_ID: u32 = 0x1000_0054;
/// Keep in View.
pub const KEEP_IN_VIEW_ID: u32 = 0x1000_0055;
/// High attack.
pub const HIGH_BUTTON_ID: u32 = 0x1000_0057;
/// Medium attack.
pub const MEDIUM_BUTTON_ID: u32 = 0x1000_0058;
/// Low attack.
pub const LOW_BUTTON_ID: u32 = 0x1000_0059;
/// The basic panel's melee look.
pub const MELEE_STATE: u32 = 0x1000_0003;
/// The basic panel's missile look.
pub const MISSILE_STATE: u32 = 0x1000_0004;
/// The string table of the option captions.
pub const UI_STRING_TABLE_ID: u32 = 0x2300_0001;
/// The caption property.
const LABEL_PROPERTY: u32 = 0x17;

/// The captions (from the layout and the UI string table).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CombatBarLabels {
    /// "Speed".
    pub speed: String,
    /// "Power".
    pub power: String,
    /// "Accuracy".
    pub accuracy: String,
    /// "Repeat Attacks".
    pub repeat_attacks: String,
    /// "Auto Target".
    pub auto_target: String,
    /// "Keep in View".
    pub keep_in_view: String,
    /// "High".
    pub high: String,
    /// "Medium".
    pub medium: String,
    /// "Low".
    pub low: String,
}

impl CombatBarLabels {
    /// The captions of the layout `root`: the element captions in their
    /// states, the option captions by key; the English words where the dat
    /// has none.
    #[must_use]
    pub fn resolve(
        root: &ElementInfo,
        info_string: &dyn Fn(&ac_dat::objects::StringInfo) -> Option<String>,
        ui_string: &dyn Fn(&str) -> Option<String>,
    ) -> Self {
        use ac_dat::objects::{DIRECT_STATE_ID, PropertyValue};
        let element = |id: u32, state: u32, fallback: &str| {
            find(root, id)
                .and_then(
                    |e| match e.effective_property(LABEL_PROPERTY, Some(state)) {
                        Some(PropertyValue::StringInfo(s)) => info_string(s),
                        _ => None,
                    },
                )
                .unwrap_or_else(|| fallback.to_owned())
        };
        let runtime =
            |key: &str, fallback: &str| ui_string(key).unwrap_or_else(|| fallback.to_owned());
        Self {
            speed: element(SPEED_LABEL_ID, DIRECT_STATE_ID, "Speed"),
            power: element(POWER_LABEL_ID, MELEE_STATE, "Power"),
            accuracy: element(POWER_LABEL_ID, MISSILE_STATE, "Accuracy"),
            repeat_attacks: runtime("ID_CombatPanelOption_AutoRepeatAttack", "Repeat Attacks"),
            auto_target: runtime("ID_CombatPanelOption_AutoTarget", "Auto Target"),
            keep_in_view: runtime("ID_CombatPanelOption_ViewCombatTarget", "Keep in View"),
            high: element(HIGH_BUTTON_ID, DIRECT_STATE_ID, "High"),
            medium: element(MEDIUM_BUTTON_ID, DIRECT_STATE_ID, "Medium"),
            low: element(LOW_BUTTON_ID, DIRECT_STATE_ID, "Low"),
        }
    }
}

fn find(e: &ElementInfo, id: u32) -> Option<&ElementInfo> {
    if e.id == id {
        return Some(e);
    }
    e.children.iter().find_map(|c| find(c, id))
}

/// What the bar shows.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct CombatBarView {
    /// The combat mode.
    pub mode: CombatMode,
    /// The attack request.
    pub attack: AttackBarView,
    /// Repeat Attacks is on.
    pub auto_repeat_attack: bool,
    /// Auto Target is on.
    pub auto_target: bool,
    /// Keep in View is on.
    pub view_combat_target: bool,
}

/// See the module documentation.
#[derive(Debug)]
pub struct CombatBarController {
    basic: ElementId,
    spellcasting: ElementId,
    power: ElementId,
    power_label: Option<ElementId>,
    heights: [(ElementId, AttackHeight, CombatAttackCommand); 3],
    options: [(ElementId, u32); 3],
    labels: CombatBarLabels,
    mode: Option<CombatMode>,
}

/// The window shows in melee, missile and magic modes.
#[must_use]
pub fn shows_in(mode: CombatMode) -> bool {
    mode == CombatMode::MELEE || mode == CombatMode::MISSILE || mode == CombatMode::MAGIC
}

impl CombatBarController {
    /// Bind to the combat layout (`None` when a control is missing).
    pub fn bind(ui: &mut UiRoot, layout: &ImportedLayout, labels: CombatBarLabels) -> Option<Self> {
        let basic = layout.find(BASIC_PANEL_ID)?;
        let spellcasting = layout.find(SPELLCASTING_PANEL_ID)?;
        let power = layout
            .find(POWER_CONTROL_ID)
            .filter(|p| ui.widget::<Scrollbar>(*p).is_some())?;
        // The layout builds a plain bar; the power control is a value
        // slider, so a press or drag moves it and reports the new power.
        ui.with::<Scrollbar, _>(power, |s, _| s.scalar = true);
        let button = |id| {
            layout
                .find(id)
                .filter(|b| ui.widget::<Button>(*b).is_some())
        };
        let high = button(HIGH_BUTTON_ID)?;
        let medium = button(MEDIUM_BUTTON_ID)?;
        let low = button(LOW_BUTTON_ID)?;
        let repeat = button(REPEAT_ATTACKS_ID)?;
        let auto_target = button(AUTO_TARGET_ID)?;
        let keep_in_view = button(KEEP_IN_VIEW_ID)?;
        ui.set_visible(basic, true);
        ui.set_visible(spellcasting, false);
        for (b, label) in [
            (high, &labels.high),
            (medium, &labels.medium),
            (low, &labels.low),
            (repeat, &labels.repeat_attacks),
            (auto_target, &labels.auto_target),
            (keep_in_view, &labels.keep_in_view),
        ] {
            ui.with::<Button, _>(b, |w, _| w.label = Some(label.clone()));
        }
        for b in [repeat, auto_target, keep_in_view] {
            bind_click(ui, b);
        }
        let speed_label = layout.find(SPEED_LABEL_ID);
        let power_label = layout.find(POWER_LABEL_ID);
        set_static_text(ui, speed_label, &labels.speed, false);
        set_static_text(ui, power_label, &labels.power, true);
        Some(Self {
            basic,
            spellcasting,
            power,
            power_label,
            heights: [
                (high, AttackHeight::HIGH, CombatAttackCommand::HighAttack),
                (
                    medium,
                    AttackHeight::MEDIUM,
                    CombatAttackCommand::MediumAttack,
                ),
                (low, AttackHeight::LOW, CombatAttackCommand::LowAttack),
            ],
            options: [
                (repeat, option_id::AUTO_REPEAT_ATTACK),
                (auto_target, option_id::AUTO_TARGET),
                (keep_in_view, option_id::VIEW_COMBAT_TARGET),
            ],
            labels,
            mode: None,
        })
    }

    /// Show `view`; the window's visibility when the mode changed (it
    /// shows in melee, missile and magic).
    pub fn update(&mut self, ui: &mut UiRoot, view: &CombatBarView) -> Option<bool> {
        let changed = self.mode != Some(view.mode);
        if changed {
            self.mode = Some(view.mode);
            let physical = view.mode == CombatMode::MELEE || view.mode == CombatMode::MISSILE;
            ui.set_visible(self.basic, physical);
            ui.set_visible(self.spellcasting, view.mode == CombatMode::MAGIC);
            if view.mode == CombatMode::MELEE {
                ui.set_retail_state(self.basic, MELEE_STATE);
            } else if view.mode == CombatMode::MISSILE {
                ui.set_retail_state(self.basic, MISSILE_STATE);
            }
            let label = if view.mode == CombatMode::MISSILE {
                &self.labels.accuracy
            } else {
                &self.labels.power
            };
            set_static_text(ui, self.power_label, &label.clone(), true);
        }
        let dragging = ui
            .widget::<Scrollbar>(self.power)
            .is_some_and(Scrollbar::is_dragging);
        ui.with::<Scrollbar, _>(self.power, |s, _| {
            if !dragging {
                s.set_scalar_position(view.attack.desired_power);
            }
            s.scalar_fill = Some(view.attack.power_bar);
        });
        for &(b, height, _) in &self.heights {
            ui.with::<Button, _>(b, |w, ui| {
                w.set_selected(ui, b, view.attack.height == height);
            });
        }
        for &(b, option) in &self.options {
            let on = match option {
                option_id::AUTO_REPEAT_ATTACK => view.auto_repeat_attack,
                option_id::AUTO_TARGET => view.auto_target,
                _ => view.view_combat_target,
            };
            ui.with::<Button, _>(b, |w, ui| w.set_selected(ui, b, on));
        }
        changed.then(|| shows_in(view.mode))
    }

    /// A widget's message; true when it was the bar's.
    pub fn handle(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if let Some(&(_, _, command)) = self.heights.iter().find(|(b, _, _)| *b == element) {
            let activation = match message {
                ElementMessage::Pressed => InputActivation::Press,
                ElementMessage::Released => InputActivation::Release,
                _ => return false,
            };
            emit(ui, GameplayCommand::AttackInput(command, activation));
            return true;
        }
        if element == self.power {
            if let ElementMessage::ScalarChanged(p) = message {
                emit(ui, GameplayCommand::SetAttackPower(*p));
                return true;
            }
            return false;
        }
        if let Some(&(b, option)) = self.options.iter().find(|(b, _)| *b == element) {
            if !matches!(message, ElementMessage::Clicked { .. }) {
                return false;
            }
            let value = ui.widget::<Button>(b).is_some_and(Button::selected);
            ui.emit(UiOutput::Panel(PanelOutput::Session(
                SessionCommand::SetCharacterOption { option, value },
            )));
            return true;
        }
        false
    }
}

impl crate::host::PanelController for CombatBarController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        self.handle(ui, element, message)
    }
}

fn emit(ui: &mut UiRoot, command: GameplayCommand) {
    ui.emit(UiOutput::Panel(PanelOutput::Session(
        SessionCommand::Gameplay(command),
    )));
}

/// A fixed one-line caption, left or right aligned, no padding.
fn set_static_text(ui: &mut UiRoot, text: Option<ElementId>, value: &str, right_aligned: bool) {
    let Some(t) = text.and_then(|t| ui.widget_mut::<Text>(t)) else {
        return;
    };
    t.one_line = true;
    t.padding = 0.0;
    t.centered = false;
    t.right_aligned = right_aligned;
    let color = t.default_color;
    t.set_lines(vec![Line::new(value.to_owned(), color)]);
}
