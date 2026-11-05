//! The options window (layout 0x2100006E, element 0x1000018D): four tabs,
//! Gameplay (buttons only), Character (the server-saved character
//! options), Chat (window opacity and the five chat windows' filters) and
//! Configuration (sound, camera, graphics, textures, input, interface).
//! Each list page is an option page ([`model`]): a change is live at once,
//! Apply commits it, Reset and Defaults are the footer's other two buttons,
//! Apply and Reset are ghosted while nothing changed, switching tabs or
//! hiding the window resets what was not applied and showing a page applies
//! it.
//!
//! The window keeps a copy of the live values ([`LiveValues`]): the client
//! settings and the character's option words. A row writes there and says
//! which section changed ([`OptionsOutput::Changed`]); the client applies
//! and saves it. The client hands the copy back whenever the values change
//! elsewhere ([`OptionsWindowController::set_live`]).

pub mod chat_dat;
pub mod keys;
pub mod model;
pub mod resolutions;
pub mod spec;

use std::collections::HashMap;
use std::sync::Arc;

use ac_host::settings::chat_opacity;

use crate::draw::Color;
use crate::font::UiFont;
use crate::host::PanelController;
use crate::import::{ImportedLayout, LayoutSource, import_rooted};
use crate::root::{ElementMessage, UiOutput, UiRoot};
use crate::states;
use crate::text::Line;
use crate::tree::{Element, ElementId};
use crate::widgets::{
    Bitfield64, Button, ListBox, Menu, MenuItem, OptionToggleSlider, Panel, ScrollSource,
    Scrollbar, TabPanel, Text, chrome,
};

pub use chat_dat::Caption;
pub use keys::{
    AudioField, CameraField, ChatField, DisplayField, LiveValues, MixerField, OptionKey, Section,
};
pub use model::{OptionPage, OptionValue, RowHost};
use spec::{ConfigRow, template};

use super::{PanelOutput, panel_id};

/// The layout holding the options window.
pub const HOST_LAYOUT_ID: u32 = 0x2100_006E;
/// The options window's element there.
pub const SLOT_ELEMENT_ID: u32 = 0x1000_018D;

/// The Gameplay page.
pub const GAMEPLAY_PAGE_ID: u32 = 0x1000_0212;
/// The Character page.
pub const CHARACTER_PAGE_ID: u32 = 0x1000_0211;
/// The Chat page.
pub const CHAT_PAGE_ID: u32 = 0x1000_050C;
/// The Configuration page.
pub const CONFIG_PAGE_ID: u32 = 0x1000_0213;

const CLOSE_BUTTON_ID: u32 = 0x1000_0210;
const EXIT_TO_CHARACTER_SELECTION_ID: u32 = 0x1000_0203;
const CONFIGURE_KEYBOARD_ID: u32 = 0x1000_0204;
const IN_GAME_HELP_FILES_ID: u32 = 0x1000_0205;
const URGENT_ASSISTANCE_ID: u32 = 0x1000_0206;
const REPORT_ABUSE_ID: u32 = 0x1000_0207;
const USE_MOUSE_TURNING_SETTINGS_ID: u32 = 0x1000_05CC;
const EXIT_GAME_ID: u32 = 0x1000_0617;

/// The footer's Apply button.
pub const APPLY_BUTTON_ID: u32 = 0x1000_01FC;
/// The footer's Reset button.
pub const RESET_BUTTON_ID: u32 = 0x1000_01FD;
/// The footer's Defaults button.
pub const DEFAULTS_BUTTON_ID: u32 = 0x1000_01FE;

/// The Character page's list.
pub const CHARACTER_LIST_ID: u32 = 0x1000_01FA;
/// The Character page's scrollbar.
pub const CHARACTER_SCROLLBAR_ID: u32 = 0x1000_01FB;
/// The Configuration page's list.
pub const CONFIG_LIST_ID: u32 = 0x1000_0200;
/// The Chat page's list.
pub const CHAT_LIST_ID: u32 = 0x1000_050D;
/// The Chat page's scrollbar (the same id as the Configuration page's).
pub const CHAT_SCROLLBAR_ID: u32 = 0x1000_0201;

const STRING_TABLE: u32 = 0x2300_0003;
const FILTER_STRING_TABLE: u32 = 0x2300_000D;

const SLIDER_LABEL_ID: u32 = 0x1000_021B;
const SLIDER_ID: u32 = 0x1000_021C;
const SLIDER_RANGE_MIN_ID: u32 = 0x1000_021E;
const SLIDER_RANGE_MAX_ID: u32 = 0x1000_021F;
const MENU_LABEL_ID: u32 = 0x1000_0223;
const MENU_ID: u32 = 0x1000_0224;
const TOGGLE_CHECKBOX_ID: u32 = 0x1000_0219;

/// The colour of a caption whose option is only stored, and of a mixer
/// row the retail mixer overrides.
pub const STORE_ONLY_CAPTION_COLOR: Color = Color::rgba(0.5, 0.5, 0.5, 1.0);

/// What the footer backing sits under (far below every authored child).
const FOOTER_BACKING_Z_ORDER: i32 = i32::MIN / 2;

const SUPPORT_URL: &str =
    "http://support.turbine.com/ics/support/ticketnewwizard.asp?style=classic";

/// What Urgent Assistance says (no browser is launched).
#[must_use]
pub fn urgent_assistance_unavailable() -> String {
    format!(
        "An error occurred while trying to launch your web browser.\nThe web site to submit an urgent assistance request is listed below. Please go there to complete your request.\n{SUPPORT_URL}"
    )
}

/// What Report Abuse says.
#[must_use]
pub fn report_abuse_unavailable() -> String {
    format!(
        "An error occurred while trying to launch your web browser.\nThe web site to submit an abuse report is listed below. Please go there to complete your request.\n{SUPPORT_URL}"
    )
}

/// The menus' look.
mod menu_chrome {
    pub const NORMAL: u32 = 0x0600_12B3;
    pub const PRESSED: u32 = 0x0600_12B4;
    pub const ARROW_CAP_CLOSED: u32 = 0x0600_12B1;
    pub const ARROW_CAP_OPEN: u32 = 0x0600_12B2;
    pub const ROWS_PER_COLUMN: usize = 6;
    pub const ROW_HEIGHT: f32 = 18.0;
    pub const COLUMN_WIDTH: f32 = 100.0;
    pub const SCROLLBAR_WIDTH: f32 = 16.0;
    pub const SCROLL_BUTTON_EXTENT: f32 = 16.0;
    pub const SCROLL_TRACK: u32 = 0x0600_4C5F;
    pub const SCROLL_THUMB_TOP: u32 = 0x0600_4C60;
    pub const SCROLL_THUMB: u32 = 0x0600_4C63;
    pub const SCROLL_THUMB_BOTTOM: u32 = 0x0600_4C66;
    pub const SCROLL_UP: u32 = 0x0600_4C6C;
    pub const SCROLL_DOWN: u32 = 0x0600_4C69;
}

/// What the options window asks the client to do.
#[derive(Debug, Clone, PartialEq)]
pub enum OptionsOutput {
    /// A row changed a section of the live values: apply it (read it from
    /// [`OptionsWindowController::live`]) and save it unless `persist` is
    /// off (a chat opacity slider mid-drag; it saves when the drag ends).
    Changed {
        /// The section.
        section: Section,
        /// Save it now.
        persist: bool,
    },
    /// A page was applied: the character options block is saved.
    Applied,
    /// Gameplay: exit to character selection.
    ExitToCharacterSelection,
    /// Gameplay: exit the game.
    ExitGame,
    /// Gameplay: use the mouse-turning settings.
    UseMouseTurningSettings,
    /// Show this text as a system message.
    SystemMessage(String),
    /// Gameplay: open the Configure Keyboard screen.
    ConfigureKeyboard,
}

