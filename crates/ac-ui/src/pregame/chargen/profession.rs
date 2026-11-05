//! The wizard's profession page: the seven template buttons, a slider,
//! a typed value and a lock per attribute, the points left and the three
//! vitals they give, the backdrop art and the description per template.

use ac_core::chargen::ChargenAttributeValues;
use ac_core::chargen::attributes::ATTRIBUTE_MIN;
use ac_runtime::session::creation::{ChargenAttribute, CreationCommand};

use super::PageContext;
use crate::pregame::rich_text;
use crate::root::{ElementMessage, UiRoot};
use crate::tree::ElementId;
use crate::widgets::{Button, Field, Scrollbar, Text};

/// Template buttons and the template index each picks.
pub const TEMPLATE_BY_BUTTON: [(u32, u32); 7] = [
    (0x1000_03D9, 0),
    (0x1000_03DA, 1),
    (0x1000_03DF, 2),
    (0x1000_03DB, 3),
    (0x1000_03DC, 4),
    (0x1000_03DD, 5),
    (0x1000_03DE, 6),
];

/// Each attribute's slider block.
pub const SLIDER_BY_ATTRIBUTE: [(ChargenAttribute, u32); 6] = [
    (ChargenAttribute::Strength, 0x1000_03E6),
    (ChargenAttribute::Endurance, 0x1000_03E7),
    (ChargenAttribute::Coordination, 0x1000_03E8),
    (ChargenAttribute::Quickness, 0x1000_03E9),
    (ChargenAttribute::Focus, 0x1000_03EA),
    (ChargenAttribute::Self_, 0x1000_03EB),
];

/// In a slider block: the lock.
pub const SLIDER_LOCK_ID: u32 = 0x1000_02EC;
/// In a slider block: the name label.
pub const SLIDER_NAME_ID: u32 = 0x1000_02ED;
/// In a slider block: the slider.
pub const SLIDER_CONTROL_ID: u32 = 0x1000_02EE;
/// In a slider block: the typed value.
pub const SLIDER_VALUE_ID: u32 = 0x1000_02EF;
/// The lock's and the slider's state while the attribute is free.
pub const UNLOCKED_STATE: u32 = 0x1000_0018;
/// Their state while it is locked.
pub const LOCKED_STATE: u32 = 0x1000_0019;
/// Points left.
pub const AVAILABLE_ID: u32 = 0x1000_03E2;
/// Health.
pub const HEALTH_ID: u32 = 0x1000_03E3;
/// Stamina.
pub const STAMINA_ID: u32 = 0x1000_03E4;
/// Mana.
pub const MANA_ID: u32 = 0x1000_03E5;
/// The description text.
pub const DESCRIPTION_ID: u32 = 0x1000_03E0;
/// The backdrop.
pub const BACKDROP_ID: u32 = 0x1000_03D8;

/// The backdrop's state per template.
pub const BACKDROP_STATE: [(u32, u32); 7] = [
    (0, 0x1000_002B),
    (1, 0x1000_002C),
    (2, 0x1000_0031),
    (3, 0x1000_002D),
    (4, 0x1000_002E),
    (5, 0x1000_002F),
    (6, 0x1000_0030),
];

/// The description per template.
const DESCRIPTION_KEY: [(u32, &str); 7] = [
    (0, "ID_CharGen_CustomText"),
    (1, "ID_CharGen_BowText"),
    (2, "ID_CharGen_SwashText"),
    (3, "ID_CharGen_LifeText"),
    (4, "ID_CharGen_WarText"),
    (5, "ID_CharGen_WayText"),
    (6, "ID_CharGen_SoldierText"),
];

/// An attribute's caption.
#[must_use]
pub fn attribute_name(attribute: ChargenAttribute) -> &'static str {
    match attribute {
        ChargenAttribute::Strength => "Strength",
        ChargenAttribute::Endurance => "Endurance",
        ChargenAttribute::Quickness => "Quickness",
        ChargenAttribute::Coordination => "Coordination",
        ChargenAttribute::Focus => "Focus",
        ChargenAttribute::Self_ => "Self",
    }
}

/// An attribute's value.
#[must_use]
pub fn attribute_value(values: &ChargenAttributeValues, attribute: ChargenAttribute) -> i32 {
    match attribute {
        ChargenAttribute::Strength => values.strength,
        ChargenAttribute::Endurance => values.endurance,
        ChargenAttribute::Quickness => values.quickness,
        ChargenAttribute::Coordination => values.coordination,
        ChargenAttribute::Focus => values.focus,
        ChargenAttribute::Self_ => values.self_,
    }
}

