//! The title bar of a plugin window with chrome: the title, shortened with
//! "..." when it does not fit, and the close button at the right. The bar
//! lets presses through, so dragging it moves the window. Under a theme the
//! title takes the title weight and the theme's colour, and the close button
//! is a ghost X.

use std::sync::Arc;

use ac_plugin_ui::chrome::{
    BORDER, CLASSIC_CLOSE_PRESSED_SPRITE, CLASSIC_CLOSE_SPRITE, CLOSE_BUTTON_SIZE,
    TITLE_BAR_HEIGHT, ellipsize,
};

use super::style::{self, PointerState};
use super::widgets::{ButtonLook, Palette};
use crate::draw::{Color, RenderContext};
use crate::event::{UiEvent, kind};
use crate::font::UiFont;
use crate::geometry::AnchorEdges;
use crate::root::{ElementMessage, UiRoot};
use crate::tree::{Element, ElementId, Widget};

/// Where the title starts in Classic.
pub const CLASSIC_TEXT_LEFT: f32 = 8.0;
/// Where the title starts under a theme.
pub const THEMED_TEXT_LEFT: f32 = 12.0;
/// The themed close button's top.
pub const THEMED_CLOSE_TOP: f32 = 3.0;
const TEXT_GAP: f32 = 4.0;
const CLASSIC_CLOSE_HOVER: Color = Color::rgba(1.0, 1.0, 1.0, 0.18);

/// The bar: the title, and the close button as its child.
#[derive(Debug, Clone)]
pub struct TitleBar {
    /// The title (the markup's, else the registration's).
    pub title: String,
    /// Its font.
    pub font: Option<Arc<UiFont>>,
    /// Its colour.
    pub color: Color,
    /// Outlined (Classic).
    pub outline: bool,
    /// The theme's colours (`None`: Classic).
    pub palette: Option<Palette>,
    /// The close button.
    pub close: ElementId,
    shown: Option<(String, f32, usize, String)>,
}

impl TitleBar {
    /// Adds a bar for a frame `frame_width` wide to `frame`.
    pub fn add(
        ui: &mut UiRoot,
        frame: ElementId,
        frame_width: f32,
        font: Option<Arc<UiFont>>,
    ) -> ElementId {
        let mut c = Element::at(
            frame_width - TITLE_BAR_HEIGHT,
            BORDER,
            CLOSE_BUTTON_SIZE,
            CLOSE_BUTTON_SIZE,
        )
        .with_anchors(AnchorEdges::TOP | AnchorEdges::RIGHT);
        c.accepts_focus = true;
        c.tab_stop = true;
        c.focus_on_mouse_click = false;
        c.name = Some("PluginClose".to_owned());
        c.runtime_tooltip_text = Some(ac_plugin_ui::chrome::CLOSE_TOOLTIP.to_owned());
        let close = ui.insert(c, CloseButton::default());
        let mut e = Element::at(0.0, 0.0, frame_width, TITLE_BAR_HEIGHT)
            .with_anchors(AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::RIGHT);
        e.click_through = true;
        e.name = Some("PluginTitleBar".to_owned());
        let bar = ui.add(
            frame,
            e,
            Self {
                title: String::new(),
                font,
                color: Color::WHITE,
                outline: true,
                palette: None,
                close,
                shown: None,
            },
        );
        ui.add_child(bar, close);
        bar
    }

    /// Sizes the bar to a frame `frame_width` wide.
    pub fn set_frame_width(ui: &mut UiRoot, bar: ElementId, frame_width: f32) {
        let Some(close) = ui.widget::<Self>(bar).map(|b| b.close) else {
            return;
        };
        if let Some(e) = ui.element_mut(bar) {
            e.width = frame_width;
        }
        if let Some(e) = ui.element_mut(close) {
            e.left = frame_width - TITLE_BAR_HEIGHT;
        }
        ui.capture_current_anchor_baseline(close);
    }

    /// Themes the bar and its close button (`None`: Classic).
    pub fn set_palette(
        ui: &mut UiRoot,
        bar: ElementId,
        palette: Option<Palette>,
        font: Option<Arc<UiFont>>,
        classic_color: Color,
    ) {
        let Some(close) = ui.widget_mut::<Self>(bar).map(|b| {
            b.palette = palette;
            b.font = font.clone();
            b.color = palette.map_or(classic_color, |p| p.text);
            b.outline = palette.is_none();
            b.close
        }) else {
            return;
        };
        if let Some(c) = ui.widget_mut::<CloseButton>(close) {
            c.palette = palette;
        }
        if let Some(e) = ui.element_mut(close) {
            e.top = if palette.is_none() {
                BORDER
            } else {
                THEMED_CLOSE_TOP
            };
        }
        // The anchor pass resets the top to the captured baseline every
        // draw; capture again so a live switch sticks.
        ui.capture_current_anchor_baseline(close);
    }