/// The four pages.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OptionsPage {
    /// Gameplay.
    Gameplay,
    /// Character.
    Character,
    /// Chat.
    Chat,
    /// Configuration.
    Configuration,
}

impl OptionsPage {
    const ALL: [Self; 4] = [
        Self::Gameplay,
        Self::Character,
        Self::Chat,
        Self::Configuration,
    ];

    /// The page's element id.
    #[must_use]
    pub const fn element_id(self) -> u32 {
        match self {
            Self::Gameplay => GAMEPLAY_PAGE_ID,
            Self::Character => CHARACTER_PAGE_ID,
            Self::Chat => CHAT_PAGE_ID,
            Self::Configuration => CONFIG_PAGE_ID,
        }
    }

    fn index(self) -> usize {
        match self {
            Self::Gameplay => 0,
            Self::Character => 1,
            Self::Chat => 2,
            Self::Configuration => 3,
        }
    }

    fn of(element_id: u32) -> Option<Self> {
        Self::ALL.into_iter().find(|p| p.element_id() == element_id)
    }
}

/// How a row shows its value.
#[derive(Debug, Clone, PartialEq)]
enum Widget {
    Checkbox(ElementId),
    Slider {
        slider: ElementId,
        min: f64,
        max: f64,
        /// Step and whole numbers, for OpenAC's number sliders.
        snap: Option<(f64, bool)>,
        /// A caption that shows the value: the text, its label and what
        /// follows the number.
        value_caption: Option<(ElementId, &'static str, &'static str)>,
    },
    Menu {
        menu: ElementId,
        values: Vec<OptionValue>,
        labels: Vec<String>,
        /// A value not among the choices shows itself (else nothing).
        missing_shows_value: bool,
    },
    Bitfield(ElementId),
}

#[derive(Debug, Clone, PartialEq)]
struct Binding {
    key: OptionKey,
    widget: Widget,
    /// The caption dimmed while the retail mixer is on: a text or a
    /// checkbox.
    dimmed_caption: Option<ElementId>,
}

#[derive(Debug, Clone, Default)]
struct Page {
    model: OptionPage,
    rows: Vec<Binding>,
    apply: Option<ElementId>,
    reset: Option<ElementId>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Action {
    Close,
    ExitToCharacterSelection,
    ExitGame,
    UseMouseTurningSettings,
    UrgentAssistance,
    ReportAbuse,
    ConfigureKeyboard,
    Apply(usize),
    Reset(usize),
    Defaults(usize),
}

/// What the pages are captioned and filled from.
pub struct OptionsSources<'a> {
    /// The layouts (row templates).
    pub layouts: &'a dyn LayoutSource,
    /// The interface's default font.
    pub font: Option<Arc<UiFont>>,
    /// A string by table and key (`None` when the dats lack it).
    pub strings: &'a dyn Fn(u32, &str) -> Option<String>,
    /// The resolutions the display menu offers.
    pub resolutions: Vec<String>,
    /// The opacity sliders' defaults (the dats', else the fallbacks).
    pub opacity_defaults: (f32, f32),
    /// The opacity sliders' captions.
    pub opacity_captions: (Caption, Caption),
}

impl std::fmt::Debug for OptionsSources<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OptionsSources")
            .field("resolutions", &self.resolutions)
            .finish_non_exhaustive()
    }
}

/// The options window. See the module documentation.
#[derive(Debug)]
pub struct OptionsWindowController {
    root: ElementId,
    pages: [Page; 4],
    live: LiveValues,
    rows: HashMap<ElementId, (usize, usize)>,
    actions: HashMap<ElementId, Action>,
    notices: Vec<String>,
}

/// The rows' side of the page model: widgets, the live copy, the
/// outputs.
struct Host<'a> {
    ui: &'a mut UiRoot,
    rows: &'a [Binding],
    live: &'a mut LiveValues,
    links: Vec<(usize, OptionValue)>,
    gates: (Option<ElementId>, Option<ElementId>),
}

fn emit(ui: &mut UiRoot, output: OptionsOutput) {
    ui.emit(UiOutput::Panel(PanelOutput::Options(output)));
}

fn snap(value: f64, min: f64, max: f64, step: f64, integer: bool) -> f64 {
    let mut v = value.clamp(min, max);
    if step > 0.0 {
        v = min + ((v - min) / step).round() * step;
    }
    if integer {
        v = v.round();
    }
    v.clamp(min, max)
}

fn normalized(value: f64, min: f64, max: f64) -> f32 {
    if max > min {
        ((value - min) / (max - min)) as f32
    } else {
        0.0
    }
}

/// True while the player drags `widget`'s slider thumb.
fn slider_dragging(ui: &UiRoot, widget: &Widget) -> bool {
    match widget {
        Widget::Slider { slider, .. } => ui
            .widget::<Scrollbar>(*slider)
            .is_some_and(Scrollbar::is_dragging),
        _ => false,
    }
}

fn show(ui: &mut UiRoot, widget: &Widget, value: &OptionValue) {
    match (widget, value) {
        (Widget::Checkbox(b), OptionValue::Bool(v)) => {
            let b = *b;
            ui.with::<Button, _>(b, |w, ui| w.set_selected(ui, b, *v));
        }
        (
            Widget::Slider {
                slider,
                min,
                max,
                snap: s,
                value_caption,
            },
            OptionValue::Float(v),
        ) => {
            let v = match s {
                Some((step, integer)) => snap(f64::from(*v), *min, *max, *step, *integer),
                None => f64::from(*v),
            };
            let n = normalized(v, *min, *max);
            ui.with::<Scrollbar, _>(*slider, |s, _| s.set_scalar_position(n));
            if let Some((text, label, suffix)) = value_caption {
                let number = if matches!(s, Some((_, true))) {
                    format!("{}", v.round() as i64)
                } else {
                    format!("{v}")
                };
                set_text(ui, *text, &format!("{label} ({number}{suffix})"), None);
            }
        }
        (
            Widget::Menu {
                menu,
                values,
                labels,
                missing_shows_value,
            },
            v,
        ) => {
            let index = values.iter().position(|x| x == v);
            let label = match (index, v) {
                (Some(i), _) => labels.get(i).cloned().unwrap_or_default(),
                (None, OptionValue::Text(t)) if *missing_shows_value => t.clone(),
                _ => String::new(),
            };
            ui.with::<Menu, _>(*menu, |m, _| {
                m.selected = index.map(|i| i as u64);
                m.button_label = label;
            });
        }
        (Widget::Bitfield(b), OptionValue::Bits(v)) => {
            let b = *b;
            let (low, high) = split_mask(*v);
            ui.with::<Bitfield64, _>(b, |f, ui| f.set_current_value(ui, low, high));
        }
        _ => {}
    }
}

/// A 64-bit chat filter as the bitfield's two halves: the low 32 bits and
/// the high 32 bits.
fn split_mask(combined: u64) -> (u64, u64) {
    (combined & 0xFFFF_FFFF, (combined >> 32) & 0xFFFF_FFFF)
}

