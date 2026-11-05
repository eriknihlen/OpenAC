//! The drop-down menu: a button showing the current choice and a popup of
//! entries, in columns or as one scrolling column. The popup draws in the
//! overlay layer and is the root's live popup while open (a press outside
//! it closes it).
//!
//! Callbacks are messages: [`ElementMessage::MenuOpening`] as the popup
//! opens (rebuild the entries now — the next draw shows them) and
//! [`ElementMessage::MenuSelected`] with the picked entry's payload.

use std::sync::Arc;

use crate::draw::{Color, DEFAULT_OUTLINE_COLOR, RenderContext, TextureRef};
use crate::event::{Key, UiEvent, kind};
use crate::font::UiFont;
use crate::plugin::Palette;
use crate::plugin::style::{self, PointerState};
use crate::plugin::widgets::ButtonLook;
use crate::root::{ElementMessage, UiRoot};
use crate::text::Scrollable;
use crate::tree::{Element, ElementId, Widget};
use crate::widgets::Field;
use crate::widgets::chrome;
use crate::widgets::scrollbar::thumb_rect;

/// One entry: its caption, the payload a pick reports, and whether it can
/// be picked (a disabled entry is greyed and inert).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MenuItem {
    /// Caption.
    pub label: String,
    /// Reported by [`ElementMessage::MenuSelected`].
    pub payload: u64,
    /// Can be picked.
    pub enabled: bool,
}

impl MenuItem {
    /// An enabled entry.
    #[must_use]
    pub fn new(label: impl Into<String>, payload: u64) -> Self {
        Self {
            label: label.into(),
            payload,
            enabled: true,
        }
    }

    /// This entry, enabled or not.
    #[must_use]
    pub fn with_enabled(mut self, enabled: bool) -> Self {
        self.enabled = enabled;
        self
    }
}

const BORDER: f32 = chrome::BORDER as f32;
const FACE_CAP_L: f32 = 20.0;
const FACE_CAP_R: f32 = 12.0;
const SCROLL_CAP_H: f32 = 3.0;
/// Inset of plain-style captions, pixels.
pub const PLAIN_PADDING: f32 = 3.0;
/// Room the plain look leaves right of the button caption for its chevron,
/// pixels.
const PLAIN_CHEVRON_RESERVE: f32 = 18.0;

/// A fitted button caption and what it was fitted for, so an unchanged
/// caption is not measured again each frame.
#[derive(Debug)]
struct CaptionFit {
    source: String,
    font: Option<Arc<UiFont>>,
    max_width: f32,
    shown: String,
}

/// A drop-down menu.
#[derive(Debug)]
pub struct Menu {
    /// The entries.
    pub items: Vec<MenuItem>,
    /// The payload of the current choice (highlighted in the popup).
    pub selected: Option<u64>,
    /// The button's caption.
    pub button_label: String,
    /// Run-time tooltip.
    pub tooltip_text: Option<String>,
    /// Rows per column (the visible window when scrollable).
    pub rows_per_column: usize,
    /// Row height, pixels.
    pub row_height: f32,
    /// Column width, pixels.
    pub column_width: f32,
    /// One scrolling column instead of a grid.
    pub scrollable: bool,
    /// The popup's scroll model.
    pub popup_scroll: Scrollable,
    /// Popup scrollbar width, pixels.
    pub scrollbar_width: f32,
    /// Popup scrollbar arrow extent, pixels.
    pub scroll_button_extent: f32,
    /// Popup scrollbar track.
    pub scroll_track_sprite: u32,
    /// Popup scrollbar thumb.
    pub scroll_thumb_sprite: u32,
    /// Popup scrollbar thumb top cap.
    pub scroll_thumb_top_sprite: u32,
    /// Popup scrollbar thumb bottom cap.
    pub scroll_thumb_bottom_sprite: u32,
    /// Popup scrollbar up arrow.
    pub scroll_up_sprite: u32,
    /// Popup scrollbar down arrow.
    pub scroll_down_sprite: u32,
    /// Hide the popup scrollbar (and its width) while everything fits.
    pub popup_scrollbar_hide_when_disabled: bool,
    /// Entry caption indent, pixels.
    pub text_indent: f32,
    /// Button caption indent, pixels.
    pub button_text_indent: f32,
    /// Arrow cap, closed.
    pub arrow_cap_closed_sprite: u32,
    /// Arrow cap, open.
    pub arrow_cap_open_sprite: u32,
    /// Arrow cap width, pixels.
    pub arrow_cap_width: f32,
    /// Arrow cap height, pixels.
    pub arrow_cap_height: f32,
    /// Entry font.
    pub font: Option<Arc<UiFont>>,
    /// Button caption font (else the entry font).
    pub button_font: Option<Arc<UiFont>>,
    /// Outlined captions.
    pub outline: bool,
    /// Outline colour.
    pub outline_color: Color,
    /// Button face.
    pub normal_sprite: u32,
    /// Button face while pressed.
    pub pressed_sprite: u32,
    /// Popup panel fill.
    pub popup_bg_sprite: u32,
    /// Entry row.
    pub item_normal_sprite: u32,
    /// Selected entry row.
    pub item_highlight_sprite: u32,
    /// A scrolling popup is as tall as its entries.
    pub popup_size_to_content: bool,
    /// Button caption centred.
    pub button_text_centered: bool,
    /// Entry captions centred.
    pub item_text_centered: bool,
    /// Button caption colour.
    pub text_color: Color,
    /// Enabled entry colour.
    pub text_color_available: Color,
    /// Disabled entry colour.
    pub text_color_ghosted: Color,
    /// Retail art; plain colours otherwise.
    pub retail_button_art: bool,
    /// Plain background.
    pub plain_background_color: Color,
    /// Plain border.
    pub plain_border_color: Color,
    /// Plain border while open or pressed.
    pub plain_open_border_color: Color,
    /// Plain caption colour.
    pub plain_text_color: Color,
    /// Plain arrow colour.
    pub plain_triangle_color: Color,
    /// Plain selected row.
    pub plain_selected_color: Color,
    /// Plain hovered row.
    pub plain_hover_color: Color,
    /// The plugin theme the plain look draws in (none: flat).
    pub theme_palette: Option<Palette>,
    /// The popup opens above the button (else below).
    pub open_upward: bool,
    /// A search band above the entries keeps those whose caption holds
    /// every word typed, ignoring case; filtering never picks (OpenAC
    /// `UiMenu.Searchable`).
    pub searchable: bool,
    /// A plain popup draws its scrollbar flat even when art is set
    /// (themed menus).
    pub plain_popup_scrollbar: bool,
    /// Widens the popup to its longest entry (never below
    /// [`Self::column_width`] or the button), shifts it left so it stays
    /// inside its window (or the screen, when it is wider than the window),
    /// and ends a button caption too long for the button in an ellipsis.
    /// Off for retail dat menus, whose widths are authored.
    pub fits_long_labels: bool,

