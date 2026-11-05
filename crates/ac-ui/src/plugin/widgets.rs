//! The markup widgets OpenAC's panel markup builds that the retail widgets
//! do not already cover: text, push buttons, tab headers, check toggles,
//! icons, the row list (single column or columns) and the log, and the
//! panel frame (retail chrome or the plugin theme). Their behaviour and
//! default colours are OpenAC's (`UiLabel`, `UiSimpleButton`,
//! `UiMarkupTabButton`, `UiMarkupToggle`, `UiMarkupIcon`, `UiMarkupList`,
//! `UiMarkupLog`, `UiCheckLamp`, `UiPluginMarkupPanel`).

use std::cell::RefCell;
use std::collections::BTreeSet;
use std::rc::Rc;
use std::sync::Arc;

use ac_plugin_ui::list::{self, ColumnPress};
use ac_plugin_ui::markup::{Column, ColumnHeader, ColumnKind};

use super::style::{self, PointerState};
use crate::draw::{Color, DEFAULT_OUTLINE_COLOR, RenderContext, TextureRef};
use crate::event::{Key, UiEvent, kind};
use crate::font::UiFont;
use crate::root::{ElementMessage, UiRoot};
use crate::shapes::CornerRadii;
use crate::text::Scrollable;
use crate::tree::{Element, ElementId, Widget};
use crate::widgets::NineSliceFrame;
use crate::widgets::scrollbar::thumb_rect;

/// A resolved image: its texture and its size in pixels.
pub type Icon = (TextureRef, f32, f32);

/// The file icons a plugin window drew this frame, by path as its markup
/// gives them: what the session holds while they are drawn. Elements that
/// are hidden (a closed tab's page, a hidden group, a list row scrolled
/// away) are not drawn, so they say nothing. Clones share the set.
#[derive(Debug, Clone, Default)]
pub struct DrawnFiles(Rc<RefCell<BTreeSet<String>>>);

impl DrawnFiles {
    /// `path` was drawn.
    pub fn record(&self, path: &str) {
        if !self.0.borrow().contains(path) {
            self.0.borrow_mut().insert(path.to_owned());
        }
    }

    /// The paths drawn since the last call, sorted.
    #[must_use]
    pub fn take(&self) -> Vec<String> {
        std::mem::take(&mut *self.0.borrow_mut())
            .into_iter()
            .collect()
    }
}

/// A file icon an element draws: its path, and where it says it drew it
/// (whether or not the image is ready yet).
#[derive(Debug, Clone)]
pub struct FileTag {
    /// The window's drawn set.
    pub drawn: DrawnFiles,
    /// The path.
    pub path: String,
}

impl FileTag {
    fn record(&self) {
        self.drawn.record(&self.path);
    }
}

/// The plugin theme's colours (OpenAC `PluginUiPalette`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Palette {
    /// The window's fill.
    pub background: Color,
    /// Fields, lists and buttons.
    pub field: Color,
    /// Borders.
    pub border: Color,
    /// Text.
    pub text: Color,
    /// Quiet text and marks.
    pub muted: Color,
    /// Highlights.
    pub accent: Color,
    /// Selection.
    pub selected: Color,
}

const fn rgb(packed: u32) -> Color {
    Color::rgba(
        ((packed >> 16) & 0xFF) as f32 / 255.0,
        ((packed >> 8) & 0xFF) as f32 / 255.0,
        (packed & 0xFF) as f32 / 255.0,
        1.0,
    )
}

impl Palette {
    /// The plugin dock's colours under Classic, its old black and gold. It is
    /// not a theme: only the dock uses it, and Classic windows still draw
    /// unthemed.
    pub const CLASSIC_DOCK: Self = Self {
        background: Color::rgba(0.0, 0.0, 0.0, 0.88),
        field: rgb(0x06_0605),
        border: rgb(0x9E_7A29),
        text: rgb(0xF0_EBDD),
        muted: rgb(0xA8_925C),
        accent: rgb(0xDB_B852),
        selected: rgb(0x17_300E),
    };
    /// "Charcoal + moss".
    pub const MOSS: Self = Self {
        background: rgb(0x17_1D1B),
        field: rgb(0x10_1613),
        border: rgb(0x35_443B),
        text: rgb(0xE0_E8E2),
        muted: rgb(0x9A_A99E),
        accent: rgb(0x97_BE81),
        selected: rgb(0x34_4B37),
    };
    /// "Warm graphite + brass".
    pub const BRASS: Self = Self {
        background: rgb(0x22_1F1B),
        field: rgb(0x18_1612),
        border: rgb(0x4C_4335),
        text: rgb(0xE9_E2D5),
        muted: rgb(0xB2_A58E),
        accent: rgb(0xC9_A665),
        selected: rgb(0x51_442D),
    };
    /// "Lavender Dusk".
    pub const LAVENDER_DUSK: Self = Self {
        background: rgb(0x17_141F),
        field: rgb(0x10_0E17),
        border: rgb(0x35_2C4A),
        text: rgb(0xEE_EAF6),
        muted: rgb(0xA6_9EBA),
        accent: rgb(0xBB_A0FF),
        selected: rgb(0x51_2F4A),
    };
    /// "Ember".
    pub const EMBER: Self = Self {
        background: rgb(0x15_1A21),
        field: rgb(0x0E_1218),
        border: rgb(0x2C_3644),
        text: rgb(0xE8_ECF1),
        muted: rgb(0x9A_A6B5),
        accent: rgb(0xFF_9447),
        selected: rgb(0x1E_3B44),
    };
    /// "Bloodmoon".
    pub const BLOODMOON: Self = Self {
        background: rgb(0x1C_1617),
        field: rgb(0x14_0F10),
        border: rgb(0x43_302F),
        text: rgb(0xF2_E9E7),
        muted: rgb(0xB8_A3A0),
        accent: rgb(0xFF_6B6B),
        selected: rgb(0x4D_2327),
    };
    /// "Glacier".
    pub const GLACIER: Self = Self {
        background: rgb(0x11_161C),
        field: rgb(0x0A_0E13),
        border: rgb(0x26_323F),
        text: rgb(0xE4_EEF4),
        muted: rgb(0x93_A4B3),
        accent: rgb(0x5E_D6E8),
        selected: rgb(0x1F_3554),
    };
    /// "Parchment", the one light theme.
    pub const PARCHMENT: Self = Self {
        background: rgb(0xED_E7DC),
        field: rgb(0xFA_F7F1),
        border: rgb(0xC9_BFAE),
        text: rgb(0x2A_2520),
        muted: rgb(0x65_5C50),
        accent: rgb(0xB4_471F),
        selected: rgb(0xDD_CFB6),
    };

    /// Whether the face is pale: a light theme, meant for dark text and art.
    #[must_use]
    pub fn is_light(&self) -> bool {
        let b = self.background;
        0.2126 * b.r + 0.7152 * b.g + 0.0722 * b.b > 0.5
    }

    /// A theme colour by its markup name.
    #[must_use]
    pub fn token(&self, name: &str) -> Option<Color> {
        Some(match name {
            "text" => self.text,
            "muted" => self.muted,
            "field" => self.field,
            "border" => self.border,
            "accent" => self.accent,
            "background" => self.background,
            _ => return None,
        })
    }
}

/// The check box OpenAC draws (`UiCheckLamp`, 11 pixels square), or the
/// theme's rounded one.
pub fn draw_check(
    rc: &mut RenderContext<'_>,
    x: f32,
    y: f32,
    checked: bool,
    palette: Option<&Palette>,
) {
    if let Some(p) = palette {
        style::check(rc, p, x, y, checked);
        return;
    }
    let (outer, inner) = if checked {
        (
            Color::rgba(0.36, 0.58, 0.12, 1.0),
            Color::rgba(0.52, 1.0, 0.08, 1.0),
        )
    } else {
        (
            Color::rgba(0.26, 0.22, 0.13, 1.0),
            Color::rgba(0.38, 0.34, 0.23, 1.0),
        )
    };
    rc.fill(x + 3.0, y, 5.0, 1.0, outer);
    rc.fill(x + 1.0, y + 1.0, 9.0, 2.0, outer);
    rc.fill(x, y + 3.0, 11.0, 5.0, outer);
    rc.fill(x + 1.0, y + 8.0, 9.0, 2.0, outer);
    rc.fill(x + 3.0, y + 10.0, 5.0, 1.0, outer);
    rc.fill(x + 3.0, y + 3.0, 5.0, 5.0, inner);
}

/// The check box's side, pixels.
pub const CHECK_SIZE: f32 = 11.0;

/// The alpha a disabled toggle's caption draws at; a disabled list's
/// buttons fade by it too.
pub const DISABLED_ALPHA: f32 = 0.42;