fn combine_mask(low: u64, high: u64) -> u64 {
    ((high & 0xFFFF_FFFF) << 32) | (low & 0xFFFF_FFFF)
}

impl RowHost for Host<'_> {
    fn apply(&mut self, row: usize, value: &OptionValue) {
        let Some(binding) = self.rows.get(row) else {
            return;
        };
        match (binding.key, value) {
            (
                OptionKey::Chat(field @ (ChatField::DefaultOpacity | ChatField::ActiveOpacity)),
                OptionValue::Float(v),
            ) => {
                let chat = &mut self.live.settings.chat;
                let (default, active) = if field == ChatField::DefaultOpacity {
                    chat_opacity::set_default(chat.active_opacity, *v)
                } else {
                    chat_opacity::set_active(chat.default_opacity, *v)
                };
                chat.default_opacity = default;
                chat.active_opacity = active;
                let (mine, partner_field, partner_value) = if field == ChatField::DefaultOpacity {
                    (default, ChatField::ActiveOpacity, active)
                } else {
                    (active, ChatField::DefaultOpacity, default)
                };
                show(self.ui, &binding.widget, &OptionValue::Float(mine));
                if let Some(j) = self
                    .rows
                    .iter()
                    .position(|b| b.key == OptionKey::Chat(partner_field))
                {
                    self.links.push((j, OptionValue::Float(partner_value)));
                }
                let dragging = slider_dragging(self.ui, &binding.widget);
                emit(
                    self.ui,
                    OptionsOutput::Changed {
                        section: Section::Chat,
                        persist: !dragging,
                    },
                );
            }
            (key, value) => {
                let value = match (&binding.widget, value) {
                    (
                        Widget::Slider {
                            min,
                            max,
                            snap: Some((step, integer)),
                            ..
                        },
                        OptionValue::Float(v),
                    ) => {
                        OptionValue::Float(snap(f64::from(*v), *min, *max, *step, *integer) as f32)
                    }
                    _ => value.clone(),
                };
                show(self.ui, &binding.widget, &value);
                // The interface scale moves the slider under the pointer, so
                // it settles (applies and saves) only when the drag ends.
                let settled = key != OptionKey::Display(DisplayField::UiScale)
                    || !slider_dragging(self.ui, &binding.widget);
                if let Some(section) = self.live.write(key, &value) {
                    emit(
                        self.ui,
                        OptionsOutput::Changed {
                            section,
                            persist: settled,
                        },
                    );
                }
            }
        }
    }

    fn read(&mut self, row: usize) -> Option<OptionValue> {
        let binding = self.rows.get(row)?;
        let value = self.live.read(binding.key)?;
        Some(match (&binding.widget, value) {
            (
                Widget::Slider {
                    min,
                    max,
                    snap: Some((step, integer)),
                    ..
                },
                OptionValue::Float(v),
            ) => OptionValue::Float(snap(f64::from(v), *min, *max, *step, *integer) as f32),
            (_, v) => v,
        })
    }

    fn refresh(&mut self, row: usize, value: &OptionValue) {
        if let Some(binding) = self.rows.get(row) {
            show(self.ui, &binding.widget, value);
        }
    }

    fn take_links(&mut self) -> Vec<(usize, OptionValue)> {
        std::mem::take(&mut self.links)
    }

    fn after_apply(&mut self) {
        emit(self.ui, OptionsOutput::Applied);
    }

    fn option_changed(&mut self, changed: bool) {
        let state = if changed {
            states::NORMAL
        } else {
            states::GHOSTED
        };
        if let (Some(apply), Some(reset)) = self.gates {
            self.ui.set_retail_state(apply, state);
            self.ui.set_retail_state(reset, state);
        }
    }
}

/// The checkbox of a toggle row: the row itself, else its first button
/// child, else the authored checkbox element.
fn find_checkbox(ui: &UiRoot, root: ElementId) -> Option<ElementId> {
    if ui.widget::<Button>(root).is_some() {
        return Some(root);
    }
    ui.children(root)
        .into_iter()
        .find(|c| ui.widget::<Button>(*c).is_some())
        .or_else(|| {
            ui.find_descendant(root, TOGGLE_CHECKBOX_ID)
                .filter(|c| ui.widget::<Button>(*c).is_some())
        })
}

/// The text widget of a row: the element itself or its first text
/// descendant.
fn find_text(ui: &UiRoot, root: ElementId) -> Option<ElementId> {
    if ui.widget::<Text>(root).is_some() {
        return Some(root);
    }
    let mut stack = ui.children(root);
    while let Some(id) = stack.pop() {
        if ui.widget::<Text>(id).is_some() {
            return Some(id);
        }
        stack.extend(ui.children(id));
    }
    None
}

fn set_text(ui: &mut UiRoot, text: ElementId, label: &str, color: Option<Color>) {
    ui.with::<Text, _>(text, |t, _| {
        let c = color.unwrap_or(t.default_color);
        t.set_lines(vec![Line::new(label, c)]);
    });
}

/// Building the pages' rows.
struct Builder<'a, 'b> {
    ui: &'a mut UiRoot,
    sources: &'a OptionsSources<'b>,
    list: ElementId,
    live: &'a LiveValues,
    notices: &'a mut Vec<String>,
}

impl Builder<'_, '_> {
    fn string(&self, key: &str) -> Option<String> {
        (self.sources.strings)(STRING_TABLE, key)
    }

    fn tooltip(&self, key: &str) -> Option<String> {
        self.string(&format!("{key}_Help"))
    }

    /// Build template `index` (detached).
    fn build(&mut self, index: usize) -> Option<ImportedLayout> {
        let t = self
            .ui
            .widget::<ListBox>(self.list)?
            .templates
            .get(index)
            .copied()?;
        let row = import_rooted(
            self.ui,
            self.sources.layouts,
            t.layout,
            t.element,
            self.sources.font.clone(),
        );
        if row.is_none() {
            self.notices
                .push(format!("options: row template {index} did not build"));
        }
        row
    }

    /// Put a built row under the rows already there.
    fn push(&mut self, root: ElementId) {
        let list = self.list;
        self.ui
            .with::<ListBox, _>(list, |l, ui| l.add_prebuilt_row(ui, list, root));
    }

    /// Build template `index` and add it under the rows already there.
    fn add(&mut self, index: usize) -> Option<ImportedLayout> {
        let row = self.build(index)?;
        self.push(row.root);
        Some(row)
    }

    fn header(&mut self, key: &str) {
        let label = self.string(key);
        self.header_text(label.as_deref(), key);
    }

    fn header_text(&mut self, label: Option<&str>, key: &str) {
        let Some(row) = self.add(template::HEADER) else {
            return;
        };
        let Some(text) = find_text(self.ui, row.root) else {
            return;
        };
        match label {
            Some(label) => set_text(self.ui, text, label, None),
            None => self.notices.push(format!(
                "options: header '{key}' did not resolve; the row shows no text"
            )),
        }
    }

    fn separator(&mut self) {
        let _ = self.add(template::SEPARATOR);
    }

    fn label_checkbox(
        &mut self,
        checkbox: ElementId,
        label: Option<String>,
        tooltip: Option<String>,
        color: Color,
    ) {
        let font = self.sources.font.clone();
        self.ui.with::<Button, _>(checkbox, |b, _| {
            if label.is_some() {
                b.label = label;
            }
            b.label_color = color;
            if tooltip.is_some() {
                b.tooltip_text = tooltip;
            }
            if b.label_font.is_none() {
                b.label_font = font;
            }
            b.listeners.click = true;
        });
    }