    open: bool,
    /// The widest entry plus its padding, as measured at opening or when
    /// the entries were replaced while open.
    items_fit_width: f32,
    /// The button's width, as last seen with the element.
    fit_button_width: f32,
    /// How far left of the button the popup starts (0 or negative), as last
    /// placed: hit testing has no access to the tree, so the placement is
    /// refreshed whenever the menu sees its element.
    popup_left: f32,
    /// The last fitted button caption.
    caption_fit: Option<CaptionFit>,
    face_pressed: bool,
    pointer: PointerState,
    hovered_popup_index: Option<usize>,
    dragging_popup_thumb: bool,
    popup_thumb_drag_offset: f32,
    /// The search band's text entry.
    search: Box<Field>,
    /// The search text as last filtered by.
    search_version: u64,
    /// A press in the search band is selecting its text.
    selecting_search: bool,
}

impl Default for Menu {
    fn default() -> Self {
        Self {
            items: Vec::new(),
            selected: None,
            button_label: String::new(),
            tooltip_text: None,
            rows_per_column: 7,
            row_height: 17.0,
            column_width: 191.0,
            scrollable: false,
            popup_scroll: Scrollable::default(),
            scrollbar_width: 16.0,
            scroll_button_extent: 16.0,
            scroll_track_sprite: 0,
            scroll_thumb_sprite: 0,
            scroll_thumb_top_sprite: 0,
            scroll_thumb_bottom_sprite: 0,
            scroll_up_sprite: 0,
            scroll_down_sprite: 0,
            popup_scrollbar_hide_when_disabled: false,
            text_indent: 19.0,
            button_text_indent: 20.0,
            arrow_cap_closed_sprite: 0,
            arrow_cap_open_sprite: 0,
            arrow_cap_width: 17.0,
            arrow_cap_height: 19.0,
            font: None,
            button_font: None,
            outline: false,
            outline_color: DEFAULT_OUTLINE_COLOR,
            normal_sprite: 0,
            pressed_sprite: 0,
            popup_bg_sprite: 0,
            item_normal_sprite: 0,
            item_highlight_sprite: 0,
            popup_size_to_content: false,
            button_text_centered: false,
            item_text_centered: false,
            text_color: Color::rgba(1.0, 0.92, 0.72, 1.0),
            text_color_available: Color::WHITE,
            text_color_ghosted: Color::rgba(0.5, 0.5, 0.5, 1.0),
            retail_button_art: true,
            plain_background_color: Color::rgba(0.0, 0.0, 0.0, 0.92),
            plain_border_color: Color::rgba(0.46, 0.37, 0.16, 1.0),
            plain_open_border_color: Color::rgba(0.70, 0.58, 0.24, 1.0),
            plain_text_color: Color::rgba(0.91, 0.87, 0.76, 1.0),
            plain_triangle_color: Color::rgba(0.91, 0.87, 0.76, 1.0),
            plain_selected_color: Color::rgba(0.28, 0.23, 0.08, 0.95),
            plain_hover_color: Color::rgba(0.40, 0.33, 0.14, 0.95),
            theme_palette: None,
            open_upward: true,
            searchable: false,
            plain_popup_scrollbar: false,
            fits_long_labels: false,
            open: false,
            items_fit_width: 0.0,
            fit_button_width: 0.0,
            popup_left: 0.0,
            caption_fit: None,
            face_pressed: false,
            pointer: PointerState::default(),
            hovered_popup_index: None,
            dragging_popup_thumb: false,
            popup_thumb_drag_offset: 0.0,
            search: {
                let mut field = Box::<Field>::default();
                field.record_history = false;
                field.selectable = true;
                field.max_characters = 256;
                field
            },
            search_version: 0,
            selecting_search: false,
        }
    }
}

impl Menu {
    /// The element flags a menu starts with: presses on it open the popup,
    /// not a window drag.
    pub fn configure(element: &mut Element) {
        element.captures_pointer_drag = true;
        element.click_through = false;
    }

    /// Replace the entries. A list replaced while the popup is open refits
    /// it now, not at the next opening.
    pub fn set_items(&mut self, items: Vec<MenuItem>) {
        self.items = items;
        if self.open && self.fits_long_labels {
            self.measure_items_fit_width();
        }
    }

    /// How far left of the button the popup starts, pixels: 0, or negative
    /// when a widened popup shifted off its window's or the screen's right
    /// edge.
    #[must_use]
    pub fn popup_left(&self) -> f32 {
        self.popup_left
    }

    /// The popup scrollbar's left edge inside the popup, pixels.
    #[must_use]
    pub fn popup_scrollbar_left(&self) -> f32 {
        BORDER + self.row_width()
    }

    /// The caption a button `width` pixels wide shows: the button label,
    /// cut short with an ellipsis when it does not fit and the menu fits
    /// long labels.
    pub fn button_caption(&mut self, width: f32) -> &str {
        self.fit_caption(width);
        self.shown_caption()
    }

    /// The width of a popup row: the column width, or for a menu that fits
    /// long labels the widest entry or the button's interior when wider.
    fn row_width(&self) -> f32 {
        if self.fits_long_labels {
            self.column_width.max(
                self.items_fit_width
                    .max(self.fit_button_width - 2.0 * BORDER - self.effective_scrollbar_width()),
            )
        } else {
            self.column_width
        }
    }