fn text(
    rc: &mut RenderContext<'_>,
    font: Option<&Arc<UiFont>>,
    s: &str,
    x: f32,
    y: f32,
    color: Color,
    outline: bool,
) {
    if let Some(font) = font {
        rc.string_dat(font, s, x, y, color, outline, DEFAULT_OUTLINE_COLOR);
    }
}

fn line_height(font: Option<&Arc<UiFont>>) -> f32 {
    font.map_or(14.0, |f| f.line_height)
}

fn measure(font: Option<&Arc<UiFont>>, s: &str) -> f32 {
    font.map_or(s.chars().count() as f32 * 7.0, |f| f.measure_width(s))
}

/// The alpha an unselected tab draws its icon at, so it recedes like its
/// caption.
pub const INACTIVE_TAB_ICON_ALPHA: f32 = 0.55;

fn draw_icon(rc: &mut RenderContext<'_>, icon: Icon, x: f32, y: f32, w: f32, h: f32) {
    draw_icon_tinted(rc, icon, (x, y, w, h), Color::WHITE);
}

/// An icon fitted into a rectangle, drawn in `tint` (white: as authored).
fn draw_icon_tinted(
    rc: &mut RenderContext<'_>,
    icon: Icon,
    rect: (f32, f32, f32, f32),
    tint: Color,
) {
    let (x, y, w, h) = rect;
    let (texture, iw, ih) = icon;
    if iw <= 0.0 || ih <= 0.0 || w <= 0.0 || h <= 0.0 {
        return;
    }
    let scale = (w / iw).min(h / ih);
    let (dw, dh) = (iw * scale, ih * scale);
    rc.sprite(
        texture,
        x + (w - dw) * 0.5,
        y + (h - dh) * 0.5,
        dw,
        dh,
        (0.0, 0.0, 1.0, 1.0),
        tint,
    );
}

fn size(ui: &UiRoot, me: ElementId) -> (f32, f32) {
    ui.element(me).map_or((0.0, 0.0), |e| (e.width, e.height))
}

fn enabled(ui: &UiRoot, me: ElementId) -> bool {
    ui.element(me).is_some_and(|e| e.enabled)
}

/// Whether a multi-select list's pick adds to the selection (and A selects
/// every row): Ctrl or the command key (Cmd on a Mac, the Windows key
/// elsewhere), as in OpenAC `UiSelectModifiers`.
fn adds_to_selection(modifiers: crate::event::Modifiers) -> bool {
    use crate::event::Modifiers;
    modifiers.contains(Modifiers::CTRL) || modifiers.contains(Modifiers::WIN)
}

/// The outline a tab stop with keyboard focus draws.
pub(crate) const KEYBOARD_FOCUS_OUTLINE: Color = Color::rgba(1.0, 0.82, 0.25, 1.0);

/// Keyboard focus on a tab stop (OpenAC `UiSimpleButton`/`UiMarkupToggle`
/// with `TabStop`): gaining and losing it switch the outline, and Enter,
/// keypad Enter or Space activates an enabled control. `Some(activate)`
/// when the event was one of those, which the control takes.
pub(crate) fn tab_stop_event(
    ui: &UiRoot,
    me: ElementId,
    e: &UiEvent,
    focused: &mut bool,
) -> Option<bool> {
    if e.target != Some(me) || !ui.element(me).is_some_and(|el| el.tab_stop) {
        return None;
    }
    match e.kind {
        kind::FOCUS_GAINED => {
            *focused = true;
            Some(false)
        }
        kind::FOCUS_LOST => {
            *focused = false;
            Some(false)
        }
        kind::KEY_DOWN
            if enabled(ui, me) && matches!(e.key, Key::Enter | Key::KeypadEnter | Key::Space) =>
        {
            Some(true)
        }
        _ => None,
    }
}

/// A line of text that sizes itself to its text (OpenAC `UiLabel`;
/// click-through).
#[derive(Debug, Clone)]
pub struct MarkupLabel {
    /// The text.
    pub text: String,
    /// Its colour.
    pub color: Color,
    /// Outlined (classic panels).
    pub outline: bool,
    /// The interface font.
    pub font: Option<Arc<UiFont>>,
    /// Inside a flex container: its size comes from the layout, so it no
    /// longer sizes itself to its text, its line is centred in the height
    /// it is given, and text wider than that is cut short with "...".
    pub sized_by_layout: bool,
    shown: Option<(String, f32, usize, String)>,
}

impl MarkupLabel {
    /// Empty white outlined text in `font`.
    #[must_use]
    pub fn new(font: Option<Arc<UiFont>>) -> Self {
        Self {
            text: String::new(),
            color: Color::WHITE,
            outline: true,
            font,
            sized_by_layout: false,
            shown: None,
        }
    }

    /// The text shortened to `width` with "...", measured as the layout
    /// measures, so a label given its text's width keeps it all. Cached per
    /// text, width and font.
    pub fn fitted(&mut self, width: f32) -> String {
        let font = self.font.as_ref().map_or(0, |f| Arc::as_ptr(f) as usize);
        if let Some((text, w, f, shown)) = &self.shown
            && *text == self.text
            && *w == width
            && *f == font
        {
            return shown.clone();
        }
        let measure_font = self.font.clone();
        let shown = ac_plugin_ui::chrome::ellipsize(
            &self.text,
            |s| measure(measure_font.as_ref(), s).ceil(),
            width,
        );
        self.shown = Some((self.text.clone(), width, font, shown.clone()));
        shown
    }
}

impl Widget for MarkupLabel {
    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let h = line_height(self.font.as_ref());
        if self.sized_by_layout {
            let (width, height) = size(ui, me);
            let y = ((height - h) / 2.0 + 0.5).floor();
            let shown = self.fitted(width);
            text(
                rc,
                self.font.as_ref(),
                &shown,
                0.0,
                y,
                self.color,
                self.outline,
            );
            return;
        }
        let w = measure(self.font.as_ref(), &self.text);
        if let Some(e) = ui.element_mut(me) {
            e.width = w;
            e.height = h;
        }
        text(
            rc,
            self.font.as_ref(),
            &self.text,
            0.0,
            0.0,
            self.color,
            self.outline,
        );
    }

    fn clips_children(&self) -> bool {
        false
    }
}

/// A push button (OpenAC `UiSimpleButton`), or a tab header (OpenAC
/// `UiMarkupTabButton`): a click sends [`ElementMessage::Clicked`].
#[derive(Debug, Clone)]
pub struct MarkupButton {
    /// The caption.
    pub text: String,
    /// Caption colour.
    pub text_color: Color,
    /// Fill.
    pub background: Color,
    /// Border.
    pub border: Color,
    /// Outlined caption.
    pub outline: bool,
    /// The interface font.
    pub font: Option<Arc<UiFont>>,
    /// An icon left of the caption.
    pub icon: Option<Icon>,
    /// The icon is a plugin file: where it says it drew it.
    pub icon_file: Option<FileTag>,
    /// A tab header: whether it is the selected tab (`None`: a button).
    pub tab_selected: Option<bool>,
    /// The theme's colours on a themed panel.
    pub palette: Option<Palette>,
    /// It has keyboard focus as a tab stop.
    pub keyboard_focused: bool,
    /// Where the pointer and left button are, for the themed look.
    pub pointer: PointerState,
}

impl MarkupButton {
    /// A button with OpenAC's colours.
    #[must_use]
    pub fn new(font: Option<Arc<UiFont>>) -> Self {
        Self {
            text: String::new(),
            text_color: Color::WHITE,
            background: Color::rgba(0.1, 0.1, 0.15, 0.8),
            border: Color::rgba(0.45, 0.45, 0.55, 1.0),
            outline: true,
            font,
            icon: None,
            icon_file: None,
            tab_selected: None,
            palette: None,
            keyboard_focused: false,
            pointer: PointerState::default(),
        }
    }

    /// A tab header: no fill or border, its caption coloured by state.
    #[must_use]
    pub fn tab(font: Option<Arc<UiFont>>) -> Self {
        Self {
            background: Color::TRANSPARENT,
            border: Color::TRANSPARENT,
            tab_selected: Some(false),
            ..Self::new(font)
        }
    }
}

