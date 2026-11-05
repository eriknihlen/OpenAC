//! How big each markup control would like to be inside a flex container,
//! and how small it can go: the content sizes of flex layout. Every default
//! lives here so the look can be tuned in one place. Sizes are in pixels
//! and come from the control's current font, so a theme switch that
//! changes the font changes them.

use std::sync::Arc;

use ac_plugin_ui::flex::{FlexMeasurement, FlexSize};
use ac_plugin_ui::markup::Element as Markup;

use super::controls::{MarkupCheckbox, MarkupHotkey, MarkupNumber, MarkupRadio, SPIN_WIDTH};
use super::fold::{CAPTION_X as EXPANDER_CAPTION_X, ExpanderHeader, MarkupExpander};
use super::style;
use super::widgets::{MarkupButton, MarkupLabel, MarkupToggle};
use crate::font::UiFont;
use crate::root::UiRoot;
use crate::tree::ElementId;
use crate::widgets::Menu;

/// Text width per character with no font (headless only).
pub const FALLBACK_CHAR_WIDTH: f32 = 7.0;
/// Line height with no font (headless only).
pub const FALLBACK_LINE_HEIGHT: f32 = 14.0;
/// The smallest height of a button, tab, field or menu.
pub const CONTROL_HEIGHT: f32 = 24.0;
/// Room above and below a control's text, together.
pub const CONTROL_TEXT_PADDING: f32 = 8.0;
/// Room left and right of a button's caption, each side.
pub const BUTTON_PADDING_X: f32 = 12.0;
/// The smallest height of a toggle.
pub const TOGGLE_HEIGHT: f32 = 20.0;
/// Where a toggle's caption starts in Classic: lamp and gap.
pub const CLASSIC_TOGGLE_CAPTION_X: f32 = 17.0;
/// Where a themed toggle's caption starts: switch and gap.
pub const THEMED_TOGGLE_CAPTION_X: f32 = 1.0 + style::SWITCH_WIDTH + style::SWITCH_CAPTION_GAP;
/// Where a checkbox's or radio button's caption starts: box and gap.
pub const CHECK_CAPTION_X: f32 = 17.0;
/// Room after each round radio option's caption.
pub const RADIO_OPTION_GAP: f32 = 8.0;
/// A colour swatch's width.
pub const COLOR_WIDTH: f32 = 40.0;
/// A separator's thickness across its line: the line and room either side.
pub const SEPARATOR_SIZE: f32 = 9.0;
/// An icon's side.
pub const ICON_SIZE: f32 = 32.0;
/// A field's preferred width.
pub const FIELD_WIDTH: f32 = 120.0;
/// A field's least width.
pub const FIELD_MIN_WIDTH: f32 = 40.0;
/// A slider's height.
pub const SLIDER_HEIGHT: f32 = 16.0;
/// A meter's height.
pub const METER_HEIGHT: f32 = 12.0;
/// A list's preferred width.
pub const LIST_WIDTH: f32 = 160.0;
/// A list's preferred height.
pub const LIST_HEIGHT: f32 = 80.0;
/// A list's least width.
pub const LIST_MIN_WIDTH: f32 = 60.0;
/// A list's least height.
pub const LIST_MIN_HEIGHT: f32 = 40.0;

type Font<'a> = Option<&'a Arc<UiFont>>;

/// The width of `text` in `font`.
#[must_use]
pub fn text_width(font: Font<'_>, text: &str) -> f32 {
    font.map_or(text.chars().count() as f32 * FALLBACK_CHAR_WIDTH, |f| {
        f.measure_width(text)
    })
}

/// A line's height in `font`.
#[must_use]
pub fn line_height(font: Font<'_>) -> f32 {
    font.map_or(FALLBACK_LINE_HEIGHT, |f| f.line_height)
}

/// The height of a button, tab, field or menu set in `font`.
#[must_use]
pub fn control_height_for(font: Font<'_>) -> f32 {
    CONTROL_HEIGHT.max((line_height(font) + CONTROL_TEXT_PADDING).ceil())
}

/// The height of a toggle, checkbox or round radio option set in `font`.
#[must_use]
pub fn toggle_height_for(font: Font<'_>) -> f32 {
    TOGGLE_HEIGHT.max((line_height(font) + 4.0).ceil())
}

/// What a control's size is measured from besides its kind: its caption,
/// font, theme, fold state and radio options. A leaf measures again only
/// when these change.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct MeasureInputs {
    text: String,
    font: usize,
    themed: bool,
    collapsed: bool,
    options: Vec<String>,
}

impl MeasureInputs {
    /// Measured as an expander showing only its header.
    #[must_use]
    pub fn collapsed(&self) -> bool {
        self.collapsed
    }
}