    /// The title as drawn in `width`: shortened to fit beside the close
    /// button. Cached per title, width and font.
    pub fn displayed_title(&mut self, bar_width: f32, measure: impl Fn(&str) -> f32) -> String {
        let left = self.text_left();
        let width = (bar_width - TITLE_BAR_HEIGHT - TEXT_GAP - left).max(0.0);
        let font = self.font.as_ref().map_or(0, |f| Arc::as_ptr(f) as usize);
        if let Some((title, w, f, shown)) = &self.shown
            && *title == self.title
            && *w == width
            && *f == font
        {
            return shown.clone();
        }
        let shown = ellipsize(&self.title, measure, width);
        self.shown = Some((self.title.clone(), width, font, shown.clone()));
        shown
    }

    fn text_left(&self) -> f32 {
        if self.palette.is_none() {
            CLASSIC_TEXT_LEFT
        } else {
            THEMED_TEXT_LEFT
        }
    }

    /// Where the title's line starts for a line `line_height` tall.
    #[must_use]
    pub fn text_origin(&self, line_height: f32) -> (f32, f32) {
        if self.palette.is_none() {
            (
                CLASSIC_TEXT_LEFT,
                BORDER + ((CLOSE_BUTTON_SIZE - line_height) / 2.0 + 0.5).floor(),
            )
        } else {
            (
                THEMED_TEXT_LEFT,
                ((TITLE_BAR_HEIGHT - line_height) / 2.0 + 0.5).floor(),
            )
        }
    }
}

impl Widget for TitleBar {
    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        if self.title.is_empty() {
            return;
        }
        let width = ui.element(me).map_or(0.0, |e| e.width);
        let font = self.font.clone();
        let text =
            self.displayed_title(width, |s| super::content_size::text_width(font.as_ref(), s));
        if text.is_empty() {
            return;
        }
        let line = super::content_size::line_height(self.font.as_ref());
        let (x, y) = self.text_origin(line);
        if let Some(font) = &self.font {
            rc.string_dat(
                font,
                &text,
                x,
                y,
                self.color,
                self.outline,
                crate::draw::DEFAULT_OUTLINE_COLOR,
            );
        }
    }

    fn clips_children(&self) -> bool {
        false
    }
}

/// The close button: the inventory window's close art in Classic, a ghost X
/// under a theme. A click sends [`ElementMessage::Clicked`].
#[derive(Debug, Clone, Default)]
pub struct CloseButton {
    /// The theme (`None`: the Classic art).
    pub palette: Option<Palette>,
    pointer: PointerState,
    keyboard_focused: bool,
}

impl CloseButton {
    /// Whether it draws themed.
    #[must_use]
    pub fn is_themed(&self) -> bool {
        self.palette.is_some()
    }
}

impl Widget for CloseButton {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        self.pointer.observe(e);
        if let Some(activate) =
            super::widgets::tab_stop_event(ui, me, e, &mut self.keyboard_focused)
        {
            if activate {
                ui.send_message(me, ElementMessage::Clicked { x: 0, y: 0 });
            }
            return true;
        }
        if e.target == Some(me) && e.kind == kind::CLICK {
            ui.send_message(me, ElementMessage::Clicked { x: 0, y: 0 });
            return true;
        }
        false
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = ui.element(me).map_or((0.0, 0.0), |e| (e.width, e.height));
        let look = self.pointer.look(true);
        if let Some(p) = &self.palette {
            style::close_button(rc, p, w, h, look, self.keyboard_focused);
            return;
        }
        let mut sprite = None;
        if look == ButtonLook::Pressed {
            sprite = rc
                .sprite_size(CLASSIC_CLOSE_PRESSED_SPRITE)
                .map(|s| (CLASSIC_CLOSE_PRESSED_SPRITE, s));
        }
        if sprite.is_none() {
            sprite = rc
                .sprite_size(CLASSIC_CLOSE_SPRITE)
                .map(|s| (CLASSIC_CLOSE_SPRITE, s));
        }
        if let Some((file, (tw, th))) = sprite
            && tw > 0.0
            && th > 0.0
        {
            let scale = (w / tw).min(h / th);
            let (sw, sh) = (tw * scale, th * scale);
            let (x, y) = ((w - sw) / 2.0, (h - sh) / 2.0);
            rc.surface(file, x, y, sw, sh, Color::WHITE);
            if look == ButtonLook::Hovered {
                rc.fill(x, y, sw, sh, CLASSIC_CLOSE_HOVER);
            }
        }
        if self.keyboard_focused {
            rc.rect_outline(
                1.0,
                1.0,
                w - 2.0,
                h - 2.0,
                super::widgets::KEYBOARD_FOCUS_OUTLINE,
                1.0,
            );
        }
    }
}