impl Widget for MarkupButton {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.target == Some(me) {
            self.pointer.observe(e);
        }
        if let Some(activate) = tab_stop_event(ui, me, e, &mut self.keyboard_focused) {
            if activate {
                ui.send_message(me, ElementMessage::Clicked { x: 0, y: 0 });
            }
            return true;
        }
        if e.target != Some(me) || e.kind != kind::CLICK {
            return false;
        }
        if enabled(ui, me) {
            ui.send_message(
                me,
                ElementMessage::Clicked {
                    x: e.data1,
                    y: e.data2,
                },
            );
        }
        true
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = size(ui, me);
        let live = enabled(ui, me);
        if let Some(p) = &self.palette {
            let look = self.pointer.look(live);
            match self.tab_selected {
                Some(selected) => style::tab(rc, p, w, h, selected, look),
                None => style::button(rc, p, (0.0, 0.0, w, h), self.background, self.border, look),
            }
            if self.keyboard_focused {
                style::focus_ring(rc, p, w, h, style::CONTROL_RADIUS);
            }
        } else {
            if self.background.a > 0.0 {
                rc.fill(0.0, 0.0, w, h, self.background);
            }
            if self.border.a > 0.0 {
                rc.rect_outline(0.0, 0.0, w, h, self.border, 1.0);
            }
            if self.keyboard_focused {
                rc.rect_outline(1.0, 1.0, w - 2.0, h - 2.0, KEYBOARD_FOCUS_OUTLINE, 1.0);
            }
        }
        // A disabled themed control fades all of it, icon and caption too.
        let fade = |c: Color| {
            if self.palette.is_some() && !live {
                style::faded(c)
            } else {
                c
            }
        };
        let mut icon_column = 0.0;
        if let Some(file) = &self.icon_file {
            file.record();
        }
        if let Some(icon) = self.icon {
            let extent = (w.min(h) - 6.0).max(0.0);
            icon_column = extent + 6.0;
            // An unselected tab's icon recedes with its caption.
            let tint = match self.tab_selected {
                Some(false) => Color::rgba(1.0, 1.0, 1.0, INACTIVE_TAB_ICON_ALPHA),
                _ => Color::WHITE,
            };
            draw_icon_tinted(
                rc,
                icon,
                (3.0, (h - extent) * 0.5, extent, extent),
                fade(tint),
            );
        }
        let color = fade(match (self.tab_selected, &self.palette) {
            (None, _) => self.text_color,
            (Some(_), Some(p)) if !live => p.muted,
            (Some(_), None) if !live => Color::rgba(0.34, 0.33, 0.29, 1.0),
            (Some(true), Some(p)) => p.text,
            (Some(true), None) => Color::rgba(0.94, 0.76, 0.18, 1.0),
            (Some(false), Some(p)) => p.muted,
            (Some(false), None) => Color::rgba(0.78, 0.76, 0.67, 1.0),
        });
        if !self.text.is_empty() {
            let tw = measure(self.font.as_ref(), &self.text);
            let area = (w - icon_column).max(0.0);
            let x = icon_column + (area - tw) * 0.5;
            let y = (h - line_height(self.font.as_ref())) * 0.5;
            text(
                rc,
                self.font.as_ref(),
                &self.text,
                x,
                y,
                color,
                self.outline,
            );
        }
        if self.tab_selected == Some(true) && self.palette.is_none() {
            let underline = Color::rgba(0.77, 0.59, 0.12, 1.0);
            rc.fill(2.0, h - 2.0, (w - 4.0).max(0.0), 1.0, underline);
        }
    }
}

/// A check box with a caption (OpenAC `UiMarkupToggle`): a click sends
/// [`ElementMessage::Clicked`]; the plugin flips its bound state.
#[derive(Debug, Clone)]
pub struct MarkupToggle {
    /// The caption.
    pub text: String,
    /// Checked.
    pub checked: bool,
    /// Caption colour.
    pub color: Color,
    /// The interface font.
    pub font: Option<Arc<UiFont>>,
    /// The theme's colours on a themed panel.
    pub palette: Option<Palette>,
    /// It has keyboard focus as a tab stop.
    pub keyboard_focused: bool,
}

impl MarkupToggle {
    /// Unchecked, OpenAC's caption colour.
    #[must_use]
    pub fn new(font: Option<Arc<UiFont>>) -> Self {
        Self {
            text: String::new(),
            checked: false,
            color: Color::rgba(0.86, 0.84, 0.74, 1.0),
            font,
            palette: None,
            keyboard_focused: false,
        }
    }
}

impl Widget for MarkupToggle {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if let Some(activate) = tab_stop_event(ui, me, e, &mut self.keyboard_focused) {
            if activate {
                ui.send_message(me, ElementMessage::Clicked { x: 0, y: 0 });
            }
            return true;
        }
        if e.target != Some(me) || e.kind != kind::CLICK {
            return false;
        }
        if enabled(ui, me) {
            ui.send_message(
                me,
                ElementMessage::Clicked {
                    x: e.data1,
                    y: e.data2,
                },
            );
        }
        true
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = size(ui, me);
        let live = enabled(ui, me);
        let mut color = self.color;
        if !live {
            color.a = DISABLED_ALPHA;
        }
        let y = (h - line_height(self.font.as_ref())) * 0.5;
        if let Some(p) = &self.palette {
            let switch_y = ((h - style::SWITCH_HEIGHT) * 0.5).max(0.0);
            style::switch(rc, p, 1.0, switch_y, self.checked, live);
            if self.keyboard_focused {
                style::focus_ring(rc, p, w, h, style::SMALL_RADIUS);
            }
            let x = super::content_size::THEMED_TOGGLE_CAPTION_X;
            text(rc, self.font.as_ref(), &self.text, x, y, color, false);
            return;
        }
        if self.keyboard_focused {
            rc.rect_outline(0.0, 0.0, w, h, KEYBOARD_FOCUS_OUTLINE, 1.0);
        }
        let check_y = ((h - CHECK_SIZE) * 0.5).max(1.0);
        draw_check(rc, 1.0, check_y, self.checked, None);
        text(rc, self.font.as_ref(), &self.text, 17.0, y, color, true);
    }
}

/// An image fitted into the element, centred (OpenAC `UiMarkupIcon`).
#[derive(Debug, Clone, Default)]
pub struct MarkupIcon {
    /// What it shows.
    pub icon: Option<Icon>,
    /// It shows a plugin file: where it says it drew it.
    pub file: Option<FileTag>,
}

impl Widget for MarkupIcon {
    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = size(ui, me);
        if let Some(file) = &self.file {
            file.record();
        }
        if let Some(icon) = self.icon {
            draw_icon(rc, icon, 0.0, 0.0, w, h);
        }
    }
}

/// The panel's frame: the retail nine-piece chrome, or on a themed panel a
/// rounded, shadowed window in the theme's colours, with a band behind its
/// title (OpenAC `UiNineSlicePanel`, `UiPluginMarkupPanel`).
#[derive(Debug, Clone, Default)]
pub struct MarkupFrame {
    /// The retail chrome.
    pub chrome: NineSliceFrame,
    /// The theme in force on a themed panel.
    pub palette: Option<Palette>,
    /// It has a title, which a theme draws a band behind.
    pub has_title: bool,
}

impl Widget for MarkupFrame {
    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        match self.palette {
            Some(p) => {
                let (w, h) = size(ui, me);
                style::window_shadow(rc, w, h);
                let radii = CornerRadii::all(style::WINDOW_RADIUS);
                rc.fill_rounded_rect(0.0, 0.0, w, h, radii, p.background);
                if self.has_title {
                    style::header(rc, &p, w, style::HEADER_HEIGHT);
                }
            }
            None => self.chrome.draw(ui, me, rc),
        }
    }

    fn draw_after_children(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        match self.palette {
            Some(p) => {
                let (w, h) = size(ui, me);
                let radii = CornerRadii::all(style::WINDOW_RADIUS - 0.5);
                rc.stroke_rounded_rect(0.5, 0.5, w - 1.0, h - 1.0, radii, p.border, 1.0);
                if ui.element(me).is_some_and(|e| e.resizable) {
                    style::resize_grip(rc, &p, w, h);
                }
            }
            None => self.chrome.draw_after_children(ui, me, rc),
        }
    }
}

/// The retail vertical scrollbar art: track, up and down buttons, thumb
/// top, middle and bottom (OpenAC `RetailScrollbarChrome`).
pub mod vertical_bar {
    /// Track.
    pub const TRACK: u32 = 0x0600_4C5F;
    /// Up button.
    pub const UP: u32 = 0x0600_4C6C;
    /// Down button.
    pub const DOWN: u32 = 0x0600_4C69;
    /// Thumb top.
    pub const THUMB_TOP: u32 = 0x0600_4C60;
    /// Thumb middle.
    pub const THUMB_MID: u32 = 0x0600_4C63;
    /// Thumb bottom.
    pub const THUMB_BOT: u32 = 0x0600_4C66;
}

const SCROLL_BUTTON: f32 = 16.0;