fn font_key(font: Option<&Arc<UiFont>>) -> usize {
    font.map_or(0, |f| Arc::as_ptr(f) as usize)
}

/// What `id` (built for `markup`) is measured from now.
#[must_use]
pub fn inputs(ui: &UiRoot, id: ElementId, markup: &Markup) -> MeasureInputs {
    let mut m = MeasureInputs::default();
    match markup {
        Markup::Label { .. } => {
            if let Some(w) = ui.widget::<MarkupLabel>(id) {
                m.text.clone_from(&w.text);
                m.font = font_key(w.font.as_ref());
            }
        }
        Markup::Button { .. } | Markup::Tab { .. } => {
            if let Some(w) = ui.widget::<MarkupButton>(id) {
                m.text.clone_from(&w.text);
                m.font = font_key(w.font.as_ref());
            }
        }
        Markup::Toggle { .. } => {
            if let Some(w) = ui.widget::<MarkupToggle>(id) {
                m.text.clone_from(&w.text);
                m.font = font_key(w.font.as_ref());
                m.themed = w.palette.is_some();
            }
        }
        Markup::Checkbox { .. } => {
            if let Some(w) = ui.widget::<MarkupCheckbox>(id) {
                m.text.clone_from(&w.text);
                m.font = font_key(w.font.as_ref());
            }
        }
        Markup::Radio { .. } => {
            if let Some(w) = ui.widget::<MarkupRadio>(id) {
                m.options.clone_from(&w.items);
                m.font = font_key(w.font.as_ref());
            }
        }
        Markup::Field { .. } => {
            if let Some(w) = ui.widget::<super::MarkupField>(id) {
                m.font = font_key(w.inner.font.as_ref());
            }
        }
        Markup::Menu { .. } => {
            if let Some(w) = ui.widget::<Menu>(id) {
                m.font = font_key(w.button_font.as_ref().or(w.font.as_ref()));
            }
        }
        Markup::Number { .. } => {
            if let Some(w) = ui.widget::<MarkupNumber>(id) {
                m.font = font_key(w.inner.font.as_ref());
            }
        }
        Markup::Hotkey { .. } => {
            if let Some(w) = ui.widget::<MarkupHotkey>(id) {
                m.font = font_key(w.font.as_ref());
            }
        }
        Markup::Expander { .. } => {
            if let Some(header) = ui.widget::<MarkupExpander>(id).map(|x| x.header)
                && let Some(h) = ui.widget::<ExpanderHeader>(header)
            {
                m.text.clone_from(&h.text);
                m.font = font_key(h.font.as_ref());
            }
            m.collapsed = collapsed(ui, id, markup);
        }
        _ => {}
    }
    m
}

/// Whether `id` is an expander showing only its header.
#[must_use]
pub fn collapsed(ui: &UiRoot, id: ElementId, markup: &Markup) -> bool {
    matches!(markup, Markup::Expander { .. })
        && ui
            .widget::<MarkupExpander>(id)
            .is_some_and(|x| !x.laid_out_expanded())
}