    /// A checkbox row; its checkbox.
    fn toggle(
        &mut self,
        label: Option<String>,
        tooltip: Option<String>,
        color: Color,
    ) -> Option<ElementId> {
        let row = self.add(template::TOGGLE)?;
        let checkbox = find_checkbox(self.ui, row.root)?;
        self.label_checkbox(checkbox, label, tooltip, color);
        Some(checkbox)
    }

    fn caption_color(store_only: bool) -> Color {
        if store_only {
            STORE_ONLY_CAPTION_COLOR
        } else {
            Color::WHITE
        }
    }

    /// A slider row; its slider.
    #[allow(clippy::too_many_arguments)]
    fn slider(
        &mut self,
        template_index: usize,
        label: Option<String>,
        label_color: Option<Color>,
        tooltip: Option<String>,
        range: (Option<String>, Option<String>),
    ) -> Option<(ElementId, Option<ElementId>)> {
        let row = self.add(template_index)?;
        let caption = row.find(SLIDER_LABEL_ID);
        if let (Some(t), Some(label)) = (caption, label.as_deref()) {
            set_text(self.ui, t, label, label_color);
        }
        for (id, text) in [
            (SLIDER_RANGE_MIN_ID, range.0),
            (SLIDER_RANGE_MAX_ID, range.1),
        ] {
            if let (Some(t), Some(text)) = (row.find(id), text) {
                set_text(self.ui, t, &text, None);
            }
        }
        let slider = row
            .find(SLIDER_ID)
            .filter(|s| self.ui.widget::<Scrollbar>(*s).is_some())?;
        self.ui.with::<Scrollbar, _>(slider, |s, _| {
            s.scalar = true;
            if tooltip.is_some() {
                s.tooltip_text = tooltip;
            }
        });
        Some((slider, caption))
    }

    /// A menu row; its menu.
    fn menu(
        &mut self,
        label: Option<String>,
        label_color: Option<Color>,
        tooltip: Option<String>,
        items: Vec<MenuItem>,
    ) -> Option<ElementId> {
        let row = self.add(template::MENU)?;
        if let (Some(t), Some(label)) = (row.find(MENU_LABEL_ID), label.as_deref()) {
            set_text(self.ui, t, label, label_color);
        }
        let menu = row
            .find(MENU_ID)
            .filter(|m| self.ui.widget::<Menu>(*m).is_some())?;
        let font = self.sources.font.clone();
        self.ui.with::<Menu, _>(menu, |m, _| {
            apply_menu_chrome(m, font);
            m.items = items;
            if tooltip.is_some() {
                m.tooltip_text = tooltip;
            }
        });
        Some(menu)
    }
}

/// OpenAC's look for the options menus.
fn apply_menu_chrome(m: &mut Menu, font: Option<Arc<UiFont>>) {
    use menu_chrome as c;
    if m.font.is_none() {
        m.font.clone_from(&font);
    }
    if m.button_font.is_none() {
        m.button_font = font;
    }
    m.normal_sprite = c::NORMAL;
    m.pressed_sprite = c::PRESSED;
    m.item_normal_sprite = c::NORMAL;
    m.item_highlight_sprite = c::PRESSED;
    m.rows_per_column = c::ROWS_PER_COLUMN;
    m.row_height = c::ROW_HEIGHT;
    m.column_width = c::COLUMN_WIDTH;
    m.scrollable = true;
    m.scrollbar_width = c::SCROLLBAR_WIDTH;
    m.scroll_button_extent = c::SCROLL_BUTTON_EXTENT;
    m.scroll_track_sprite = c::SCROLL_TRACK;
    m.scroll_thumb_top_sprite = c::SCROLL_THUMB_TOP;
    m.scroll_thumb_sprite = c::SCROLL_THUMB;
    m.scroll_thumb_bottom_sprite = c::SCROLL_THUMB_BOTTOM;
    m.scroll_up_sprite = c::SCROLL_UP;
    m.scroll_down_sprite = c::SCROLL_DOWN;
    m.arrow_cap_closed_sprite = c::ARROW_CAP_CLOSED;
    m.arrow_cap_open_sprite = c::ARROW_CAP_OPEN;
    m.open_upward = false;
    m.text_indent = 0.0;
    m.button_text_indent = 0.0;
    m.text_color = Color::WHITE;
    m.button_text_centered = true;
    m.item_text_centered = true;
    m.popup_size_to_content = true;
}