/// Draws a vertical scrollbar at `x`, `width` wide, the element's height:
/// retail art, or the theme's slim thumb with no track, `active` while it is
/// held (OpenAC `UiMarkupList.DrawScrollbar`).
fn draw_vbar(
    rc: &mut RenderContext<'_>,
    x: f32,
    width: f32,
    height: f32,
    scroll: &Scrollable,
    palette: Option<&Palette>,
    active: bool,
) {
    if let Some(p) = palette {
        let button = SCROLL_BUTTON.min(height / 2.0);
        let (top, extent) = thumb_rect(scroll, button, (height - 2.0 * button).max(0.0));
        style::scroll_thumb(rc, p, x, top, width, extent, active);
        return;
    }
    let dec = SCROLL_BUTTON.clamp(0.0, height);
    let inc = SCROLL_BUTTON.clamp(0.0, height - dec);
    rc.surface(vertical_bar::TRACK, x, 0.0, width, height, Color::WHITE);
    let flat = |rc: &mut RenderContext<'_>, id: u32, y: f32, h: f32| {
        if h > 0.0 && rc.sprite_size(id).is_some() {
            rc.sprite(
                TextureRef::Surface(id),
                x,
                y,
                width,
                h,
                (0.0, 0.0, 1.0, 1.0),
                Color::WHITE,
            );
        }
    };
    flat(rc, vertical_bar::UP, 0.0, dec);
    flat(rc, vertical_bar::DOWN, height - inc, inc);
    let (top, extent) = thumb_rect(scroll, dec, (height - dec - inc).max(0.0));
    const CAP: f32 = 3.0;
    if extent >= 2.0 * CAP {
        flat(rc, vertical_bar::THUMB_TOP, top, CAP);
        rc.surface(
            vertical_bar::THUMB_MID,
            x,
            top + CAP,
            width,
            extent - 2.0 * CAP,
            Color::WHITE,
        );
        flat(rc, vertical_bar::THUMB_BOT, top + extent - CAP, CAP);
    } else {
        flat(rc, vertical_bar::THUMB_MID, top, extent);
    }
}

/// What a vertical scrollbar press at `y` does: step, page or grab the
/// thumb (the grab offset).
enum BarPress {
    Step(i32),
    Page(i32),
    Grab(f32),
}

fn press_vbar(scroll: &Scrollable, height: f32, y: f32) -> BarPress {
    let dec = SCROLL_BUTTON.clamp(0.0, height);
    let inc = SCROLL_BUTTON.clamp(0.0, height - dec);
    if y < dec {
        return BarPress::Step(-1);
    }
    if y >= height - inc {
        return BarPress::Step(1);
    }
    let (top, extent) = thumb_rect(scroll, dec, (height - dec - inc).max(0.0));
    if y >= top && y <= top + extent {
        BarPress::Grab(y - top)
    } else {
        BarPress::Page(if y < top { -1 } else { 1 })
    }
}

fn drag_ratio(scroll: &Scrollable, height: f32, y: f32, offset: f32) -> f32 {
    let dec = SCROLL_BUTTON.clamp(0.0, height);
    let inc = SCROLL_BUTTON.clamp(0.0, height - dec);
    let track = (height - dec - inc).max(0.0);
    let (_, extent) = thumb_rect(scroll, dec, track);
    let travel = (track - extent).max(1.0);
    (y - offset - dec) / travel
}

/// One list cell's content.
#[derive(Debug, Clone, PartialEq)]
pub enum Cell {
    /// Text in a colour.
    Text(String, Color),
    /// A check box.
    Check(bool),
    /// An icon (none for an empty one).
    Icon(Option<Icon>),
    /// A button with this caption (an empty caption draws no button).
    Button(String),
    /// Nothing (a short column).
    Empty,
}

/// A list of rows (OpenAC `UiMarkupList`): one column of text with
/// optional per-row colours and icons, or columns. A press on a row sends
/// [`ElementMessage::Clicked`] with the press point; the owner works out
/// what it means ([`MarkupList::press`]).
#[derive(Debug, Clone)]
pub struct MarkupList {
    /// The columns' layout and kinds (empty: the single-column form).
    pub columns: Vec<Column>,
    /// Each row's cells (one per column; the single-column form has one
    /// text cell and maybe an icon).
    pub rows: Vec<Vec<Cell>>,
    /// Each column's own row count.
    pub column_rows: Vec<usize>,
    /// The plugin file icons each row draws, by the plugin's row, and
    /// where the rows drawn say so.
    pub row_files: Vec<Vec<String>>,
    /// The window's drawn set, on a list with file icons.
    pub drawn_files: Option<DrawnFiles>,
    /// The single-column form's per-row icons.
    pub row_icons: Option<Vec<Option<Icon>>>,
    /// The selected row (-1: none).
    pub selected: i32,
    /// Row height.
    pub row_height: f32,
    /// A band behind the selected row.
    pub selection_band: bool,
    /// The interface font.
    pub font: Option<Arc<UiFont>>,
    /// The header's font under a theme (none: the interface font).
    pub header_font: Option<Arc<UiFont>>,
    /// Fill.
    pub background: Color,
    /// Border.
    pub border: Color,
    /// Default text colour.
    pub text_color: Color,
    /// Selection band.
    pub selected_color: Color,
    /// The theme's colours on a themed panel.
    pub palette: Option<Palette>,
    /// A button column's face; Classic matches a markup `<button>` (a
    /// themed window uses its background colour, the list itself being
    /// drawn in the field colour).
    pub button_color: Color,
    /// A button column's border (a themed window uses its border colour).
    pub button_border_color: Color,
    top: usize,
    revealed: i32,
    scroll: Scrollable,
    dragging: Option<f32>,
    /// The button cell under the pointer, as (row, column).
    hover: Option<(usize, usize)>,
    /// The button the left button went down on, and what the list showed
    /// then.
    press: Option<ButtonPress>,
    /// The pointer's last list-local position, so the hovered button can be
    /// found again when the rows move under a pointer that did not.
    pointer: Option<(f32, f32)>,
    /// A column list's header, sort, widths, multi-select and tree.
    pub ext: Option<Box<super::list_ext::ListExt>>,
    /// With an extension: every row in the plugin's order (`rows` is
    /// them as shown).
    pub all_rows: Vec<Vec<Cell>>,
    /// With an extension: the markup's columns (`columns` has the widths in
    /// force).
    pub markup_columns: Vec<Column>,
    /// The plugin row each shown row is.
    plugin_rows: Vec<usize>,
    /// It has the keyboard (a multi-select or tree list is a tab stop).
    focused: bool,
}

/// A held list button. A release fires by row index, so the press is
/// dropped if the list scrolls, its row count changes, or the pressed
/// button's caption changes before then.
#[derive(Debug, Clone, PartialEq)]
struct ButtonPress {
    row: usize,
    column: usize,
    top: usize,
    row_count: usize,
    caption: String,
}

/// The look a list button takes from the pointer and the list's enabled
/// flag (OpenAC `UiControlState`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ButtonLook {
    /// At rest.
    Normal,
    /// Under the pointer.
    Hovered,
    /// Held, the pointer still over it.
    Pressed,
    /// The list is disabled.
    Disabled,
}

const PADDING: f32 = 3.0;

/// `a` moved `t` of the way to `b`, keeping `a`'s alpha.
fn toward(a: Color, b: Color, t: f32) -> Color {
    Color::rgba(
        a.r + (b.r - a.r) * t,
        a.g + (b.g - a.g) * t,
        a.b + (b.b - a.b) * t,
        a.a,
    )
}

fn dimmed(mut color: Color) -> Color {
    color.a *= DISABLED_ALPHA;
    color
}

impl MarkupList {
    /// An empty list with OpenAC's colours.
    #[must_use]
    pub fn new(font: Option<Arc<UiFont>>, row_height: f32) -> Self {
        Self {
            columns: Vec::new(),
            rows: Vec::new(),
            column_rows: Vec::new(),
            row_icons: None,
            row_files: Vec::new(),
            drawn_files: None,
            selected: -1,
            row_height,
            selection_band: false,
            font,
            header_font: None,
            background: Color::rgba(0.0, 0.0, 0.0, 0.92),
            border: Color::rgba(0.46, 0.37, 0.16, 1.0),
            text_color: Color::rgba(0.91, 0.87, 0.76, 1.0),
            selected_color: Color::rgba(0.28, 0.23, 0.08, 0.95),
            palette: None,
            button_color: Color::rgba(0.1, 0.1, 0.15, 0.8),
            button_border_color: Color::rgba(0.45, 0.45, 0.55, 1.0),
            top: 0,
            revealed: i32::MIN,
            scroll: Scrollable::default(),
            dragging: None,
            hover: None,
            press: None,
            pointer: None,
            ext: None,
            all_rows: Vec::new(),
            markup_columns: Vec::new(),
            plugin_rows: Vec::new(),
            focused: false,
        }
    }

    /// The plugin row shown at `index` (the same without an extension).
    #[must_use]
    pub fn plugin_row(&self, index: usize) -> usize {
        if self.ext.is_some() {
            self.plugin_rows.get(index).copied().unwrap_or(index)
        } else {
            index
        }
    }