    fn measure_items_fit_width(&mut self) {
        let pad = if self.retail_button_art {
            self.text_indent
        } else {
            PLAIN_PADDING
        };
        let widest = self
            .items
            .iter()
            .map(|item| self.measure_text(&item.label))
            .fold(0.0f32, f32::max);
        self.items_fit_width = widest + 2.0 * pad;
    }

    /// Note the button's width and where the popup starts. A popup wider
    /// than the room right of the button shifts left to end at the right
    /// edge of its window, or of the screen when the window is too narrow
    /// for it, but never past the left edge of either.
    fn place_popup(&mut self, ui: &UiRoot, me: ElementId) {
        if !self.fits_long_labels || !ui.is_connected(me) {
            self.popup_left = 0.0;
            return;
        }
        let Some(width) = ui.element(me).map(|e| e.width) else {
            self.popup_left = 0.0;
            return;
        };
        self.fit_button_width = width;
        let ow = self.popup_outer_width();
        let (sx, _) = ui.screen_position(me);
        let (mut lo, mut hi) = (0.0f32, ui.effective_canvas_size().0);
        let mut cur = ui.parent(me);
        while let Some(c) = cur {
            if c == ui.root() {
                break;
            }
            if let Some(e) = ui.element(c)
                && (e.draggable || e.resizable)
            {
                let (wl, _) = ui.screen_position(c);
                let (wlo, whi) = (lo.max(wl), hi.min(wl + e.width));
                if ow <= whi - wlo {
                    (lo, hi) = (wlo, whi);
                }
                break;
            }
            cur = ui.parent(c);
        }
        self.popup_left = (lo - sx).max(0.0f32.min(hi - sx - ow));
    }

    /// The room a button `width` pixels wide leaves its caption.
    fn caption_room(&self, width: f32) -> f32 {
        if !self.retail_button_art {
            return width - PLAIN_PADDING - PLAIN_CHEVRON_RESERVE;
        }
        let right = if self.arrow_cap_closed_sprite != 0 {
            self.arrow_cap_width
        } else {
            FACE_CAP_R
        };
        let indent = if self.button_text_centered {
            0.0
        } else {
            self.button_text_indent
        };
        width - right - indent
    }

    fn caption_font(&self) -> Option<&Arc<UiFont>> {
        self.button_font.as_ref().or(self.font.as_ref())
    }

    fn measure_caption(font: Option<&Arc<UiFont>>, s: &str) -> f32 {
        font.map_or(s.chars().count() as f32 * 7.0, |f| f.measure_width(s))
    }

    /// Fit the button caption to a button `width` pixels wide, unless the
    /// last fit was for the same caption, font and room.
    fn fit_caption(&mut self, width: f32) {
        if !self.fits_long_labels {
            return;
        }
        let max_width = self.caption_room(width);
        let font = self.caption_font();
        if let Some(fit) = &self.caption_fit
            && fit.source == self.button_label
            && fit.max_width == max_width
            && match (&fit.font, font) {
                (Some(a), Some(b)) => Arc::ptr_eq(a, b),
                (None, None) => true,
                _ => false,
            }
        {
            return;
        }
        let shown = Self::shorten_caption(font, &self.button_label, max_width);
        self.caption_fit = Some(CaptionFit {
            source: self.button_label.clone(),
            font: font.cloned(),
            max_width,
            shown,
        });
    }

    /// The caption as drawn: the fitted one, or the label itself.
    fn shown_caption(&self) -> &str {
        match &self.caption_fit {
            Some(fit) if self.fits_long_labels => &fit.shown,
            _ => &self.button_label,
        }
    }

    /// `caption`, cut on a whole character with an ellipsis when it is
    /// wider than `max_width`. A font without the ellipsis glyph gets three
    /// dots.
    fn shorten_caption(font: Option<&Arc<UiFont>>, caption: &str, max_width: f32) -> String {
        if Self::measure_caption(font, caption) <= max_width {
            return caption.to_owned();
        }
        let dots = match font {
            Some(f) if f.glyph('\u{2026}').is_none() => "...",
            _ => "\u{2026}",
        };
        let cuts = caption.char_indices().rev().map(|(n, _)| n);
        for n in cuts.filter(|&n| n > 0) {
            let cut = format!("{}{dots}", caption[..n].trim_end());
            if Self::measure_caption(font, &cut) <= max_width {
                return cut;
            }
        }
        if Self::measure_caption(font, dots) <= max_width {
            dots.to_owned()
        } else {
            String::new()
        }
    }

    /// True while the popup is open.
    #[must_use]
    pub fn is_open(&self) -> bool {
        self.open
    }

    /// The face drawn now (pressed only while the button is held).
    #[must_use]
    pub fn current_face_sprite(&self) -> u32 {
        if self.face_pressed {
            self.pressed_sprite
        } else {
            self.normal_sprite
        }
    }

    /// The arrow cap drawn now.
    #[must_use]
    pub fn current_arrow_cap_sprite(&self) -> u32 {
        if self.open {
            self.arrow_cap_open_sprite
        } else {
            self.arrow_cap_closed_sprite
        }
    }

    /// The hovered popup entry (plain style).
    #[must_use]
    pub fn hovered_popup_index(&self) -> Option<usize> {
        self.hovered_popup_index
    }

    /// Open or close the popup (opening asks the owner to refresh the
    /// entries first).
    pub fn set_open(&mut self, ui: &mut UiRoot, me: ElementId, value: bool) {
        if self.open == value {
            return;
        }
        if value {
            if self.searchable {
                // Each opening starts with an empty query and takes the
                // keyboard for the search band.
                if let Some(e) = ui.element_mut(me) {
                    e.accepts_focus = true;
                    e.is_edit_control = true;
                }
                self.search.set_text("");
            }
            ui.send_message(me, ElementMessage::MenuOpening);
            if self.searchable {
                self.filter_items();
            }
            if self.fits_long_labels {
                self.measure_items_fit_width();
            }
        } else {
            self.end_search_selection(ui, me);
            self.dragging_popup_thumb = false;
        }
        self.open = value;
        self.hovered_popup_index = None;
        if value {
            self.place_popup(ui, me);
            ui.set_active_popup(me, me);
            if self.searchable {
                ui.set_keyboard_focus(Some(me));
            }
        } else {
            ui.clear_active_popup(me);
            if self.searchable && ui.keyboard_focus() == Some(me) {
                ui.set_keyboard_focus(None);
            }
        }
    }