impl OptionsWindowController {
    /// Bind the imported options window `layout`: its buttons, the three
    /// list pages' rows (built from their templates, seeded from `live`),
    /// the footers, and the tabs (Gameplay shown). `None` when the root is
    /// not the tab panel.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        sources: &OptionsSources<'_>,
        live: LiveValues,
    ) -> Option<Self> {
        let root = layout.root;
        ui.widget::<TabPanel>(root)?;
        let mut c = Self {
            root,
            pages: [
                Page {
                    model: OptionPage::new(false),
                    ..Page::default()
                },
                Page {
                    model: OptionPage::new(true),
                    ..Page::default()
                },
                Page {
                    model: OptionPage::new(true),
                    ..Page::default()
                },
                Page {
                    model: OptionPage::new(true),
                    ..Page::default()
                },
            ],
            live,
            rows: HashMap::new(),
            actions: HashMap::new(),
            notices: Vec::new(),
        };
        for (id, action) in [
            (CLOSE_BUTTON_ID, Action::Close),
            (
                EXIT_TO_CHARACTER_SELECTION_ID,
                Action::ExitToCharacterSelection,
            ),
            (EXIT_GAME_ID, Action::ExitGame),
            (
                USE_MOUSE_TURNING_SETTINGS_ID,
                Action::UseMouseTurningSettings,
            ),
            (URGENT_ASSISTANCE_ID, Action::UrgentAssistance),
            (REPORT_ABUSE_ID, Action::ReportAbuse),
            (CONFIGURE_KEYBOARD_ID, Action::ConfigureKeyboard),
        ] {
            c.bind_button(ui, layout.find(id), action, id);
        }
        // The help files have no handler: help is a web page.
        let _ = IN_GAME_HELP_FILES_ID;

        for page in [
            OptionsPage::Character,
            OptionsPage::Chat,
            OptionsPage::Configuration,
        ] {
            c.bind_footer(ui, page);
        }
        c.bind_character_page(ui, layout, sources);
        c.bind_chat_page(ui, layout, sources);
        c.bind_config_page(ui, layout, sources);
        for page in OptionsPage::ALL {
            let i = page.index();
            let changed = c.pages[i].model.changed();
            let gates = (c.pages[i].apply, c.pages[i].reset);
            let rows = std::mem::take(&mut c.pages[i].rows);
            Host {
                ui,
                rows: &rows,
                live: &mut c.live,
                links: Vec::new(),
                gates,
            }
            .option_changed(changed);
            c.pages[i].rows = rows;
        }
        c.refresh_dims(ui);
        ui.with::<TabPanel, _>(root, |t, ui| t.activate(ui, root));
        Some(c)
    }

    fn bind_button(&mut self, ui: &mut UiRoot, button: Option<ElementId>, action: Action, id: u32) {
        match button.filter(|b| ui.widget::<Button>(*b).is_some()) {
            Some(b) => {
                ui.with::<Button, _>(b, |w, _| w.listeners.click = true);
                self.actions.insert(b, action);
            }
            None => self
                .notices
                .push(format!("options: button {id:#010x} is not in the layout")),
        }
    }

    /// The footer's three buttons and the backing behind them (so rows
    /// scrolled under the footer do not show through it).
    fn bind_footer(&mut self, ui: &mut UiRoot, page: OptionsPage) {
        let Some(page_root) = ui.find_descendant(self.root, page.element_id()) else {
            self.notices.push(format!(
                "options: page {:#010x} is not in the layout",
                page.element_id()
            ));
            return;
        };
        let i = page.index();
        let find = |ui: &UiRoot, id| {
            ui.find_descendant(page_root, id)
                .filter(|b| ui.widget::<Button>(*b).is_some())
        };
        let apply = find(ui, APPLY_BUTTON_ID);
        let reset = find(ui, RESET_BUTTON_ID);
        let defaults = find(ui, DEFAULTS_BUTTON_ID);
        for (button, action) in [
            (apply, Action::Apply(i)),
            (reset, Action::Reset(i)),
            (defaults, Action::Defaults(i)),
        ] {
            if let Some(b) = button {
                ui.with::<Button, _>(b, |w, _| w.listeners.click = true);
                self.actions.insert(b, action);
            }
        }
        if apply.is_some() && reset.is_some() {
            self.pages[i].apply = apply;
            self.pages[i].reset = reset;
        }
        let Some(first) = apply.or(reset).or(defaults) else {
            self.notices.push(format!(
                "options: page {:#010x} has no footer buttons",
                page.element_id()
            ));
            return;
        };
        let (top, height) = ui.element(first).map_or((0.0, 0.0), |e| (e.top, e.height));
        let width = ui.element(page_root).map_or(0.0, |e| e.width);
        let mut element = Element::at(0.0, top, width, height);
        element.z_order = FOOTER_BACKING_Z_ORDER;
        element.click_through = true;
        let backing = Panel {
            background_sprite: chrome::CENTER_FILL,
            border_color: Color::rgba(0.0, 0.0, 0.0, 0.0),
            border_thickness: 0.0,
            ..Panel::default()
        };
        ui.add(page_root, element, backing);
    }

    /// The page list `list_id` with its scrollbar linked; the list.
    fn page_list(
        &mut self,
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        list_id: u32,
        page: OptionsPage,
        scrollbar_id: Option<u32>,
    ) -> Option<ElementId> {
        let Some(list) = layout
            .find(list_id)
            .filter(|l| ui.widget::<ListBox>(*l).is_some())
        else {
            self.notices.push(format!(
                "options: list {list_id:#010x} is not in the layout; its page has no rows"
            ));
            return None;
        };
        let scrollbar_id = scrollbar_id
            .or_else(|| ui.widget::<ListBox>(list).map(|l| l.scrollbar_element_id))
            .unwrap_or(0);
        let bar = ui
            .find_descendant(self.root, page.element_id())
            .and_then(|p| ui.find_descendant(p, scrollbar_id))
            .filter(|b| ui.widget::<Scrollbar>(*b).is_some());
        let viewport = ui.with::<ListBox, _>(list, |l, ui| l.viewport(ui, list));
        match (bar, viewport) {
            (Some(bar), Some(v)) => {
                ui.with::<Scrollbar, _>(bar, |b, _| b.model = ScrollSource::Element(v));
            }
            _ => self.notices.push(format!(
                "options: list {list_id:#010x} has no scrollbar {scrollbar_id:#010x}"
            )),
        }
        Some(list)
    }

    fn register(
        &mut self,
        page: usize,
        binding: Binding,
        initial: OptionValue,
        default: OptionValue,
    ) {
        let row = self.pages[page].model.register(initial, default);
        let element = match &binding.widget {
            Widget::Checkbox(e) | Widget::Bitfield(e) => *e,
            Widget::Slider { slider, .. } => *slider,
            Widget::Menu { menu, .. } => *menu,
        };
        self.rows.insert(element, (page, row));
        self.pages[page].rows.push(binding);
    }

    fn bind_character_page(
        &mut self,
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        sources: &OptionsSources<'_>,
    ) {
        let page = OptionsPage::Character.index();
        let Some(list) = self.page_list(
            ui,
            layout,
            CHARACTER_LIST_ID,
            OptionsPage::Character,
            Some(CHARACTER_SCROLLBAR_ID),
        ) else {
            return;
        };
        let live = self.live.clone();
        let mut notices = Vec::new();
        let mut bound = Vec::new();
        {
            let mut b = Builder {
                ui,
                sources,
                list,
                live: &live,
                notices: &mut notices,
            };
            for group in spec::CHARACTER_GROUPS {
                b.header(group.header_key);
                for row in group.rows {
                    let key = format!("ID_PlayerOption_{}", row.retail_name);
                    let label = b.string(&key);
                    if label.is_none() {
                        b.notices.push(format!(
                            "options: '{key}' did not resolve; the row has no caption"
                        ));
                    }
                    let tooltip = b.tooltip(&key);
                    let Some(checkbox) =
                        b.toggle(label, tooltip, Builder::caption_color(row.store_only))
                    else {
                        continue;
                    };
                    let Some(entry) = ac_runtime::character::option(row.id) else {
                        continue;
                    };
                    let initial = b
                        .live
                        .character_option(row.id)
                        .unwrap_or(entry.client_default);
                    b.ui.with::<Button, _>(checkbox, |w, ui| w.set_selected(ui, checkbox, initial));
                    bound.push((
                        Binding {
                            key: OptionKey::Character(row.id),
                            widget: Widget::Checkbox(checkbox),
                            dimmed_caption: None,
                        },
                        OptionValue::Bool(initial),
                        OptionValue::Bool(entry.client_default),
                    ));
                }
                b.separator();
            }
        }
        self.notices.extend(notices);
        for (binding, initial, default) in bound {
            self.register(page, binding, initial, default);
        }
    }

    fn bind_chat_page(
        &mut self,
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        sources: &OptionsSources<'_>,
    ) {
        let page = OptionsPage::Chat.index();
        let Some(list) = self.page_list(
            ui,
            layout,
            CHAT_LIST_ID,
            OptionsPage::Chat,
            Some(CHAT_SCROLLBAR_ID),
        ) else {
            return;
        };
        let live = self.live.clone();
        let mut notices = Vec::new();
        let mut bound = Vec::new();
        {
            let mut b = Builder {
                ui,
                sources,
                list,
                live: &live,
                notices: &mut notices,
            };
            b.header("ID_ChatOption_GeneralOptions_Section");
            let (default_caption, active_caption) = &sources.opacity_captions;
            let first = b.slider(
                template::SIMPLE_SLIDER,
                default_caption.name.clone(),
                None,
                default_caption.tooltip.clone(),
                (None, None),
            );
            let second = b.slider(
                template::RANGED_SLIDER,
                active_caption.name.clone(),
                None,
                active_caption.tooltip.clone(),
                (
                    b.string("ID_UI_Value_Transparent"),
                    b.string("ID_UI_Value_Opaque"),
                ),
            );
            if let (Some((s1, _)), Some((s2, _))) = (first, second) {
                let chat = &b.live.settings.chat;
                for (slider, field, initial, default) in [
                    (
                        s1,
                        ChatField::DefaultOpacity,
                        chat.default_opacity,
                        sources.opacity_defaults.0,
                    ),
                    (
                        s2,
                        ChatField::ActiveOpacity,
                        chat.active_opacity,
                        sources.opacity_defaults.1,
                    ),
                ] {
                    let widget = Widget::Slider {
                        slider,
                        min: 0.0,
                        max: 1.0,
                        snap: None,
                        value_caption: None,
                    };
                    show(b.ui, &widget, &OptionValue::Float(initial));
                    bound.push((
                        Binding {
                            key: OptionKey::Chat(field),
                            widget,
                            dimmed_caption: None,
                        },
                        OptionValue::Float(initial),
                        OptionValue::Float(default),
                    ));
                }
            }
            b.separator();
            for block in spec::FILTER_BLOCKS {
                b.header(block.header_key);
                if let Some(binding) = build_filter_block(&mut b, &block) {
                    let initial = b
                        .live
                        .settings
                        .chat
                        .filter(block.window)
                        .unwrap_or(block.default_filter);
                    show(b.ui, &binding.widget, &OptionValue::Bits(initial));
                    bound.push((
                        binding,
                        OptionValue::Bits(initial),
                        OptionValue::Bits(block.default_filter),
                    ));
                }
                b.separator();
            }
        }
        self.notices.extend(notices);
        for (binding, initial, default) in bound {
            self.register(page, binding, initial, default);
        }
    }

    #[allow(clippy::too_many_lines)]
    fn bind_config_page(
        &mut self,
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        sources: &OptionsSources<'_>,
    ) {
        let page = OptionsPage::Configuration.index();
        let Some(list) =
            self.page_list(ui, layout, CONFIG_LIST_ID, OptionsPage::Configuration, None)
        else {
            return;
        };
        let live = self.live.clone();
        let mut notices = Vec::new();
        let mut bound: Vec<(Binding, OptionValue, OptionValue)> = Vec::new();
        {
            let mut b = Builder {
                ui,
                sources,
                list,
                live: &live,
                notices: &mut notices,
            };
            for row in spec::config_rows() {
                match row {
                    ConfigRow::Header(key) => b.header(key),
                    ConfigRow::ExplicitHeader(label) => b.header_text(Some(label), label),
                    ConfigRow::Separator => b.separator(),
                    ConfigRow::Toggle {
                        key,
                        default,
                        target,
                        store_only,
                    } => {
                        let label = b.string(key);
                        let tooltip = b.tooltip(key);
                        if let Some(checkbox) =
                            b.toggle(label, tooltip, Builder::caption_color(store_only))
                        {
                            bound.push(seeded(
                                &mut b,
                                target,
                                Widget::Checkbox(checkbox),
                                OptionValue::Bool(default),
                                None,
                            ));
                        }
                    }
                    ConfigRow::Slider {
                        template: t,
                        key,
                        min,
                        max,
                        default,
                        target,
                        store_only,
                        range,
                    } => {
                        let label = b.string(key);
                        let tooltip = b.tooltip(key);
                        let range =
                            range.map_or((None, None), |(lo, hi)| (b.string(lo), b.string(hi)));
                        let color = store_only.then_some(STORE_ONLY_CAPTION_COLOR);
                        if let Some((slider, _)) = b.slider(t, label, color, tooltip, range) {
                            bound.push(seeded(
                                &mut b,
                                target,
                                Widget::Slider {
                                    slider,
                                    min: f64::from(min),
                                    max: f64::from(max),
                                    snap: None,
                                    value_caption: None,
                                },
                                OptionValue::Float(default),
                                None,
                            ));
                        }
                    }
                    ConfigRow::Trio {
                        toggle_key,
                        slider_tooltip_key,
                        toggle_default,
                        slider_default,
                        toggle_target,
                        slider_target,
                    } => {
                        let Some(row) = b.add(template::TRIO) else {
                            continue;
                        };
                        let parts =
                            b.ui.widget::<OptionToggleSlider>(row.root)
                                .copied()
                                .unwrap_or_default();
                        let (Some(checkbox), Some(slider)) = (parts.toggle, parts.slider) else {
                            b.notices.push(format!(
                                "options: the row for '{toggle_key}' lacks its checkbox or slider"
                            ));
                            continue;
                        };
                        let label = b.string(toggle_key);
                        let tooltip = b.tooltip(toggle_key);
                        b.label_checkbox(checkbox, label, tooltip, Color::WHITE);
                        bound.push(seeded(
                            &mut b,
                            toggle_target,
                            Widget::Checkbox(checkbox),
                            OptionValue::Bool(toggle_default),
                            None,
                        ));
                        let tooltip = b.tooltip(slider_tooltip_key);
                        b.ui.with::<Scrollbar, _>(slider, |s, _| {
                            s.scalar = true;
                            if tooltip.is_some() {
                                s.tooltip_text = tooltip;
                            }
                        });
                        bound.push(seeded(
                            &mut b,
                            slider_target,
                            Widget::Slider {
                                slider,
                                min: 0.0,
                                max: 1.0,
                                snap: None,
                                value_caption: None,
                            },
                            OptionValue::Float(slider_default),
                            None,
                        ));
                    }
                    ConfigRow::Menu {
                        key,
                        choices,
                        payloads,
                        default,
                        target,
                        store_only,
                    } => {
                        let labels: Vec<String> = choices
                            .iter()
                            .map(|k| b.string(k).unwrap_or_default())
                            .collect();
                        let values: Vec<OptionValue> = (0..choices.len())
                            .map(|i| OptionValue::Int(payloads.map_or(i as i32, |p| p[i])))
                            .collect();
                        let items = labels
                            .iter()
                            .enumerate()
                            .map(|(i, l)| MenuItem::new(l.clone(), i as u64))
                            .collect();
                        let label = b.string(key);
                        let tooltip = b.tooltip(key);
                        let color = store_only.then_some(STORE_ONLY_CAPTION_COLOR);
                        if let Some(menu) = b.menu(label, color, tooltip, items) {
                            bound.push(seeded(
                                &mut b,
                                target,
                                Widget::Menu {
                                    menu,
                                    values,
                                    labels,
                                    missing_shows_value: false,
                                },
                                OptionValue::Int(default),
                                None,
                            ));
                        }
                    }
                    ConfigRow::Resolution { key } => {
                        let choices = sources.resolutions.clone();
                        let items = choices
                            .iter()
                            .enumerate()
                            .map(|(i, l)| MenuItem::new(l.clone(), i as u64))
                            .collect();
                        let label = b.string(key);
                        let tooltip = b.tooltip(key);
                        let default = choices.last().cloned().unwrap_or_else(|| {
                            ac_host::settings::DisplaySettings::default().resolution
                        });
                        if let Some(menu) = b.menu(label, None, tooltip, items) {
                            bound.push(seeded(
                                &mut b,
                                OptionKey::Display(DisplayField::Resolution),
                                Widget::Menu {
                                    menu,
                                    values: choices
                                        .iter()
                                        .cloned()
                                        .map(OptionValue::Text)
                                        .collect(),
                                    labels: choices,
                                    missing_shows_value: true,
                                },
                                OptionValue::Text(default),
                                None,
                            ));
                        }
                    }
                    ConfigRow::ExplicitToggle {
                        label,
                        default,
                        target,
                        tooltip,
                        dimmed_by_retail_mixer,
                    } => {
                        if let Some(checkbox) = b.toggle(
                            Some(label.to_owned()),
                            (!tooltip.is_empty()).then(|| tooltip.to_owned()),
                            Color::WHITE,
                        ) {
                            bound.push(seeded(
                                &mut b,
                                target,
                                Widget::Checkbox(checkbox),
                                OptionValue::Bool(default),
                                dimmed_by_retail_mixer.then_some(checkbox),
                            ));
                        }
                    }
                    ConfigRow::ExplicitSlider {
                        label,
                        min,
                        max,
                        step,
                        integer,
                        default,
                        target,
                        tooltip,
                        dimmed_by_retail_mixer,
                        range_low,
                        range_high,
                        value_suffix,
                    } => {
                        let number = |v: f64| {
                            if integer {
                                format!("{}", v.round() as i64)
                            } else {
                                format!("{v}")
                            }
                        };
                        let range = (
                            Some(range_low.map_or_else(|| number(min), str::to_owned)),
                            Some(range_high.map_or_else(|| number(max), str::to_owned)),
                        );
                        if let Some((slider, caption)) = b.slider(
                            template::RANGED_SLIDER,
                            Some(label.to_owned()),
                            None,
                            (!tooltip.is_empty()).then(|| tooltip.to_owned()),
                            range,
                        ) {
                            let default = snap(default, min, max, step, integer) as f32;
                            bound.push(seeded(
                                &mut b,
                                target,
                                Widget::Slider {
                                    slider,
                                    min,
                                    max,
                                    snap: Some((step, integer)),
                                    value_caption: caption
                                        .zip(value_suffix)
                                        .map(|(text, suffix)| (text, label, suffix)),
                                },
                                OptionValue::Float(default),
                                caption.filter(|_| dimmed_by_retail_mixer),
                            ));
                        }
                    }
                    ConfigRow::ExplicitMenu {
                        label,
                        choices,
                        default,
                        target,
                    } => {
                        let items = choices
                            .iter()
                            .enumerate()
                            .map(|(i, ch)| {
                                MenuItem::new(ch.label, i as u64).with_enabled(ch.enabled)
                            })
                            .collect();
                        let initial = live.read(target);
                        let tooltip = choices
                            .iter()
                            .find(|ch| Some(OptionValue::Text(ch.id.to_owned())) == initial)
                            .filter(|ch| !ch.tooltip.is_empty())
                            .map(|ch| ch.tooltip.to_owned());
                        if let Some(menu) = b.menu(Some(label.to_owned()), None, tooltip, items) {
                            bound.push(seeded(
                                &mut b,
                                target,
                                Widget::Menu {
                                    menu,
                                    values: choices
                                        .iter()
                                        .map(|ch| OptionValue::Text(ch.id.to_owned()))
                                        .collect(),
                                    labels: choices.iter().map(|ch| ch.label.to_owned()).collect(),
                                    missing_shows_value: true,
                                },
                                OptionValue::Text(default.to_owned()),
                                None,
                            ));
                        }
                    }
                }
            }
        }
        self.notices.extend(notices);
        for (binding, initial, default) in bound {
            self.register(page, binding, initial, default);
        }
    }

    /// The mixer rows the retail mixer overrides show grey while it is on.
    fn refresh_dims(&self, ui: &mut UiRoot) {
        let dim = self.live.settings.mixer.retail_mixer;
        let color = if dim {
            STORE_ONLY_CAPTION_COLOR
        } else {
            Color::WHITE
        };
        for binding in &self.pages[OptionsPage::Configuration.index()].rows {
            // Both Mouse Buttons Run Forward needs modern mouse turning.
            if binding.key == OptionKey::Camera(CameraField::BothMouseButtonsRunForward)
                && let Widget::Checkbox(checkbox) = binding.widget
            {
                let color = if spec::unavailable(binding.key, &self.live.settings) {
                    STORE_ONLY_CAPTION_COLOR
                } else {
                    Color::WHITE
                };
                ui.with::<Button, _>(checkbox, |b, _| b.label_color = color);
            }
            let Some(caption) = binding.dimmed_caption else {
                continue;
            };
            if ui.widget::<Button>(caption).is_some() {
                ui.with::<Button, _>(caption, |b, _| b.label_color = color);
            } else {
                ui.with::<Text, _>(caption, |t, _| {
                    let lines: Vec<Line> = match t.content() {
                        crate::widgets::TextContent::Lines(lines) => lines
                            .iter()
                            .map(|l| Line::new(l.text.clone(), color))
                            .collect(),
                        _ => Vec::new(),
                    };
                    if !lines.is_empty() {
                        t.set_lines(lines);
                    }
                });
            }
        }
    }

    /// Run `f` on page `page`'s model with its host.
    fn with_page(
        &mut self,
        ui: &mut UiRoot,
        page: usize,
        f: impl FnOnce(&mut OptionPage, &mut Host<'_>),
    ) {
        let Self { pages, live, .. } = self;
        let p = &mut pages[page];
        let mut host = Host {
            ui,
            rows: &p.rows,
            live,
            links: Vec::new(),
            gates: (p.apply, p.reset),
        };
        f(&mut p.model, &mut host);
        self.refresh_dims(ui);
    }

    /// The page shown.
    #[must_use]
    pub fn active_page(&self, ui: &UiRoot) -> Option<OptionsPage> {
        ui.widget::<TabPanel>(self.root)
            .and_then(|t| OptionsPage::of(t.active_page()))
    }

    /// Show page `page` (as a click on its tab).
    pub fn show_page(&mut self, ui: &mut UiRoot, page: OptionsPage) {
        let root = self.root;
        ui.with::<TabPanel, _>(root, |t, ui| t.switch_to(ui, root, page.element_id()));
    }

    /// The window's root (the tab panel).
    #[must_use]
    pub fn root(&self) -> ElementId {
        self.root
    }

    /// The live values the rows read.
    #[must_use]
    pub fn live(&self) -> &LiveValues {
        &self.live
    }

    /// The live values changed elsewhere (a console command, the server's
    /// option words): the rows read them at the next Apply, show or reload.
    pub fn set_live(&mut self, live: LiveValues) {
        self.live = live;
    }

    /// The character's options arrived from the server: the page shown
    /// re-reads its rows (nothing is sent back).
    pub fn on_server_options_seeded(&mut self, ui: &mut UiRoot) {
        if let Some(page) = self.active_page(ui) {
            self.with_page(ui, page.index(), |m, h| m.reload_from_live(h));
        }
    }

    /// Apply page `page` (the mouse-turning settings button applies the
    /// Configuration page after changing the camera).
    pub fn apply_page(&mut self, ui: &mut UiRoot, page: OptionsPage) {
        self.with_page(ui, page.index(), |m, h| m.apply(h));
    }

    /// Page `page`'s model (tests and probes).
    #[must_use]
    pub fn page(&self, page: OptionsPage) -> &OptionPage {
        &self.pages[page.index()].model
    }

    /// Notices from binding (missing elements, strings).
    pub fn take_notices(&mut self) -> Vec<String> {
        std::mem::take(&mut self.notices)
    }

    fn row_value(
        &self,
        ui: &UiRoot,
        page: usize,
        row: usize,
        message: &ElementMessage,
    ) -> Option<OptionValue> {
        let binding = self.pages[page].rows.get(row)?;
        match (&binding.widget, message) {
            (Widget::Checkbox(b), ElementMessage::Clicked { .. }) => {
                let w = ui.widget::<Button>(*b)?;
                // A toggle button flipped itself on the press's release.
                let selected = if w.toggle_behavior {
                    w.selected()
                } else {
                    !w.selected()
                };
                Some(OptionValue::Bool(selected))
            }
            (
                Widget::Slider {
                    min, max, snap: s, ..
                },
                ElementMessage::ScalarChanged(n),
            ) => {
                let raw = min + f64::from(*n) * (max - min);
                let v = match s {
                    Some((step, integer)) => snap(raw, *min, *max, *step, *integer),
                    None => raw,
                };
                Some(OptionValue::Float(v as f32))
            }
            (Widget::Menu { values, .. }, ElementMessage::MenuSelected(payload)) => {
                values.get(usize::try_from(*payload).ok()?).cloned()
            }
            (Widget::Bitfield(_), ElementMessage::BitfieldChanged { low, high }) => {
                Some(OptionValue::Bits(combine_mask(*low, *high)))
            }
            _ => None,
        }
    }

    fn act(&mut self, ui: &mut UiRoot, action: Action) {
        match action {
            Action::Close => ui.emit(UiOutput::Panel(PanelOutput::ClosePanel(panel_id::OPTIONS))),
            Action::ExitToCharacterSelection => emit(ui, OptionsOutput::ExitToCharacterSelection),
            Action::ExitGame => emit(ui, OptionsOutput::ExitGame),
            Action::UseMouseTurningSettings => emit(ui, OptionsOutput::UseMouseTurningSettings),
            Action::UrgentAssistance => {
                emit(
                    ui,
                    OptionsOutput::SystemMessage(urgent_assistance_unavailable()),
                );
            }
            Action::ReportAbuse => {
                emit(ui, OptionsOutput::SystemMessage(report_abuse_unavailable()));
            }
            Action::ConfigureKeyboard => emit(ui, OptionsOutput::ConfigureKeyboard),
            Action::Apply(page) => self.with_page(ui, page, |m, h| m.apply(h)),
            Action::Reset(page) => self.with_page(ui, page, |m, h| m.reset(h)),
            Action::Defaults(page) => self.with_page(ui, page, |m, h| m.defaults(h)),
        }
    }
}