    /// The rows' area: the list less its header.
    fn rows_size(&self, ui: &UiRoot, me: ElementId) -> (f32, f32) {
        let (w, h) = size(ui, me);
        (w, (h - self.rows_top()).max(0.0))
    }

    /// Where the rows start (under the header).
    #[must_use]
    pub fn rows_top(&self) -> f32 {
        self.ext.as_ref().map_or(0.0, |x| x.rows_top())
    }

    /// With an extension: the order, widths and shown rows for now.
    pub fn arrange(&mut self) {
        let Some(ext) = self.ext.as_mut() else {
            return;
        };
        ext.refresh(&self.markup_columns, &self.all_rows);
        let view = &ext.view;
        self.plugin_rows = (0..view.view_count() as i32)
            .map(|v| usize::try_from(view.plugin_row(v)).unwrap_or(0))
            .collect();
        self.rows = self
            .plugin_rows
            .iter()
            .map(|r| self.all_rows[*r].clone())
            .collect();
        self.columns = ext.columns_now(&self.markup_columns);
        self.column_rows = vec![self.rows.len(); self.columns.len()];
    }

    fn has_buttons(&self) -> bool {
        self.columns
            .iter()
            .any(|c| matches!(c.kind, ColumnKind::Button { .. }))
    }

    /// The caption of a button cell, when it has one (an empty caption is
    /// no button).
    fn caption(&self, row: usize, column: usize) -> Option<&str> {
        if !matches!(
            self.columns.get(column).map(|c| &c.kind),
            Some(ColumnKind::Button { .. })
        ) {
            return None;
        }
        match self.rows.get(row)?.get(column)? {
            Cell::Button(caption) if !caption.is_empty() => Some(caption),
            _ => None,
        }
    }

    fn button_rect(
        &self,
        column: usize,
        cell_x: f32,
        cell_w: f32,
        row_y: f32,
    ) -> (f32, f32, f32, f32) {
        let (bw, bh) = match self.columns.get(column).map(|c| &c.kind) {
            Some(ColumnKind::Button {
                button_width,
                button_height,
                ..
            }) => (*button_width, *button_height),
            _ => (0.0, 0.0),
        };
        list::button_rect(bw, bh, cell_x, cell_w, row_y, self.row_height)
    }

    /// The button cell at a list-local point, as (row, column).
    fn button_cell_at(&self, width: f32, height: f32, x: f32, y: f32) -> Option<(usize, usize)> {
        let content = self.content_width(width, height);
        if self.columns.is_empty() || x < 0.0 || x >= content {
            return None;
        }
        let row = (y / self.row_height.max(1.0)).floor();
        if row < 0.0 || row as usize >= self.visible(height) {
            return None;
        }
        let index = self.top + row as usize;
        let layout = list::column_layout(&self.columns, content);
        for (c, &(cell_x, cell_w)) in layout.iter().enumerate() {
            if x < cell_x || x >= cell_x + cell_w {
                continue;
            }
            self.caption(index, c)?;
            let (bx, by, bw, bh) = self.button_rect(c, cell_x, cell_w, row * self.row_height);
            let on = x >= bx && x < bx + bw && y >= by && y < by + bh;
            return on.then_some((index, c));
        }
        None
    }

    /// The look of one button cell: pressed only while the pointer is still
    /// over the button it went down on, and no other button hovers while
    /// one is held; a cell with no caption is at rest (OpenAC
    /// `UiMarkupList.ButtonState`).
    #[must_use]
    pub fn button_look(&self, row: usize, column: usize, enabled: bool) -> ButtonLook {
        if self.caption(row, column).is_none() {
            return ButtonLook::Normal;
        }
        if !enabled {
            return ButtonLook::Disabled;
        }
        let over = self.hover == Some((row, column));
        match &self.press {
            Some(press) if press.row == row && press.column == column && over => {
                ButtonLook::Pressed
            }
            Some(_) => ButtonLook::Normal,
            None if over => ButtonLook::Hovered,
            None => ButtonLook::Normal,
        }
    }

    /// Finds the hovered button again from the last pointer position.
    fn refresh_hover(&mut self, width: f32, height: f32) {
        self.hover = match self.pointer {
            Some((x, y)) if self.dragging.is_none() => self.button_cell_at(width, height, x, y),
            _ => None,
        };
    }

    /// Drops the press when the row under it may no longer be the item that
    /// was pressed: the list scrolled, its row count changed, or the
    /// pressed button's caption changed or went empty. Other columns are
    /// left out on purpose: a live column such as a distance changes every
    /// frame and would make the buttons beside it unreliable.
    fn cancel_press_if_rows_moved(&mut self) {
        let Some(press) = &self.press else {
            return;
        };
        let moved = self.top != press.top
            || self.rows.len() != press.row_count
            || self.caption(press.row, press.column) != Some(press.caption.as_str());
        if moved {
            self.press = None;
        }
    }

    /// Follows the pointer over button cells and fires a button when the
    /// left button comes up over the one it went down on. Only a release
    /// that fires a button is handled here; everything else carries on to
    /// the list.
    fn track_button_pointer(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        let (w, h) = self.rows_size(ui, me);
        match e.kind {
            kind::HOVER_ENTER | kind::MOUSE_MOVE | kind::MOUSE_DOWN => {
                self.pointer = Some((e.data1 as f32, e.data2 as f32));
                self.refresh_hover(w, h);
                false
            }
            kind::HOVER_LEAVE => {
                self.pointer = None;
                self.hover = None;
                false
            }
            kind::CAPTURE_CHANGED => {
                self.press = None;
                false
            }
            kind::MOUSE_UP => {
                self.pointer = Some((e.data1 as f32, e.data2 as f32));
                self.cancel_press_if_rows_moved();
                let press = self.press.take();
                self.hover = self.button_cell_at(w, h, e.data1 as f32, e.data2 as f32);
                match press {
                    Some(p) if enabled(ui, me) && self.hover == Some((p.row, p.column)) => {
                        ui.send_message(
                            me,
                            ElementMessage::ListButtonClicked {
                                column: p.column,
                                row: p.row,
                            },
                        );
                        true
                    }
                    _ => false,
                }
            }
            _ => false,
        }
    }

    /// One button cell: inset by a pixel all round, its caption centred in
    /// the list's text colour and cut at the button's edges; a disabled
    /// list fades face, border and caption (OpenAC
    /// `UiMarkupList.DrawButtonCell`).
    #[allow(clippy::too_many_arguments)]
    fn draw_button_cell(
        &self,
        rc: &mut RenderContext<'_>,
        row: usize,
        column: usize,
        cell_x: f32,
        cell_w: f32,
        y: f32,
        enabled: bool,
    ) {
        let Some(caption) = self.caption(row, column) else {
            return;
        };
        let (x, top, w, h) = self.button_rect(column, cell_x, cell_w, y);
        let p = self.palette;
        let look = self.button_look(row, column, enabled);
        let mut text_color = p.map_or(self.text_color, |p| p.text);
        if let Some(p) = &p {
            // The list's own ground is the field colour, so its buttons take
            // the window's.
            style::button(rc, p, (x, top, w, h), p.background, p.border, look);
            if look == ButtonLook::Disabled {
                text_color = style::faded(text_color);
            }
        } else {
            let base = self.button_color;
            let mut border = self.button_border_color;
            let mut face = match look {
                ButtonLook::Hovered => toward(base, border, 0.3),
                ButtonLook::Pressed => toward(base, Color::rgba(0.0, 0.0, 0.0, 0.0), 0.25),
                _ => base,
            };
            if look == ButtonLook::Disabled {
                face = dimmed(face);
                border = dimmed(border);
                text_color = dimmed(text_color);
            }
            rc.fill(x, top, w, h, face);
            rc.rect_outline(x, top, w, h, border, 1.0);
        }
        rc.push_clip(x, top, w, h);
        let font = self.font.as_ref();
        text(
            rc,
            font,
            caption,
            x + (w - measure(font, caption)) * 0.5,
            top + (h - line_height(font)) * 0.5,
            text_color,
            p.is_none(),
        );
        rc.pop_clip();
    }

    /// The first row shown.
    #[must_use]
    pub fn top(&self) -> usize {
        self.top
    }

    fn visible(&self, height: f32) -> usize {
        list::visible_rows(height, self.row_height)
    }

    fn content_width(&self, width: f32, height: f32) -> f32 {
        if self.rows.len() > self.visible(height) {
            (width - list::SCROLLBAR_WIDTH).max(0.0)
        } else {
            width
        }
    }