    /// The search text.
    #[must_use]
    pub fn search_text(&self) -> String {
        self.search.text()
    }

    /// The entries the popup shows, by index into [`Self::items`]: every
    /// one, or with a search band those whose caption holds every word of
    /// the query, ignoring case.
    #[must_use]
    pub fn display_indices(&self) -> Vec<usize> {
        if !self.searchable {
            return (0..self.items.len()).collect();
        }
        let query = self.search.text().to_lowercase();
        let words: Vec<&str> = query.split_whitespace().collect();
        self.items
            .iter()
            .enumerate()
            .filter(|(_, item)| {
                let label = item.label.to_lowercase();
                words.iter().all(|w| label.contains(w))
            })
            .map(|(i, _)| i)
            .collect()
    }

    fn display_len(&self) -> usize {
        if self.searchable {
            self.display_indices().len()
        } else {
            self.items.len()
        }
    }

    /// The entry shown at popup row `i`.
    fn display_item(&self, i: usize) -> Option<&MenuItem> {
        if self.searchable {
            self.display_indices()
                .get(i)
                .and_then(|&index| self.items.get(index))
        } else {
            self.items.get(i)
        }
    }

    /// The query changed: no row is hovered and the list starts at its
    /// top (OpenAC `FilterItems`).
    fn filter_items(&mut self) {
        self.search_version = self.search.text_version();
        self.hovered_popup_index = None;
        self.popup_scroll.set_scroll_y(0);
    }

    fn follow_search(&mut self) {
        if self.search.text_version() != self.search_version {
            self.filter_items();
        }
    }

    fn end_search_selection(&mut self, ui: &mut UiRoot, me: ElementId) {
        if !std::mem::take(&mut self.selecting_search) {
            return;
        }
        let up = UiEvent::new(0, me, kind::MOUSE_UP);
        let width = self.interior_w();
        self.search.handle_detached(ui, me, &up, width);
    }

    /// A key in the open search band: Escape closes, Enter picks the row
    /// the arrows reached (else the first), the arrows step over disabled
    /// rows and scroll the row into view. False for any other key (the
    /// band's entry takes it).
    fn search_key(&mut self, ui: &mut UiRoot, me: ElementId, _height: f32, key: Key) -> bool {
        match key {
            Key::Escape => {
                self.set_open(ui, me, false);
                true
            }
            Key::Enter | Key::KeypadEnter => {
                let index = self.hovered_popup_index.unwrap_or(0);
                if let Some(item) = self.display_item(index)
                    && item.enabled
                {
                    ui.send_message(me, ElementMessage::MenuSelected(item.payload));
                    self.set_open(ui, me, false);
                }
                true
            }
            Key::Down | Key::Up => {
                let shown = self.display_indices();
                let len = shown.len() as isize;
                let step = if key == Key::Down { 1 } else { -1 };
                let mut next = self.hovered_popup_index.map_or(-1, |i| i as isize);
                loop {
                    next += step;
                    if next < 0 || next >= len || self.items[shown[next as usize]].enabled {
                        break;
                    }
                }
                if next >= 0 && next < len {
                    let next = next as usize;
                    self.hovered_popup_index = Some(next);
                    self.configure_popup_scroll();
                    let top = self.visible_top_row();
                    let rows = self.effective_visible_rows();
                    if next < top {
                        self.popup_scroll
                            .set_scroll_y((next as f32 * self.row_height) as i32);
                    } else if next >= top + rows {
                        self.popup_scroll
                            .set_scroll_y(((next + 1 - rows) as f32 * self.row_height) as i32);
                    }
                }
                true
            }
            _ => false,
        }
    }

    /// The search entry takes the menu's font and colours.
    fn style_search(&mut self) {
        self.search.font.clone_from(&self.font);
        self.search.text_color = if self.retail_button_art {
            self.text_color_available
        } else {
            self.plain_text_color
        };
        self.search.background_color = self.plain_background_color;
        self.search.selection_color = self.plain_selected_color;
        self.search.theme_palette = self.theme_palette;
    }