/// A row seeded from the live value of `target` (`default` when it has
/// none), shown on its widget.
fn seeded(
    b: &mut Builder<'_, '_>,
    target: OptionKey,
    widget: Widget,
    default: OptionValue,
    dimmed_caption: Option<ElementId>,
) -> (Binding, OptionValue, OptionValue) {
    let initial = b.live.read(target).unwrap_or_else(|| default.clone());
    let initial = match (&widget, initial) {
        (
            Widget::Slider {
                min,
                max,
                snap: Some((step, integer)),
                ..
            },
            OptionValue::Float(v),
        ) => OptionValue::Float(snap(f64::from(v), *min, *max, *step, *integer) as f32),
        (_, v) => v,
    };
    show(b.ui, &widget, &initial);
    (
        Binding {
            key: target,
            widget,
            dimmed_caption,
        },
        initial,
        default,
    )
}

/// One chat window's filter block: the bitfield template filled with the
/// filter rows (the main window has no Gameplay row), added to the list.
fn build_filter_block(b: &mut Builder<'_, '_>, block: &spec::FilterBlock) -> Option<Binding> {
    let row = b.build(template::BITFIELD)?;
    let field = row.root;
    if b.ui.widget::<Bitfield64>(field).is_none() {
        b.notices.push(format!(
            "options: the filter block of window {} is not a checkbox bitfield",
            block.retail_window_id
        ));
        return None;
    }
    let row_template =
        b.ui.widget::<Bitfield64>(field)
            .and_then(|f| f.templates.first().copied());
    let (low, high) = split_mask(block.default_filter);
    b.ui.with::<Bitfield64, _>(field, |f, _| f.set_default_value(low, high));
    for (i, filter) in spec::FILTER_ROWS.iter().enumerate() {
        if i == 0 && !block.includes_gameplay_row {
            continue;
        }
        let label = (b.sources.strings)(FILTER_STRING_TABLE, filter.label_key);
        let tooltip =
            (b.sources.strings)(FILTER_STRING_TABLE, &format!("{}_Desc", filter.label_key));
        let Some(t) = row_template else {
            continue;
        };
        let Some(built) = import_rooted(
            b.ui,
            b.sources.layouts,
            t.layout,
            t.element,
            b.sources.font.clone(),
        ) else {
            continue;
        };
        let (lo, hi) = split_mask(filter.mask);
        let label = label.unwrap_or_default();
        let checkbox = b.ui.with::<Bitfield64, _>(field, |f, ui| {
            f.add_row(ui, field, built.root, lo, hi, &label, tooltip.as_deref())
        });
        if let Some(Some(checkbox)) = checkbox {
            let font = b.sources.font.clone();
            b.ui.with::<Button, _>(checkbox, |w, _| {
                if w.label_font.is_none() {
                    w.label_font = font;
                }
            });
        }
    }
    // Added once it has grown to its rows, so the rows after it start
    // below its final height.
    b.push(field);
    Some(Binding {
        key: OptionKey::Chat(ChatField::Filter(block.window)),
        widget: Widget::Bitfield(field),
        dimmed_caption: None,
    })
}