/// The content size of `id`, built for `markup`, or `None` for an element
/// this does not size (groups are sized by their own layout or their
/// authored size). `expanded_height` is an expander's height while open.
#[must_use]
pub fn measure(
    ui: &UiRoot,
    id: ElementId,
    markup: &Markup,
    expanded_height: f32,
) -> Option<FlexMeasurement> {
    Some(match markup {
        Markup::Label { .. } => {
            let w = ui.widget::<MarkupLabel>(id)?;
            // A label may shrink to nothing (it draws "..." when cut short),
            // so a long bound caption never raises its window's minimum.
            let height = line_height(w.font.as_ref());
            FlexMeasurement::new(
                FlexSize::new(text_width(w.font.as_ref(), &w.text).ceil(), height),
                FlexSize::new(0.0, height),
            )
        }
        Markup::Button { icon, .. } | Markup::Tab { icon, .. } => {
            let w = ui.widget::<MarkupButton>(id)?;
            let height = control_height_for(w.font.as_ref());
            // A button with an icon keeps a square column for it on the
            // left, and centres its caption in the rest.
            let icon = if icon.is_some() { height } else { 0.0 };
            let caption = if w.text.is_empty() {
                0.0
            } else {
                text_width(w.font.as_ref(), &w.text).ceil() + 2.0 * BUTTON_PADDING_X
            };
            fixed((icon + caption).max(height), height)
        }
        Markup::Toggle { .. } => {
            let w = ui.widget::<MarkupToggle>(id)?;
            let caption_x = if w.palette.is_some() {
                THEMED_TOGGLE_CAPTION_X
            } else {
                CLASSIC_TOGGLE_CAPTION_X
            };
            fixed(
                caption_x + text_width(w.font.as_ref(), &w.text).ceil(),
                toggle_height_for(w.font.as_ref()),
            )
        }
        Markup::Icon { .. } => fixed(ICON_SIZE, ICON_SIZE),
        Markup::Field { .. } => {
            let w = ui.widget::<super::MarkupField>(id)?;
            range(
                FIELD_WIDTH,
                FIELD_MIN_WIDTH,
                control_height_for(w.inner.font.as_ref()),
            )
        }
        Markup::Menu { .. } => {
            let w = ui.widget::<Menu>(id)?;
            range(
                FIELD_WIDTH,
                FIELD_MIN_WIDTH,
                control_height_for(w.button_font.as_ref().or(w.font.as_ref())),
            )
        }
        Markup::Slider { .. } => range(FIELD_WIDTH, FIELD_MIN_WIDTH, SLIDER_HEIGHT),
        Markup::Meter { .. } => range(FIELD_WIDTH, FIELD_MIN_WIDTH, METER_HEIGHT),
        Markup::List { .. } | Markup::Log { .. } | Markup::Split { .. } => list_size(),
        Markup::Checkbox { .. } => {
            let w = ui.widget::<MarkupCheckbox>(id)?;
            fixed(
                CHECK_CAPTION_X + text_width(w.font.as_ref(), &w.text).ceil(),
                toggle_height_for(w.font.as_ref()),
            )
        }
        Markup::Radio { .. } => radio_size(ui.widget::<MarkupRadio>(id)?),
        Markup::Number { .. } => {
            let w = ui.widget::<MarkupNumber>(id)?;
            range(
                FIELD_WIDTH,
                FIELD_MIN_WIDTH + SPIN_WIDTH,
                control_height_for(w.inner.font.as_ref()),
            )
        }
        Markup::Hotkey { .. } => {
            let w = ui.widget::<MarkupHotkey>(id)?;
            range(
                FIELD_WIDTH,
                FIELD_MIN_WIDTH,
                control_height_for(w.font.as_ref()),
            )
        }
        Markup::Color { .. } => fixed(COLOR_WIDTH, CONTROL_HEIGHT),
        // Stretched along the container's cross axis by the default align,
        // it lies across a column and down a row.
        Markup::Separator { .. } => fixed(SEPARATOR_SIZE, SEPARATOR_SIZE),
        Markup::Expander { .. } => {
            let header = ui.widget::<MarkupExpander>(id)?.header;
            let h = ui.widget::<ExpanderHeader>(header)?;
            // The header's arrow and caption, at the height for its state.
            let min = EXPANDER_CAPTION_X + text_width(h.font.as_ref(), &h.text).ceil();
            let height = if collapsed(ui, id, markup) {
                ac_plugin_ui::markup::EXPANDER_HEADER_HEIGHT
            } else {
                expanded_height
            };
            FlexMeasurement::new(
                FlexSize::new(LIST_WIDTH.max(min), height),
                FlexSize::new(min, height),
            )
        }
        _ => return None,
    })
}

fn list_size() -> FlexMeasurement {
    FlexMeasurement::new(
        FlexSize::new(LIST_WIDTH, LIST_HEIGHT),
        FlexSize::new(LIST_MIN_WIDTH, LIST_MIN_HEIGHT),
    )
}

/// A radio group's cells: each as wide as its widest caption, one per
/// option along its orientation.
fn radio_size(radio: &MarkupRadio) -> FlexMeasurement {
    let font = radio.font.as_ref();
    let widest = radio
        .items
        .iter()
        .map(|item| text_width(font, item).ceil())
        .fold(0.0_f32, f32::max);
    let cell_w = if radio.segmented {
        widest + 2.0 * BUTTON_PADDING_X
    } else {
        CHECK_CAPTION_X + widest + RADIO_OPTION_GAP
    };
    let cell_h = if radio.segmented {
        control_height_for(font)
    } else {
        toggle_height_for(font)
    };
    let count = radio.items.len().max(1) as f32;
    if radio.horizontal {
        fixed(cell_w * count, cell_h)
    } else {
        fixed(cell_w, cell_h * count)
    }
}

fn fixed(width: f32, height: f32) -> FlexMeasurement {
    let s = FlexSize::new(width, height);
    FlexMeasurement::new(s, s)
}

fn range(width: f32, min_width: f32, height: f32) -> FlexMeasurement {
    FlexMeasurement::new(
        FlexSize::new(width, height),
        FlexSize::new(min_width, height),
    )
}