    /// Hand the search entry an event (styled as the menu draws it).
    fn search_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        self.style_search();
        let width = self.interior_w();
        let handled = self.search.handle_detached(ui, me, e, width);
        self.follow_search();
        handled
    }

    /// The search band's height (none without one).
    fn search_height(&self) -> f32 {
        if self.searchable {
            24.0f32.max(self.row_height + 6.0)
        } else {
            0.0
        }
    }

    /// The clipboard text the search band asked for.
    pub fn paste(&mut self, ui: &mut UiRoot, me: ElementId, clip: &str) {
        self.search.paste_text(clip);
        let _ = (ui, me);
        self.follow_search();
    }

    fn column_count(&self) -> usize {
        if self.scrollable {
            1
        } else {
            let minimum = usize::from(self.searchable);
            self.display_len()
                .div_ceil(self.rows_per_column.max(1))
                .max(minimum)
        }
    }

    fn effective_visible_rows(&self) -> usize {
        if self.scrollable && self.popup_size_to_content {
            self.display_len().max(1)
        } else {
            self.rows_per_column
        }
    }

    fn popup_content_overflows(&self) -> bool {
        self.display_len() > self.effective_visible_rows()
    }

    /// True when the popup scrollbar shows.
    #[must_use]
    pub fn popup_scrollbar_visible(&self) -> bool {
        !self.popup_scrollbar_hide_when_disabled || self.popup_content_overflows()
    }

    fn effective_scrollbar_width(&self) -> f32 {
        if self.popup_scrollbar_visible() {
            self.scrollbar_width
        } else {
            0.0
        }
    }

    fn interior_w(&self) -> f32 {
        if self.scrollable {
            self.row_width() + self.effective_scrollbar_width()
        } else {
            self.column_count() as f32 * self.row_width()
        }
    }

    fn interior_h(&self) -> f32 {
        self.effective_visible_rows() as f32 * self.row_height
    }

    /// The popup's outer width, pixels.
    #[must_use]
    pub fn popup_outer_width(&self) -> f32 {
        self.interior_w() + 2.0 * BORDER
    }

    /// The popup's outer height, pixels.
    #[must_use]
    pub fn popup_outer_height(&self) -> f32 {
        self.interior_h() + self.search_height() + 2.0 * BORDER
    }

    /// Size the rows so the popup comes out exactly `width` wide.
    pub fn size_popup_to_width(&mut self, width: f32) {
        self.column_width = (width - 2.0 * BORDER - self.effective_scrollbar_width()).max(1.0);
    }

    fn popup_top(&self, height: f32) -> f32 {
        if self.open_upward {
            -self.popup_outer_height()
        } else {
            height
        }
    }

    fn configure_popup_scroll(&mut self) {
        let lh = (self.row_height.round_ties_even() as i32).max(1);
        self.popup_scroll.line_height = lh;
        self.popup_scroll.set_extents(
            self.display_len() as i32 * lh,
            self.effective_visible_rows() as i32 * lh,
            false,
        );
    }

    fn visible_top_row(&self) -> usize {
        let lh = (self.row_height.round_ties_even() as i32).max(1);
        let max_start = self
            .display_len()
            .saturating_sub(self.effective_visible_rows());
        let row = (self.popup_scroll.scroll_y() as f32 / lh as f32).round_ties_even() as i64;
        (row.max(0) as usize).min(max_start)
    }

    fn line_h(&self) -> f32 {
        self.font.as_ref().map_or(14.0, |f| f.line_height)
    }

    fn measure_text(&self, s: &str) -> f32 {
        self.font
            .as_ref()
            .map_or(s.chars().count() as f32 * 7.0, |f| f.measure_width(s))
    }

    fn item_text_x(&self, label: &str) -> f32 {
        if self.item_text_centered {
            ((self.row_width() - self.measure_text(label)) * 0.5).max(0.0)
        } else {
            self.text_indent
        }
    }

    /// The button width that fits its caption.
    #[must_use]
    pub fn natural_button_width(&self) -> f32 {
        let font = self.button_font.as_ref().or(self.font.as_ref());
        let w = font.map_or(self.button_label.chars().count() as f32 * 7.0, |f| {
            f.measure_width(&self.button_label)
        });
        self.button_text_indent + w + 4.0 + FACE_CAP_R
    }

    fn hovered_grid_index(&self, ix: f32, iy: f32) -> Option<usize> {
        if ix < 0.0 || ix >= self.interior_w() || iy < 0.0 || iy >= self.interior_h() {
            return None;
        }
        let col = (ix / self.row_width()) as usize;
        let row = (iy / self.row_height) as usize;
        let idx = col * self.rows_per_column + row;
        (row < self.rows_per_column && idx < self.display_len()).then_some(idx)
    }

    fn hovered_scrollable_index(&self, ix: f32, iy: f32) -> Option<usize> {
        if ix < 0.0 || ix >= self.row_width() || iy < 0.0 || iy >= self.interior_h() {
            return None;
        }
        let row = (iy / self.row_height) as usize;
        let idx = self.visible_top_row() + row;
        (row < self.effective_visible_rows() && idx < self.display_len()).then_some(idx)
    }

    /// Pick popup row `idx` (an enabled entry only).
    fn pick(&mut self, ui: &mut UiRoot, me: ElementId, idx: Option<usize>) {
        if let Some(i) = idx
            && let Some(item) = self.display_item(i)
            && item.enabled
        {
            ui.send_message(me, ElementMessage::MenuSelected(item.payload));
        }
    }

    /// The rows' top inside the popup, below the search band.
    fn rows_top(&self, height: f32) -> f32 {
        self.popup_top(height) + BORDER + self.search_height()
    }

    fn scroll_extents(&self) -> (f32, f32) {
        let h = self.interior_h();
        let dec = self.scroll_button_extent.clamp(0.0, h.max(0.0));
        let inc = self.scroll_button_extent.clamp(0.0, (h - dec).max(0.0));
        (dec, inc)
    }

    fn handle_scrollable_popup_mouse_down(
        &mut self,
        ui: &mut UiRoot,
        me: ElementId,
        ix: f32,
        iy: f32,
    ) -> bool {
        let ih = self.interior_h();
        if ix >= 0.0 && ix < self.row_width() && iy >= 0.0 && iy < ih {
            let idx = self.hovered_scrollable_index(ix, iy);
            self.pick(ui, me, idx);
            self.set_open(ui, me, false);
            return true;
        }
        let sx = self.row_width();
        if self.popup_scrollbar_visible()
            && ix >= sx
            && ix < sx + self.scrollbar_width
            && iy >= 0.0
            && iy < ih
        {
            self.configure_popup_scroll();
            let (dec, inc) = self.scroll_extents();
            if iy < dec {
                self.popup_scroll.scroll_by_lines(-1);
                return true;
            }
            if iy >= ih - inc {
                self.popup_scroll.scroll_by_lines(1);
                return true;
            }
            let (ty, th) = thumb_rect(&self.popup_scroll, dec, (ih - dec - inc).max(0.0));
            if iy >= ty && iy <= ty + th {
                self.dragging_popup_thumb = true;
                self.popup_thumb_drag_offset = iy - ty;
            } else {
                self.popup_scroll
                    .scroll_by_page(if iy < ty { -1 } else { 1 });
            }
            return true;
        }
        self.set_open(ui, me, false);
        true
    }

    fn drag_popup_thumb(&mut self, height: f32, ly: f32) {
        let iy = ly - self.rows_top(height);
        self.configure_popup_scroll();
        let (dec, inc) = self.scroll_extents();
        let track_len = (self.interior_h() - dec - inc).max(0.0);
        let (_, th) = thumb_rect(&self.popup_scroll, dec, track_len);
        let travel = (track_len - th).max(1.0);
        self.popup_scroll
            .set_position_ratio((iy - self.popup_thumb_drag_offset - dec) / travel);
    }

    // ── Drawing ─────────────────────────────────────────────────────────

    fn tile(rc: &mut RenderContext<'_>, id: u32, x: f32, y: f32, w: f32, h: f32) {
        if id != 0 && w > 0.0 && h > 0.0 {
            rc.surface(id, x, y, w, h, Color::WHITE);
        }
    }

    fn label(&self, rc: &mut RenderContext<'_>, s: &str, x: f32, y: f32, color: Color) {
        if let Some(font) = &self.font {
            rc.string_dat(font, s, x, y, color, self.outline, self.outline_color);
        }
    }

    fn draw_bevel(rc: &mut RenderContext<'_>, x: f32, y: f32, w: f32, h: f32) {
        let r = chrome::compute_frame_rects(w, h, chrome::BORDER);
        for (id, d) in [
            (chrome::CENTER_FILL, r.center),
            (chrome::TOP_EDGE, r.top),
            (chrome::BOTTOM_EDGE, r.bottom),
            (chrome::LEFT_EDGE, r.left),
            (chrome::RIGHT_EDGE, r.right),
            (chrome::CORNER_TL, r.tl),
            (chrome::CORNER_TR, r.tr),
            (chrome::CORNER_BL, r.bl),
            (chrome::CORNER_BR, r.br),
        ] {
            Self::tile(rc, id, x + d.x, y + d.y, d.w, d.h);
        }
    }

    fn draw_button_face(&self, rc: &mut RenderContext<'_>, w: f32, h: f32) {
        let id = self.current_face_sprite();
        let Some((tw, _)) = rc.sprite_size(id) else {
            return;
        };
        let t = TextureRef::Surface(id);
        let (u_l, u_r) = (FACE_CAP_L / tw, (tw - FACE_CAP_R) / tw);
        let mid = w - FACE_CAP_L - FACE_CAP_R;
        rc.sprite(
            t,
            0.0,
            0.0,
            FACE_CAP_L,
            h,
            (0.0, 0.0, u_l, 1.0),
            Color::WHITE,
        );
        if mid > 0.0 {
            rc.sprite(
                t,
                FACE_CAP_L,
                0.0,
                mid,
                h,
                (u_l, 0.0, u_r, 1.0),
                Color::WHITE,
            );
        }
        rc.sprite(
            t,
            w - FACE_CAP_R,
            0.0,
            FACE_CAP_R,
            h,
            (u_r, 0.0, 1.0, 1.0),
            Color::WHITE,
        );
    }

    fn draw_closed(&self, rc: &mut RenderContext<'_>, w: f32, h: f32, live: bool) {
        if !self.retail_button_art {
            let open = self.open || self.face_pressed;
            let border = if open {
                self.plain_open_border_color
            } else {
                self.plain_border_color
            };
            let mut caption = self.plain_text_color;
            if let Some(p) = &self.theme_palette {
                let look = if open {
                    ButtonLook::Normal
                } else {
                    self.pointer.look(live)
                };
                let face = (0.0, 0.0, w, h);
                style::button(rc, p, face, self.plain_background_color, border, look);
                if !live {
                    caption = style::faded(caption);
                }
            } else {
                rc.fill(0.0, 0.0, w, h, self.plain_background_color);
                rc.rect_outline(0.0, 0.0, w, h, border, 1.0);
            }
            let font = self.caption_font();
            if let Some(f) = font {
                let y = (h - f.line_height) * 0.5;
                rc.string_dat(
                    f,
                    self.shown_caption(),
                    PLAIN_PADDING,
                    y,
                    caption,
                    self.outline,
                    self.outline_color,
                );
            }
            if self.theme_palette.is_some() {
                let triangle = self.plain_triangle_color;
                style::chevron(rc, w - 14.0, (h - 4.0) * 0.5, 8.0, 4.0, triangle);
                return;
            }
            let (tw, right) = (7.0, 6.0);
            let (x, y) = (w - right - tw, (h - 4.0) * 0.5);
            for i in 0..4 {
                let i = i as f32;
                rc.fill(x + i, y + i, tw - 2.0 * i, 1.0, self.plain_triangle_color);
            }
            return;
        }
        self.draw_button_face(rc, w, h);
        let font = self.caption_font();
        let caption_w = Self::measure_caption(font, self.shown_caption());
        let caption_h = font.map_or(14.0, |f| f.line_height);
        let cap_x = if self.button_text_centered {
            let arrow = if self.arrow_cap_closed_sprite != 0 {
                self.arrow_cap_width
            } else {
                0.0
            };
            ((w - arrow - caption_w) * 0.5).max(0.0)
        } else {
            self.button_text_indent
        };
        if let Some(f) = font {
            rc.string_dat(
                f,
                self.shown_caption(),
                cap_x,
                (h - caption_h) * 0.5,
                self.text_color,
                self.outline,
                self.outline_color,
            );
        }
        let cap = self.current_arrow_cap_sprite();
        if cap != 0 && rc.sprite_size(cap).is_some() {
            rc.sprite(
                TextureRef::Surface(cap),
                w - self.arrow_cap_width,
                0.0,
                self.arrow_cap_width,
                self.arrow_cap_height,
                (0.0, 0.0, 1.0, 1.0),
                Color::WHITE,
            );
        }
    }

    fn draw_popup(&mut self, rc: &mut RenderContext<'_>, h: f32) {
        if self.scrollable {
            self.configure_popup_scroll();
        }
        let top = self.popup_top(h);
        let (in_x, in_y) = (BORDER, self.rows_top(h));
        let (ow, oh) = (self.popup_outer_width(), self.popup_outer_height());
        let shown = self.display_indices();
        let plain = !self.retail_button_art;
        if plain && self.theme_palette.is_some() {
            style::popup_shadow(rc, 0.0, top, ow, oh);
            style::surface(
                rc,
                0.0,
                top,
                ow,
                oh,
                style::CONTAINER_RADIUS,
                self.plain_background_color,
                self.plain_border_color,
            );
        } else if plain {
            rc.fill(0.0, top, ow, oh, self.plain_background_color);
            rc.rect_outline(0.0, top, ow, oh, self.plain_border_color, 1.0);
        } else {
            Self::draw_bevel(rc, 0.0, top, ow, oh);
            let bg_w = if self.scrollable {
                self.row_width()
            } else {
                self.interior_w()
            };
            Self::tile(
                rc,
                self.popup_bg_sprite,
                in_x,
                in_y,
                bg_w,
                self.interior_h(),
            );
        }
        // (index, x, y) of every row drawn.
        let rows: Vec<(usize, f32, f32)> = if self.scrollable {
            let start = self.visible_top_row();
            let count = self
                .effective_visible_rows()
                .min(shown.len().saturating_sub(start));
            (0..count)
                .map(|i| (start + i, in_x, in_y + i as f32 * self.row_height))
                .collect()
        } else {
            (0..shown.len())
                .map(|i| {
                    let (col, row) = (
                        i / self.rows_per_column.max(1),
                        i % self.rows_per_column.max(1),
                    );
                    (
                        i,
                        in_x + col as f32 * self.row_width(),
                        in_y + row as f32 * self.row_height,
                    )
                })
                .collect()
        };
        for &(i, x, y) in &rows {
            // With a search band the row the arrows reached is lit too.
            let selected = self.selected == Some(self.items[shown[i]].payload)
                || (self.searchable && self.hovered_popup_index == Some(i));
            if plain {
                let highlight = if selected {
                    Some(self.plain_selected_color)
                } else if self.hovered_popup_index == Some(i) {
                    Some(self.plain_hover_color)
                } else {
                    None
                };
                if let Some(color) = highlight {
                    let (rw, rh) = (self.row_width(), self.row_height);
                    if self.theme_palette.is_some() {
                        style::row(rc, x, y, rw, rh, color);
                    } else {
                        rc.fill(x, y, rw, rh, color);
                    }
                }
            } else {
                let id = if selected {
                    self.item_highlight_sprite
                } else {
                    self.item_normal_sprite
                };
                Self::tile(rc, id, x, y, self.row_width(), self.row_height);
            }
        }
        let text_y = (self.row_height - self.line_h()) * 0.5;
        for &(i, x, y) in &rows {
            let item = &self.items[shown[i]];
            let (dx, color) = if plain {
                (
                    PLAIN_PADDING,
                    if item.enabled {
                        self.plain_text_color
                    } else {
                        self.text_color_ghosted
                    },
                )
            } else {
                (
                    self.item_text_x(&item.label),
                    if item.enabled {
                        self.text_color_available
                    } else {
                        self.text_color_ghosted
                    },
                )
            };
            self.label(rc, &item.label, x + dx, y + text_y, color);
        }
        if self.scrollable {
            self.draw_popup_scrollbar(rc, in_x + self.row_width(), in_y);
        }
        self.draw_search(rc, top, shown.is_empty());
    }

    /// The search band: the entry, its frame, the hint while empty, and
    /// "No matches" under it when nothing matches (OpenAC `DrawSearch`).
    fn draw_search(&mut self, rc: &mut RenderContext<'_>, top: f32, nothing: bool) {
        if !self.searchable {
            return;
        }
        let (w, h) = (self.interior_w(), self.search_height() - 2.0);
        self.style_search();
        rc.push_transform(BORDER, top + BORDER);
        self.search.draw_detached(w, h, rc);
        rc.pop_transform();
        if self.theme_palette.is_none() {
            rc.rect_outline(
                BORDER,
                top + BORDER,
                w,
                h,
                self.plain_open_border_color,
                1.0,
            );
        }
        if self.search.text().is_empty() {
            let y = top + BORDER + (self.search_height() - self.line_h()) * 0.5;
            self.label(rc, "Search...", BORDER + 7.0, y, self.text_color_ghosted);
        }
        if nothing {
            let y = top + BORDER + self.search_height() + 2.0;
            self.label(
                rc,
                "No matches",
                BORDER + PLAIN_PADDING,
                y,
                self.text_color_ghosted,
            );
        }
    }

    fn draw_popup_scrollbar(&self, rc: &mut RenderContext<'_>, x: f32, y: f32) {
        if !self.popup_scrollbar_visible() {
            return;
        }
        let ih = self.interior_h();
        let (dec, inc) = self.scroll_extents();
        if let Some(p) = self
            .theme_palette
            .as_ref()
            .filter(|_| !self.retail_button_art)
        {
            if self.popup_scroll.has_overflow() {
                let (ty, th) = thumb_rect(&self.popup_scroll, dec, (ih - dec - inc).max(0.0));
                let active = self.dragging_popup_thumb;
                style::scroll_thumb(rc, p, x, y + ty, self.scrollbar_width, th, active);
            }
            return;
        }
        if !self.retail_button_art && (self.plain_popup_scrollbar || self.scroll_track_sprite == 0)
        {
            rc.fill(x, y, self.scrollbar_width, ih, self.plain_background_color);
            rc.rect_outline(x, y, self.scrollbar_width, ih, self.plain_border_color, 1.0);
            if !self.popup_scroll.has_overflow() {
                return;
            }
            let (ty, th) = thumb_rect(&self.popup_scroll, dec, (ih - dec - inc).max(0.0));
            rc.fill(
                x + 1.0,
                y + ty,
                (self.scrollbar_width - 2.0).max(0.0),
                th,
                self.plain_border_color,
            );
            return;
        }
        let sw = self.scrollbar_width;
        Self::tile(rc, self.scroll_track_sprite, x, y, sw, ih);
        Self::tile(rc, self.scroll_up_sprite, x, y, sw, dec);
        Self::tile(rc, self.scroll_down_sprite, x, y + ih - inc, sw, inc);
        if !self.popup_scroll.has_overflow() {
            return;
        }
        let (ty, th) = thumb_rect(&self.popup_scroll, dec, (ih - dec - inc).max(0.0));
        if self.scroll_thumb_top_sprite != 0
            && self.scroll_thumb_bottom_sprite != 0
            && th >= 2.0 * SCROLL_CAP_H
        {
            Self::tile(
                rc,
                self.scroll_thumb_top_sprite,
                x,
                y + ty,
                sw,
                SCROLL_CAP_H,
            );
            Self::tile(
                rc,
                self.scroll_thumb_sprite,
                x,
                y + ty + SCROLL_CAP_H,
                sw,
                th - 2.0 * SCROLL_CAP_H,
            );
            Self::tile(
                rc,
                self.scroll_thumb_bottom_sprite,
                x,
                y + ty + th - SCROLL_CAP_H,
                sw,
                SCROLL_CAP_H,
            );
        } else {
            Self::tile(rc, self.scroll_thumb_sprite, x, y + ty, sw, th);
        }
    }
}