impl PanelController for OptionsWindowController {
    fn on_shown(&mut self, ui: &mut UiRoot) {
        if let Some(page) = self.active_page(ui) {
            self.with_page(ui, page.index(), |m, h| m.on_shown(h));
        }
    }

    fn on_hidden(&mut self, ui: &mut UiRoot) {
        if let Some(page) = self.active_page(ui) {
            self.with_page(ui, page.index(), |m, h| m.on_hidden(h));
        }
    }

    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if element == self.root
            && let ElementMessage::TabChanged { previous, page } = message
        {
            if let Some(p) = OptionsPage::of(*previous) {
                self.with_page(ui, p.index(), |m, h| m.on_hidden(h));
            }
            if let Some(p) = OptionsPage::of(*page) {
                self.with_page(ui, p.index(), |m, h| m.on_shown(h));
            }
            return true;
        }
        if let (Some(&action), ElementMessage::Clicked { .. }) =
            (self.actions.get(&element), message)
        {
            self.act(ui, action);
            return true;
        }
        let Some(&(page, row)) = self.rows.get(&element) else {
            return false;
        };
        if matches!(message, ElementMessage::DragCompleted) {
            // An opacity or interface-scale slider settles: apply and save
            // what the drag left.
            let section = match self.pages[page].rows.get(row).map(|b| b.key) {
                Some(OptionKey::Chat(ChatField::DefaultOpacity | ChatField::ActiveOpacity)) => {
                    Some(Section::Chat)
                }
                Some(OptionKey::Display(DisplayField::UiScale)) => Some(Section::Display),
                _ => None,
            };
            if let Some(section) = section {
                emit(
                    ui,
                    OptionsOutput::Changed {
                        section,
                        persist: true,
                    },
                );
            }
            return true;
        }
        let Some(value) = self.row_value(ui, page, row, message) else {
            return false;
        };
        // A row the settings make unavailable keeps its value: the widget
        // that flipped itself shows it again.
        if let Some(binding) = self.pages[page].rows.get(row)
            && spec::unavailable(binding.key, &self.live.settings)
        {
            if let Some(current) = self.pages[page].model.current(row).cloned() {
                show(ui, &binding.widget, &current);
            }
            return true;
        }
        self.with_page(ui, page, |m, h| m.set_current(row, value, h));
        true
    }
}

#[cfg(test)]
mod tests;