    fn configure_scroll(&mut self, visible: usize) {
        let line = self.row_height.round().max(1.0) as i32;
        self.scroll.line_height = line;
        self.scroll
            .set_extents(self.rows.len() as i32 * line, visible as i32 * line, false);
        self.scroll.set_scroll_y(self.top as i32 * line);
    }

    /// A column list's own handling: the header (sorting, resizing,
    /// select-all), tree arrows, multi-select picks and Ctrl+A. `Some` when
    /// it took the event.
    fn ext_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> Option<bool> {
        let live = enabled(ui, me);
        let (w, h) = size(ui, me);
        let top = self.rows_top();
        let (_, rows_h) = self.rows_size(ui, me);
        let content = self.content_width(w, rows_h);
        let layout = list::column_layout(&self.columns, content);
        let (x, y) = (e.data1 as f32, e.data2 as f32);
        let row_plugin = {
            let index = ((y - top) / self.row_height.max(1.0)).floor();
            let shown = self.top as i64 + index as i64;
            (index >= 0.0
                && (index as usize) < self.visible(rows_h)
                && shown >= 0
                && (shown as usize) < self.rows.len())
            .then(|| self.plugin_row(shown as usize))
        };
        let ext = self.ext.as_mut()?;
        match e.kind {
            kind::FOCUS_GAINED => {
                self.focused = true;
                Some(true)
            }
            kind::FOCUS_LOST => {
                self.focused = false;
                Some(true)
            }
            kind::KEY_DOWN
                if self.focused
                    && live
                    && ext.multi
                    && e.key == Key::A
                    && adds_to_selection(e.modifiers) =>
            {
                ext.select_all_rows();
                Some(true)
            }
            kind::MOUSE_MOVE if ext.resizing() => {
                ext.drag_resize(&layout, x, content);
                self.arrange();
                Some(true)
            }
            kind::MOUSE_UP if ext.resizing() || (ext.header && y < top) => {
                ext.header_up(&self.markup_columns, &layout, x, y, &self.all_rows);
                self.arrange();
                Some(true)
            }
            kind::CAPTURE_CHANGED if ext.resizing() => {
                ext.header_up(&self.markup_columns, &layout, -1.0, -1.0, &self.all_rows);
                Some(true)
            }
            kind::DOUBLE_CLICK if ext.header && y < top => {
                if live {
                    ext.reset_width(&layout, x, y);
                    self.arrange();
                }
                Some(true)
            }
            kind::MOUSE_DOWN if ext.header && y >= 0.0 && y < top => {
                ext.header_down(&layout, x, y, live);
                if ext.resizing() {
                    ui.set_capture(me);
                }
                Some(true)
            }
            kind::CLICK if ext.header && y < top => Some(true),
            kind::MOUSE_DOWN if live && x < content => {
                let plugin = row_plugin?;
                if let Some(tree) = &ext.tree {
                    let cell_x = layout.get(tree.column).map_or(0.0, |l| l.0);
                    if ext.on_arrow(plugin, cell_x, x) {
                        ext.toggle_expanded(plugin);
                        self.arrange();
                        return Some(true);
                    }
                }
                if ext.multi {
                    let press = self.press(w, h, x, y);
                    if matches!(press, ColumnPress::Select(_)) {
                        let ext = self.ext.as_mut()?;
                        ext.pick(
                            plugin as i32,
                            adds_to_selection(e.modifiers),
                            e.modifiers.contains(crate::event::Modifiers::SHIFT),
                        );
                        return Some(true);
                    }
                }
                None
            }
            _ => None,
        }
    }

    /// The header row: captions, the sort glyph, select-all boxes and the
    /// lines between cells.
    fn draw_header(&mut self, rc: &mut RenderContext<'_>, w: f32, layout: &[(f32, f32)]) {
        let p = self.palette;
        let border = p.map_or(self.border, |p| p.border);
        let text_color = p.map_or(self.text_color, |p| p.text);
        let underline = super::list_ext::HEADER_HEIGHT - 1.0;
        if p.is_some() {
            // The header sits inside the list's rounded container.
            let inset = style::ROW_INSET;
            rc.fill(inset, underline, w - 2.0 * inset, 1.0, border);
        } else {
            rc.fill(0.0, 0.0, w, super::list_ext::HEADER_HEIGHT, self.background);
            rc.fill(0.0, underline, w, 1.0, border);
        }
        let font = self.header_font.clone().or_else(|| self.font.clone());
        let Some(ext) = self.ext.as_mut() else {
            return;
        };
        let (sorted, descending) = ext.current_sort(&self.markup_columns);
        let h = super::list_ext::HEADER_HEIGHT;
        for (c, &(cx, cw)) in layout.iter().enumerate() {
            if cw <= 0.0 {
                continue;
            }
            if c + 1 < layout.len() {
                rc.fill(cx + cw - 1.0, 2.0, 1.0, h - 4.0, border);
            }
            let column = &self.markup_columns[c];
            if let ColumnHeader::SelectAll(onall) = &column.header {
                if onall.is_some() {
                    let state = super::list_ext::ListExt::select_all_look(&self.all_rows, c);
                    super::controls::draw_check_box(
                        rc,
                        cx + 1.0 + ((cw - 2.0 - CHECK_SIZE).max(0.0) * 0.5),
                        ((h - CHECK_SIZE) * 0.5).max(1.0),
                        state,
                        p.as_ref(),
                    );
                }
                continue;
            }
            let caption = ext.captions.get(c).cloned().flatten().unwrap_or_default();
            let is_sorted = c as i32 == sorted;
            rc.push_clip(
                cx,
                0.0,
                (cw - if is_sorted { 12.0 } else { 0.0 }).max(0.0),
                h,
            );
            let ty = ((h - line_height(font.as_ref())) * 0.5).max(0.0);
            text(
                rc,
                font.as_ref(),
                &caption,
                cx + PADDING,
                ty,
                text_color,
                p.is_none(),
            );
            rc.pop_clip();
            if is_sorted {
                let left = cx + cw - 10.0;
                let top = ((h - 4.0) * 0.5).floor();
                for r in 0..4u8 {
                    let step = if descending { 3 - r } else { r };
                    let width = 1.0 + 2.0 * f32::from(step);
                    rc.fill(
                        left + (7.0 - width) * 0.5,
                        top + f32::from(r),
                        width,
                        1.0,
                        text_color,
                    );
                }
            }
        }
    }

    /// What a press at list-local (`x`, `y`) means, with the list as last
    /// drawn (OpenAC `UiMarkupList.OnEvent` / `OnEventColumns`).
    #[must_use]
    pub fn press(&self, width: f32, height: f32, x: f32, y: f32) -> ColumnPress {
        let top = self.rows_top();
        let (height, y) = ((height - top).max(0.0), y - top);
        if y < 0.0 {
            return ColumnPress::Nothing;
        }
        let visible = self.visible(height);
        if self.columns.is_empty() {
            let row = (y / self.row_height.max(1.0)).floor();
            let index = self.top as i64 + row as i64;
            return if row >= 0.0
                && (row as usize) < visible
                && index >= 0
                && (index as usize) < self.rows.len()
            {
                ColumnPress::Select(index as usize)
            } else {
                ColumnPress::Nothing
            };
        }
        let layout = list::column_layout(&self.columns, self.content_width(width, height));
        list::press_columns(
            &self.columns,
            &layout,
            &self.column_rows,
            self.top,
            visible,
            self.row_height,
            (x, y),
        )
    }
}