impl Widget for Menu {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        let Some((w, h)) = ui.element(me).map(|el| (el.width, el.height)) else {
            return false;
        };
        let own = e.target == Some(me);
        if own {
            self.pointer.observe(e);
        }
        if self.open {
            self.place_popup(ui, me);
        }
        if self.searchable && matches!(e.kind, kind::FOCUS_GAINED | kind::FOCUS_LOST) {
            if e.kind == kind::FOCUS_LOST {
                self.end_search_selection(ui, me);
            }
            return self.search_event(ui, me, e);
        }
        if e.kind == kind::CAPTURE_CHANGED {
            self.end_search_selection(ui, me);
            self.dragging_popup_thumb = false;
        }
        if self.selecting_search && matches!(e.kind, kind::MOUSE_MOVE | kind::MOUSE_UP) {
            // A drag that began in the search band selects its text.
            let mut inner = e.clone();
            inner.data1 = (e.data1 as f32 - self.popup_left - BORDER) as i32;
            inner.data2 = (e.data2 as f32 - self.popup_top(h) - BORDER) as i32;
            self.search_event(ui, me, &inner);
            if e.kind == kind::MOUSE_UP {
                self.selecting_search = false;
            }
            return true;
        }
        if self.searchable
            && self.open
            && matches!(e.kind, kind::KEY_DOWN | kind::KEY_UP | kind::CHAR)
        {
            if e.kind == kind::KEY_DOWN && self.search_key(ui, me, h, e.key) {
                return true;
            }
            self.search_event(ui, me, e);
            return true;
        }
        if self.scrollable && self.open {
            if e.kind == kind::MOUSE_MOVE && self.dragging_popup_thumb && own {
                self.drag_popup_thumb(h, e.data2 as f32);
                return true;
            }
            if e.kind == kind::MOUSE_UP && self.dragging_popup_thumb {
                self.dragging_popup_thumb = false;
                return true;
            }
            if e.kind == kind::SCROLL {
                self.configure_popup_scroll();
                self.popup_scroll.scroll_by_lines(-e.data0);
                return true;
            }
        }
        if !self.retail_button_art && self.open && e.kind == kind::MOUSE_MOVE && own {
            let (ix, iy) = (
                e.data1 as f32 - self.popup_left - BORDER,
                e.data2 as f32 - self.rows_top(h),
            );
            self.hovered_popup_index = if self.scrollable {
                self.hovered_scrollable_index(ix, iy)
            } else {
                self.hovered_grid_index(ix, iy)
            };
            return true;
        }
        if matches!(
            e.kind,
            kind::MOUSE_UP | kind::HOVER_LEAVE | kind::CAPTURE_CHANGED
        ) {
            self.face_pressed = false;
            if e.kind == kind::HOVER_LEAVE {
                self.hovered_popup_index = None;
            }
            return false;
        }
        if e.kind != kind::MOUSE_DOWN || !own {
            return false;
        }
        let (lx, ly) = (e.data1 as f32, e.data2 as f32);
        let in_popup = if self.open_upward { ly < 0.0 } else { ly >= h };
        if self.open && in_popup {
            let (ix, iy) = (lx - self.popup_left - BORDER, ly - self.rows_top(h));
            let band = self.search_height();
            if self.searchable && iy < 0.0 && iy >= -band {
                // A press in the search band puts the caret there.
                ui.set_keyboard_focus(Some(me));
                self.selecting_search = true;
                self.hovered_popup_index = None;
                let mut inner = e.clone();
                inner.data1 = ix as i32;
                inner.data2 = (iy + band) as i32;
                self.search_event(ui, me, &inner);
                return true;
            }
            if self.scrollable {
                return self.handle_scrollable_popup_mouse_down(ui, me, ix, iy);
            }
            let idx = self.hovered_grid_index(ix, iy);
            self.pick(ui, me, idx);
            self.set_open(ui, me, false);
            return true;
        }
        let _ = w;
        self.face_pressed = true;
        // A menu with nothing to offer stays shut, unless it can search.
        if !self.open && self.items.is_empty() && !self.searchable {
            return true;
        }
        let next = !self.open;
        self.set_open(ui, me, next);
        true
    }

    fn hit_test(&self, element: &Element, x: f32, y: f32) -> bool {
        // The button itself, plus the popup on whichever side it opens.
        // Across the button row only the button counts, so a popup wider
        // than the button does not take the space beside it.
        if !self.open || (y >= 0.0 && y < element.height) {
            return element.contains_local(x, y);
        }
        let (left, top) = (self.popup_left, self.popup_top(element.height));
        x >= left
            && x < left + self.popup_outer_width()
            && y >= top
            && y < top + self.popup_outer_height()
    }

    fn receives_hover_mouse_move(&self) -> bool {
        self.open && !self.retail_button_art
    }

    fn clips_children(&self) -> bool {
        false
    }

    fn expands_clip_for_popup(&self) -> bool {
        true
    }

    fn consumes_dat_children(&self) -> bool {
        true
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn tooltip_text(&self, element: &Element) -> Option<String> {
        self.tooltip_text
            .as_ref()
            .filter(|t| !t.trim().is_empty())
            .cloned()
            .or_else(|| {
                element
                    .runtime_tooltip_text
                    .as_ref()
                    .filter(|t| !t.trim().is_empty())
                    .cloned()
            })
    }

    fn on_popup_dismissed(&mut self, ui: &mut UiRoot, me: ElementId, _popup: ElementId) {
        // The root already forgot the popup; only the menu's state closes.
        if self.open {
            self.open = false;
            self.hovered_popup_index = None;
            ui.clear_active_popup(me);
        }
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        let live = ui.element(me).is_some_and(|e| e.enabled);
        self.draw_closed(rc, w, h, live);
    }

    fn draw_overlay(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        if !self.open {
            return;
        }
        let Some(h) = ui.element(me).map(|e| e.height) else {
            return;
        };
        self.place_popup(ui, me);
        rc.push_alpha_absolute(1.0);
        rc.push_transform(self.popup_left, 0.0);
        self.draw_popup(rc, h);
        rc.pop_transform();
        rc.pop_alpha();
    }
}