/// Whether the snapshot's lock mask locks `attribute` (bit per attribute,
/// strength first, as the runtime numbers them).
#[must_use]
pub fn is_locked(mask: u32, attribute: ChargenAttribute) -> bool {
    mask & (1 << (attribute as u32 - 1)) != 0
}

/// The attribute a slider position asks for: the position times 100,
/// truncated, never under the minimum.
#[must_use]
pub fn attribute_from_scalar(scalar: f32) -> i32 {
    ((scalar * 100.0) as i32).max(ATTRIBUTE_MIN)
}

#[derive(Debug, Clone, Copy)]
struct Slider {
    attribute: ChargenAttribute,
    lock: Option<ElementId>,
    slider: Option<ElementId>,
    value: Option<ElementId>,
}

/// The profession page. See the module documentation.
#[derive(Debug, Clone)]
pub struct ProfessionPage {
    templates: Vec<(ElementId, u32)>,
    sliders: Vec<Slider>,
    available: Option<ElementId>,
    health: Option<ElementId>,
    stamina: Option<ElementId>,
    mana: Option<ElementId>,
    description: Option<ElementId>,
    backdrop: Option<ElementId>,
}

impl ProfessionPage {
    /// Bind the page under `root`.
    pub fn bind(ui: &mut UiRoot, root: ElementId) -> Self {
        let button = |ui: &mut UiRoot, scope, id| {
            let b = ui
                .find_descendant(scope, id)
                .filter(|b| ui.widget::<Button>(*b).is_some())?;
            if let Some(w) = ui.widget_mut::<Button>(b) {
                w.listeners.click = true;
            }
            Some(b)
        };
        let mut templates = Vec::new();
        for (id, template) in TEMPLATE_BY_BUTTON {
            if let Some(b) = button(ui, root, id) {
                templates.push((b, template));
            }
        }
        let mut sliders = Vec::new();
        for (attribute, container_id) in SLIDER_BY_ATTRIBUTE {
            let Some(container) = ui.find_descendant(root, container_id) else {
                continue;
            };
            let lock = button(ui, container, SLIDER_LOCK_ID);
            let slider = ui
                .find_descendant(container, SLIDER_CONTROL_ID)
                .filter(|s| ui.widget::<Scrollbar>(*s).is_some());
            // A value slider: dragging it reports the new value.
            if let Some(s) = slider.and_then(|s| ui.widget_mut::<Scrollbar>(s)) {
                s.horizontal = true;
                s.scalar = true;
            }
            let value = ui
                .find_descendant(container, SLIDER_VALUE_ID)
                .filter(|f| ui.widget::<Field>(*f).is_some());
            if let Some(v) = value {
                ui.with::<Field, _>(v, |f, ui| {
                    f.set_editable(ui, v, true);
                    f.character_filter = Some(Box::new(|c: char| c.is_ascii_digit()));
                });
            }
            if let Some(name) = ui
                .find_descendant(container, SLIDER_NAME_ID)
                .and_then(|n| ui.widget_mut::<Button>(n))
            {
                name.label = Some(attribute_name(attribute).to_owned());
            }
            sliders.push(Slider {
                attribute,
                lock,
                slider,
                value,
            });
        }
        let find = |ui: &UiRoot, id| ui.find_descendant(root, id);
        Self {
            templates,
            sliders,
            available: find(ui, AVAILABLE_ID),
            health: find(ui, HEALTH_ID),
            stamina: find(ui, STAMINA_ID),
            mana: find(ui, MANA_ID),
            description: find(ui, DESCRIPTION_ID).filter(|d| ui.widget::<Text>(*d).is_some()),
            backdrop: find(ui, BACKDROP_ID),
        }
    }

    /// The template buttons with their template indices.
    #[must_use]
    pub fn template_buttons(&self) -> &[(ElementId, u32)] {
        &self.templates
    }

    /// An attribute's lock, slider and typed value.
    #[must_use]
    pub fn slider(
        &self,
        attribute: ChargenAttribute,
    ) -> Option<(Option<ElementId>, Option<ElementId>, Option<ElementId>)> {
        self.sliders
            .iter()
            .find(|s| s.attribute == attribute)
            .map(|s| (s.lock, s.slider, s.value))
    }