impl Widget for MarkupList {
    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.target != Some(me) {
            return false;
        }
        if self.ext.is_some()
            && let Some(taken) = self.ext_event(ui, me, e)
        {
            return taken;
        }
        // Below the header: the rows' own coordinates.
        let mut shifted = e.clone();
        shifted.data2 -= self.rows_top() as i32;
        let e = &shifted;
        let has_buttons = self.has_buttons();
        if has_buttons && self.track_button_pointer(ui, me, e) {
            return true;
        }
        let (w, h) = self.rows_size(ui, me);
        let visible = self.visible(h);
        let count = self.rows.len();
        // A list with nothing to scroll leaves the wheel to the group around it.
        match e.kind {
            kind::SCROLL if count > visible => {
                self.top = if e.data0 > 0 {
                    self.top.saturating_sub(1)
                } else {
                    self.top + 1
                };
                self.top = list::clamp_top(self.top, count, visible);
                if has_buttons {
                    // The rows moved under the pointer: a press no longer
                    // names the row it went down on, and the hovered button
                    // may differ.
                    self.cancel_press_if_rows_moved();
                    self.pointer = Some((e.data1 as f32, e.data2 as f32));
                    self.refresh_hover(w, h);
                }
                true
            }
            kind::MOUSE_MOVE if self.dragging.is_some() => {
                self.configure_scroll(visible);
                let ratio = drag_ratio(
                    &self.scroll,
                    h,
                    e.data2 as f32,
                    self.dragging.unwrap_or(0.0),
                );
                self.scroll.set_position_ratio(ratio);
                let line = self.row_height.round().max(1.0);
                self.top = list::clamp_top(
                    (self.scroll.scroll_y() as f32 / line).round() as usize,
                    count,
                    visible,
                );
                true
            }
            kind::MOUSE_UP | kind::CAPTURE_CHANGED if self.dragging.is_some() => {
                self.dragging = None;
                if ui.captured() == Some(me) {
                    ui.release_capture();
                }
                true
            }
            kind::MOUSE_DOWN => {
                let content = self.content_width(w, h);
                if count > visible && e.data1 as f32 >= content {
                    self.configure_scroll(visible);
                    match press_vbar(&self.scroll, h, e.data2 as f32) {
                        BarPress::Step(d) => {
                            self.top = list::clamp_top(
                                (self.top as i64 + i64::from(d)).max(0) as usize,
                                count,
                                visible,
                            );
                        }
                        BarPress::Page(d) => {
                            let next = self.top as i64 + i64::from(d) * visible as i64;
                            self.top = list::clamp_top(next.max(0) as usize, count, visible);
                        }
                        BarPress::Grab(offset) => {
                            self.dragging = Some(offset);
                            ui.set_capture(me);
                        }
                    }
                    return true;
                }
                if enabled(ui, me) {
                    // A press on a button fires when it comes up over the
                    // same button (`track_button_pointer`).
                    if let Some((row, column)) = self.hover {
                        self.press = self.caption(row, column).map(|caption| ButtonPress {
                            row,
                            column,
                            top: self.top,
                            row_count: count,
                            caption: caption.to_owned(),
                        });
                    }
                    ui.send_message(
                        me,
                        ElementMessage::Clicked {
                            x: e.data1,
                            y: e.data2 + self.rows_top() as i32,
                        },
                    );
                }
                true
            }
            _ => false,
        }
    }

    fn receives_hover_mouse_move(&self) -> bool {
        self.has_buttons()
    }

    fn on_enabled_changed(&mut self, ui: &mut UiRoot, me: ElementId) {
        if !enabled(ui, me) {
            self.press = None;
        }
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        self.arrange();
        let (w, h) = self.rows_size(ui, me);
        let visible = self.visible(h);
        let count = self.rows.len();
        let shown_selected = if self.ext.is_some() {
            self.plugin_rows
                .iter()
                .position(|r| *r as i32 == self.selected)
                .map_or(-1, |i| i as i32)
        } else {
            self.selected
        };
        if shown_selected != self.revealed {
            self.revealed = shown_selected;
            self.top = list::reveal(self.top, shown_selected, count, visible);
        }
        let header_top = self.rows_top();
        if let Some(p) = &self.palette {
            let (outer_w, outer_h) = size(ui, me);
            let radius = style::CONTAINER_RADIUS;
            style::surface(rc, 0.0, 0.0, outer_w, outer_h, radius, p.field, p.border);
        }
        if header_top > 0.0 {
            let content = self.content_width(w, h);
            let layout = list::column_layout(&self.columns, content);
            self.draw_header(rc, w, &layout);
        }
        rc.push_transform(0.0, header_top);
        self.top = list::clamp_top(self.top, count, visible);
        if self.has_buttons() {
            self.cancel_press_if_rows_moved();
            self.refresh_hover(w, h);
        }
        let is_enabled = enabled(ui, me);
        let content = self.content_width(w, h);
        let p = self.palette;
        if p.is_none() {
            rc.fill(0.0, 0.0, w, h, self.background);
            rc.rect_outline(0.0, 0.0, w, h, self.border, 1.0);
        }
        let band = self.selection_band || p.is_some();
        let font = self.font.clone();
        let text_y = (self.row_height - line_height(font.as_ref())).max(0.0) * 0.5;
        let layout = if self.columns.is_empty() {
            Vec::new()
        } else {
            list::column_layout(&self.columns, content)
        };
        let icon_column = if self.row_icons.is_some() {
            (self.row_height - 2.0).max(0.0)
        } else {
            0.0
        };
        let end = count.min(self.top + visible);
        for index in self.top..end {
            let y = (index - self.top) as f32 * self.row_height;
            let plugin = self.plugin_row(index);
            if let (Some(drawn), Some(files)) = (&self.drawn_files, self.row_files.get(plugin)) {
                for path in files {
                    drawn.record(path);
                }
            }
            let banded = match &self.ext {
                Some(ext) if ext.multi => ext.banded(plugin),
                Some(_) => band && plugin as i32 == self.selected,
                None => {
                    band && i32::try_from(index).ok() == Some(self.selected)
                        && (self.columns.is_empty() || p.is_some())
                }
            };
            if banded {
                match &p {
                    Some(p) => style::row(rc, 0.0, y, content, self.row_height, p.selected),
                    None => rc.fill(
                        1.0,
                        y + 1.0,
                        content - 2.0,
                        self.row_height - 1.0,
                        self.selected_color,
                    ),
                }
            }
            if self.columns.is_empty() {
                if let Some(Some(icon)) = self.row_icons.as_ref().and_then(|icons| icons.get(index))
                {
                    let extent = (icon_column - 2.0).max(0.0);
                    draw_icon(
                        rc,
                        *icon,
                        1.0,
                        y + (self.row_height - extent) * 0.5,
                        extent,
                        extent,
                    );
                }
                if let Some(Cell::Text(s, color)) = self.rows[index].first() {
                    text(
                        rc,
                        font.as_ref(),
                        s,
                        PADDING + icon_column,
                        y + text_y,
                        *color,
                        p.is_none(),
                    );
                }
                continue;
            }
            for (c, cell) in self.rows[index].iter().enumerate() {
                let Some(&(cx, cw)) = layout.get(c) else {
                    continue;
                };
                if cw <= 0.0 {
                    continue;
                }
                rc.push_clip(cx, y, cw, self.row_height);
                match cell {
                    Cell::Text(s, color) => {
                        // A tree column: indent, an arrow on a parent, the text
                        // after the arrow slot.
                        let tree = self
                            .ext
                            .as_ref()
                            .and_then(|x| x.tree.as_ref().map(|t| t.column))
                            .filter(|column| *column == c)
                            .and_then(|_| self.ext.as_ref()?.tree_row(plugin));
                        let mut tx = cx + PADDING;
                        if let Some((level, parent, expanded)) = tree {
                            let slot = cx + PADDING + level as f32 * super::list_ext::TREE_INDENT;
                            if let (true, Some(p)) = (parent, &p) {
                                // A smooth chevron: down when expanded, right
                                // when collapsed.
                                let cy = y + self.row_height * 0.5;
                                if expanded {
                                    style::chevron(rc, slot + 2.0, cy - 2.0, 7.0, 4.0, p.text);
                                } else {
                                    let x = slot + 3.5;
                                    style::chevron_right(rc, x, cy - 3.5, 3.5, 7.0, p.text);
                                }
                            } else if parent {
                                let arrow = self.text_color;
                                let top = y + ((self.row_height - 7.0) * 0.5).floor();
                                for i in 0..4u8 {
                                    let i = f32::from(i);
                                    let length = 7.0 - 2.0 * i;
                                    if expanded {
                                        rc.fill(slot + 2.0 + i, top + 1.0 + i, length, 1.0, arrow);
                                    } else {
                                        rc.fill(slot + 3.0 + i, top + i, 1.0, length, arrow);
                                    }
                                }
                            }
                            tx = slot + super::list_ext::TREE_INDENT;
                        }
                        text(rc, font.as_ref(), s, tx, y + text_y, *color, p.is_none());
                    }
                    Cell::Check(checked) => {
                        let extent = (cw - 2.0).max(0.0);
                        let lx = cx + 1.0 + ((extent - CHECK_SIZE) * 0.5).max(0.0);
                        let ly = y + ((self.row_height - CHECK_SIZE) * 0.5).max(1.0);
                        draw_check(rc, lx, ly, *checked, p.as_ref());
                    }
                    Cell::Icon(Some(icon)) => {
                        draw_icon(
                            rc,
                            *icon,
                            cx + 1.0,
                            y + 1.0,
                            (cw - 2.0).max(0.0),
                            (self.row_height - 2.0).max(0.0),
                        );
                    }
                    Cell::Button(_) => {
                        self.draw_button_cell(rc, index, c, cx, cw, y, is_enabled);
                    }
                    Cell::Icon(None) | Cell::Empty => {}
                }
                rc.pop_clip();
            }
        }
        if count > visible {
            self.configure_scroll(visible);
            draw_vbar(
                rc,
                content,
                list::SCROLLBAR_WIDTH,
                h,
                &self.scroll,
                p.as_ref(),
                self.dragging.is_some(),
            );
        }
        rc.pop_transform();
    }
}

/// A wrapped transcript that follows new entries only while the reader is
/// at its end (OpenAC `UiMarkupLog`).
#[derive(Debug, Clone)]
pub struct MarkupLog {
    /// The interface font.
    pub font: Option<Arc<UiFont>>,
    /// Text colour.
    pub text_color: Color,
    /// The theme's colours on a themed panel.
    pub palette: Option<Palette>,
    entries: Vec<String>,
    first: i32,
    rows: Vec<(i64, usize, String)>,
    scroll: Scrollable,
    follow_end: bool,
    laid_out: Option<(f32, f32)>,
    dirty: bool,
    dragging: Option<f32>,
}

impl MarkupLog {
    /// An empty log.
    #[must_use]
    pub fn new(font: Option<Arc<UiFont>>) -> Self {
        Self {
            font,
            text_color: Color::rgba(0.91, 0.87, 0.76, 1.0),
            palette: None,
            entries: Vec::new(),
            first: 0,
            rows: Vec::new(),
            scroll: Scrollable::default(),
            follow_end: true,
            laid_out: None,
            dirty: true,
            dragging: None,
        }
    }

    /// The entries and the number of the first one.
    pub fn set_entries(&mut self, entries: &[String], first: i32) {
        if self.entries != entries || self.first != first {
            self.entries = entries.to_vec();
            self.first = first;
            self.dirty = true;
        }
    }

    /// Another font: the rows are wrapped again (OpenAC `UiMarkupLog`
    /// re-lays out when its font changes).
    pub fn set_font(&mut self, font: Option<Arc<UiFont>>) {
        let same = match (&self.font, &font) {
            (Some(a), Some(b)) => Arc::ptr_eq(a, b),
            (None, None) => true,
            _ => false,
        };
        if !same {
            self.font = font;
            self.dirty = true;
        }
    }

    /// Whether it follows the newest entry.
    #[must_use]
    pub fn following_end(&self) -> bool {
        self.follow_end
    }

    fn line_height(&self) -> i32 {
        (line_height(self.font.as_ref()).ceil() as i32 + 2).max(14)
    }

    /// The scroll position, for tests.
    #[cfg(test)]
    pub(crate) fn scroll(&self) -> &Scrollable {
        &self.scroll
    }

    /// Scrolls to `y` from outside, as OpenAC's tests move the log's
    /// scroll model: following resumes exactly when it lands at the end.
    #[cfg(test)]
    pub(crate) fn set_scroll_y(&mut self, y: i32) {
        self.scroll.set_scroll_y(y);
        self.follow_end = self.scroll.at_end();
    }

    /// The entry number and text of the top visible row, for tests.
    #[cfg(test)]
    pub(crate) fn first_visible(&self) -> Option<(i64, String)> {
        let line = self.line_height();
        let index = (self.scroll.scroll_y() / line).max(0) as usize;
        self.rows
            .get(index.min(self.rows.len().checked_sub(1)?))
            .map(|(entry, _, text)| (*entry, text.clone()))
    }

    fn wrap(&self, entry: i64, text_in: &str, available: f32, out: &mut Vec<(i64, usize, String)>) {
        let chars: Vec<char> = text_in.chars().collect();
        if chars.is_empty() {
            out.push((entry, 0, String::new()));
            return;
        }
        let advance = |c: char| self.font.as_ref().map_or(7.0, |f| f.advance(c));
        let mut start = 0usize;
        while start < chars.len() {
            let mut end = start;
            let mut space: Option<usize> = None;
            let mut width = 0.0;
            while end < chars.len() && chars[end] != '\n' {
                let a = advance(chars[end]);
                if end > start && width + a > available {
                    break;
                }
                width += a;
                if chars[end].is_whitespace() {
                    space = Some(end);
                }
                end += 1;
            }
            let newline = end < chars.len() && chars[end] == '\n';
            if !newline
                && end < chars.len()
                && let Some(s) = space.filter(|s| *s > start)
            {
                end = s;
            }
            let line: String = chars[start..end].iter().collect();
            out.push((entry, start, line.trim_end_matches('\r').to_owned()));
            start = end;
            if start < chars.len() && chars[start].is_whitespace() {
                start += 1;
            }
        }
    }

    fn refresh(&mut self, w: f32, h: f32) {
        if !self.dirty && self.laid_out == Some((w, h)) {
            return;
        }
        let line = self.line_height();
        let anchor = (!self.rows.is_empty()).then(|| {
            let i = (self.scroll.scroll_y() / line).max(0) as usize;
            self.rows[i.min(self.rows.len() - 1)].clone()
        });
        let remainder = self.scroll.scroll_y() % line;
        let available = (w - 26.0).max(1.0);
        let mut rows = Vec::new();
        for (i, entry) in self.entries.iter().enumerate() {
            self.wrap(
                i64::from(self.first) + i as i64,
                entry,
                available,
                &mut rows,
            );
        }
        self.rows = rows;
        self.scroll.line_height = line;
        self.scroll
            .set_extents(self.rows.len() as i32 * line, (h as i32 - 8).max(0), false);
        if self.follow_end {
            self.scroll.scroll_to_end();
        } else if let Some((entry, offset, _)) = anchor {
            let index = self
                .rows
                .iter()
                .rposition(|(e, o, _)| *e == entry && *o <= offset);
            self.scroll
                .set_scroll_y(index.map_or(0, |i| i as i32 * line + remainder.min(line - 1)));
        }
        self.laid_out = Some((w, h));
        self.dirty = false;
    }
}

impl Widget for MarkupLog {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.target != Some(me) {
            return false;
        }
        let (w, h) = size(ui, me);
        self.refresh(w, h);
        match e.kind {
            // A log with nothing to scroll leaves the wheel to the group
            // around it.
            kind::SCROLL if self.scroll.has_overflow() => {
                self.scroll.scroll_by_lines(-e.data0 * 3);
                self.follow_end = self.scroll.at_end();
                true
            }
            kind::MOUSE_DOWN if self.scroll.has_overflow() && e.data1 as f32 >= w - 17.0 => {
                match press_vbar(&self.scroll, h - 2.0, e.data2 as f32 - 1.0) {
                    BarPress::Step(d) => self.scroll.scroll_by_lines(d),
                    BarPress::Page(d) => self
                        .scroll
                        .scroll_by_lines(d * ((h as i32 - 8) / self.line_height()).max(1)),
                    BarPress::Grab(offset) => {
                        self.dragging = Some(offset);
                        ui.set_capture(me);
                    }
                }
                self.follow_end = self.scroll.at_end();
                true
            }
            kind::MOUSE_MOVE if self.dragging.is_some() => {
                let ratio = drag_ratio(
                    &self.scroll,
                    h - 2.0,
                    e.data2 as f32 - 1.0,
                    self.dragging.unwrap_or(0.0),
                );
                self.scroll.set_position_ratio(ratio);
                self.follow_end = self.scroll.at_end();
                true
            }
            kind::MOUSE_UP | kind::CAPTURE_CHANGED if self.dragging.is_some() => {
                self.dragging = None;
                if ui.captured() == Some(me) {
                    ui.release_capture();
                }
                true
            }
            _ => false,
        }
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = size(ui, me);
        self.refresh(w, h);
        let p = self.palette;
        if let Some(p) = &p {
            let radius = style::CONTAINER_RADIUS;
            style::surface(rc, 0.0, 0.0, w, h, radius, p.field, p.border);
        } else {
            rc.fill(0.0, 0.0, w, h, Color::rgba(0.0, 0.0, 0.0, 0.92));
            rc.rect_outline(0.0, 0.0, w, h, Color::rgba(0.46, 0.37, 0.16, 1.0), 1.0);
        }
        let line = self.line_height();
        rc.push_clip(4.0, 4.0, (w - 25.0).max(0.0), (h - 8.0).max(0.0));
        let top = (self.scroll.scroll_y() / line).max(0) as usize;
        let font = self.font.clone();
        let color = p.map_or(self.text_color, |p| p.text);
        for (i, (_, _, row)) in self.rows.iter().enumerate().skip(top) {
            let y = 4.0 + (i as i32 * line - self.scroll.scroll_y()) as f32;
            if y >= h - 4.0 {
                break;
            }
            text(rc, font.as_ref(), row, 4.0, y, color, p.is_none());
        }
        rc.pop_clip();
        if self.scroll.has_overflow() {
            rc.push_transform(0.0, 1.0);
            draw_vbar(
                rc,
                (w - 17.0).max(0.0),
                16.0,
                (h - 2.0).max(0.0),
                &self.scroll,
                p.as_ref(),
                self.dragging.is_some(),
            );
            rc.pop_transform();
        }
    }
}