    /// Show the snapshot's template and attributes.
    pub fn refresh(&self, ui: &mut UiRoot, ctx: &PageContext<'_>) {
        let s = ctx.snapshot;
        for &(b, template) in &self.templates {
            ui.with::<Button, _>(b, |w, ui| w.set_selected(ui, b, template == s.template));
        }
        for slider in &self.sliders {
            let value = attribute_value(&s.attributes, slider.attribute);
            if let Some(bar) = slider.slider.and_then(|b| ui.widget_mut::<Scrollbar>(b)) {
                bar.set_scalar_position(value as f32 / 100.0);
            }
            if let Some(v) = slider.value.and_then(|v| ui.widget_mut::<Field>(v)) {
                v.set_text(&value.to_string());
            }
            // The lock and its slider show the authored Locked/Unlocked art;
            // the lock is a toggle with no Normal/Highlight pair.
            let state = if is_locked(s.attribute_lock_mask, slider.attribute) {
                LOCKED_STATE
            } else {
                UNLOCKED_STATE
            };
            for e in [slider.lock, slider.slider].into_iter().flatten() {
                ui.set_retail_state(e, state);
            }
        }
        let endurance = s.attributes.endurance;
        for (display, value) in [
            (self.available, s.remaining_attribute_credits),
            (self.health, endurance / 2),
            (self.stamina, endurance),
            (self.mana, s.attributes.self_),
        ] {
            if let Some(b) = display.and_then(|d| ui.widget_mut::<Button>(d)) {
                b.value_label = Some(value.to_string());
            }
        }
        if let Some(backdrop) = self.backdrop
            && let Some(&(_, state)) = BACKDROP_STATE.iter().find(|(t, _)| *t == s.template)
        {
            ui.set_retail_state(backdrop, state);
        }
        if let Some(d) = self.description
            && let Some(&(_, key)) = DESCRIPTION_KEY.iter().find(|(t, _)| *t == s.template)
        {
            let text = (ctx.text)(key);
            let color = ui
                .widget::<Text>(d)
                .map_or(crate::draw::Color::WHITE, |t| t.default_color);
            let lines = rich_text::compose(ui, d, &[(text.as_deref(), color)]);
            rich_text::set_lines(ui, d, lines);
        }
    }

    /// What a message on the page asks; `None` when it is not the page's.
    #[must_use]
    pub fn on_message(
        &self,
        element: ElementId,
        message: &ElementMessage,
        ctx: &PageContext<'_>,
    ) -> Option<Vec<CreationCommand>> {
        if let ElementMessage::Clicked { .. } = message
            && let Some(&(_, template)) = self.templates.iter().find(|(b, _)| *b == element)
        {
            return Some(vec![CreationCommand::SelectTemplate(template)]);
        }
        let slider = self.sliders.iter().find(|s| {
            s.lock == Some(element) || s.slider == Some(element) || s.value == Some(element)
        })?;
        let attribute = slider.attribute;
        Some(match message {
            ElementMessage::Clicked { .. } if slider.lock == Some(element) => {
                let locked = is_locked(ctx.snapshot.attribute_lock_mask, attribute);
                vec![CreationCommand::SetAttributeLock(attribute, !locked)]
            }
            ElementMessage::ScalarChanged(scalar) if slider.slider == Some(element) => {
                vec![CreationCommand::SetAttribute(
                    attribute,
                    attribute_from_scalar(*scalar),
                )]
            }
            ElementMessage::Submitted(text) if slider.value == Some(element) => text
                .parse::<i32>()
                .ok()
                .filter(|_| text.bytes().all(|b| b.is_ascii_digit()))
                .map(|v| vec![CreationCommand::SetAttribute(attribute, v)])
                .unwrap_or_default(),
            _ => Vec::new(),
        })
    }

    /// The random button: one of the heritage's templates.
    #[must_use]
    pub fn randomize(
        ctx: &PageContext<'_>,
        random: &mut dyn FnMut(u32) -> u32,
    ) -> Option<CreationCommand> {
        let heritage = ctx.options.try_get_heritage(ctx.snapshot.heritage_id)?;
        let count = u32::try_from(heritage.templates.len())
            .ok()
            .filter(|&c| c > 0)?;
        Some(CreationCommand::SelectTemplate(random(count)))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_slider_position_is_a_truncated_percentage_clamped_low_only() {
        // OpenAC `CharacterCreationUiControllerTests.
        // ProfessionSlider_ScalarChange_TruncatesAndClampsLowOnly`.
        assert_eq!(attribute_from_scalar(0.0), 10);
        assert_eq!(attribute_from_scalar(0.05), 10);
        assert_eq!(attribute_from_scalar(0.555), 55);
        assert_eq!(attribute_from_scalar(1.0), 100);
    }

    #[test]
    fn the_lock_mask_has_a_bit_per_attribute_in_runtime_order() {
        assert!(is_locked(0b1, ChargenAttribute::Strength));
        assert!(is_locked(0b100, ChargenAttribute::Quickness));
        assert!(is_locked(0b1000, ChargenAttribute::Coordination));
        assert!(!is_locked(0b1000, ChargenAttribute::Self_));
    }
}
