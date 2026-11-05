//! The meter: a bar filled to a share — vitals, the selected object's
//! health, power bars. A back track and a front fill, each a three-piece
//! (left cap, tiled middle, right cap) image clipped to the fill; animated
//! tracks; per-state fills and captions; the vitals detail icons.

use std::sync::Arc;

use crate::draw::{Color, DEFAULT_OUTLINE_COLOR, RenderContext, TextureRef};
use crate::font::UiFont;
use crate::geometry::PixelRect;
use crate::layout_policy::LayoutPolicy;
use crate::media::LoopingAnimation;
use crate::plugin::Palette;
use crate::plugin::style;
use crate::root::UiRoot;
use crate::states;
use crate::tree::{Element, ElementId, Widget};
use crate::widgets::ChildImport;

/// Where a meter caption sits.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum MeterLabelAlign {
    /// Flush left.
    Left,
    /// Centred.
    #[default]
    Center,
    /// Flush right.
    Right,
}

/// One vitals detail icon absorbed from a meter's slice container: its
/// image, its authored rectangle (local to the container, which spans the
/// meter), its authored edge modes and the container's authored size.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct DetailOverlay {
    /// The ShowDetail image.
    pub sprite: u32,
    /// Left.
    pub x: f32,
    /// Top.
    pub y: f32,
    /// Width.
    pub w: f32,
    /// Height.
    pub h: f32,
    /// Left edge mode.
    pub left_mode: u32,
    /// Top edge mode.
    pub top_mode: u32,
    /// Right edge mode.
    pub right_mode: u32,
    /// Bottom edge mode.
    pub bottom_mode: u32,
    /// The container's authored width.
    pub parent_w: f32,
    /// The container's authored height.
    pub parent_h: f32,
}

/// A meter.
#[derive(Debug, Clone)]
pub struct Meter {
    /// Dat element id.
    pub element_id: u32,
    /// The fill share, 0..=1; `None` draws an empty bar.
    pub fill: Option<f32>,
    /// A caption drawn over the bar (wins over a state caption).
    pub label: Option<String>,
    /// Plain fill colour (no images).
    pub bar_color: Color,
    /// The plugin theme the plain look draws in (none: flat).
    pub theme_palette: Option<Palette>,
    /// Plain background colour (no images).
    pub bg_color: Color,
    /// Caption colour.
    pub label_color: Color,
    /// Outlined caption.
    pub outline: bool,
    /// Caption outline colour.
    pub outline_color: Color,
    /// Caption font.
    pub font: Option<Arc<UiFont>>,
    /// Empty-track left cap.
    pub back_left: u32,
    /// Empty-track middle (tiled).
    pub back_tile: u32,
    /// Empty-track right cap.
    pub back_right: u32,
    /// Fill left cap.
    pub front_left: u32,
    /// Fill middle (tiled).
    pub front_tile: u32,
    /// Fill right cap.
    pub front_right: u32,
    /// Fills along y.
    pub vertical: bool,
    /// A vertical fill grows from the bottom.
    pub fill_from_bottom: bool,

    state_fill_sprites: Vec<(u32, u32)>,
    state_labels: Vec<(u32, String, MeterLabelAlign)>,
    active_state_label: Option<(String, MeterLabelAlign)>,
    animated: Option<(Arc<LoopingAnimation>, Arc<LoopingAnimation>)>,
    animation_started: Option<f64>,
    detail_configured: bool,
    detail_pass_to_children: bool,
    detail_back: DetailOverlay,
    detail_front: DetailOverlay,
    active_retail_state_id: u32,
}

impl Default for Meter {
    fn default() -> Self {
        Self {
            element_id: 0,
            fill: Some(0.0),
            label: None,
            bar_color: Color::rgba(1.0, 0.0, 0.0, 1.0),
            theme_palette: None,
            bg_color: Color::rgba(0.0, 0.0, 0.0, 0.5),
            label_color: Color::WHITE,
            outline: false,
            outline_color: DEFAULT_OUTLINE_COLOR,
            font: None,
            back_left: 0,
            back_tile: 0,
            back_right: 0,
            front_left: 0,
            front_tile: 0,
            front_right: 0,
            vertical: false,
            fill_from_bottom: true,
            state_fill_sprites: Vec::new(),
            state_labels: Vec::new(),
            active_state_label: None,
            animated: None,
            animation_started: None,
            detail_configured: false,
            detail_pass_to_children: false,
            detail_back: DetailOverlay::default(),
            detail_front: DetailOverlay::default(),
            active_retail_state_id: 0,
        }
    }
}

/// The horizontal fill rectangle for share `pct` of a `w` × `h` bar.
#[must_use]
pub fn compute_fill_rect(pct: f32, w: f32, h: f32) -> (f32, f32, f32, f32) {
    (0.0, 0.0, w * pct.clamp(0.0, 1.0), h)
}

/// The vertical fill rectangle for share `pct` of a `w` × `h` bar.
#[must_use]
pub fn compute_v_fill_rect(pct: f32, w: f32, h: f32, from_bottom: bool) -> (f32, f32, f32, f32) {
    let fh = h * pct.clamp(0.0, 1.0);
    (0.0, if from_bottom { h - fh } else { 0.0 }, w, fh)
}

/// A detail icon's rectangle at the meter's current size: the authored
/// rectangle at the authored size, else laid out by its edge modes.
#[must_use]
pub fn compute_detail_overlay_rect(
    overlay: &DetailOverlay,
    meter_w: f32,
    meter_h: f32,
) -> (f32, f32, f32, f32) {
    let (pw, ph) = (meter_w as i32, meter_h as i32);
    let (opw, oph) = (overlay.parent_w as i32, overlay.parent_h as i32);
    if opw <= 0 || oph <= 0 || (pw == opw && ph == oph) {
        return (overlay.x, overlay.y, overlay.w, overlay.h);
    }
    let original = PixelRect::from_position_and_size(
        overlay.x as i32,
        overlay.y as i32,
        overlay.w as i32,
        overlay.h as i32,
    );
    let r = LayoutPolicy::apply_raw(
        (
            overlay.left_mode,
            overlay.top_mode,
            overlay.right_mode,
            overlay.bottom_mode,
        ),
        original,
        PixelRect::from_position_and_size(0, 0, opw, oph),
        original,
        PixelRect::from_position_and_size(0, 0, pw, ph),
        false,
    )
    .unwrap_or(original);
    (
        r.x0 as f32,
        r.y0 as f32,
        r.width() as f32,
        r.height() as f32,
    )
}

fn align_x(align: MeterLabelAlign, width: f32, text_width: f32) -> f32 {
    match align {
        MeterLabelAlign::Left => 0.0,
        MeterLabelAlign::Right => width - text_width,
        MeterLabelAlign::Center => (width - text_width) * 0.5,
    }
}

impl Meter {
    /// The element flags a meter starts with: it is click-through.
    pub fn configure(element: &mut Element) {
        element.click_through = true;
    }

    /// The active numeric state.
    #[must_use]
    pub fn active_retail_state_id(&self) -> u32 {
        self.active_retail_state_id
    }

    /// The caption of the active state.
    #[must_use]
    pub fn active_state_label(&self) -> Option<&str> {
        self.active_state_label.as_ref().map(|(t, _)| t.as_str())
    }

    /// Its alignment.
    #[must_use]
    pub fn active_state_label_align(&self) -> Option<MeterLabelAlign> {
        self.active_state_label.as_ref().map(|(_, a)| *a)
    }

    /// True when the meter absorbed detail icons.
    #[must_use]
    pub fn has_detail_overlay(&self) -> bool {
        self.detail_configured
    }

    /// The dim detail icon over the empty track.
    #[must_use]
    pub fn detail_back(&self) -> &DetailOverlay {
        &self.detail_back
    }

    /// The bright detail icon clipped with the fill.
    #[must_use]
    pub fn detail_front(&self) -> &DetailOverlay {
        &self.detail_front
    }

    /// Animate the back and front middles with these loops.
    pub fn configure_animated_tracks(
        &mut self,
        back: Arc<LoopingAnimation>,
        front: Arc<LoopingAnimation>,
    ) {
        self.back_tile = back.sample(0.0);
        self.front_tile = front.sample(0.0);
        self.animated = Some((back, front));
        self.animation_started = None;
    }

    /// The back and front middles `elapsed` seconds into the loops.
    #[must_use]
    pub fn sample_animated_tracks(&self, elapsed: f64) -> (u32, u32) {
        match &self.animated {
            Some((b, f)) => (b.sample(elapsed), f.sample(elapsed)),
            None => (self.back_tile, self.front_tile),
        }
    }

    /// Step the animated tracks to UI time `now` (seconds), restarting the
    /// loop after each full duration.
    pub fn advance_animated_tracks(&mut self, now: f64) {
        let Some((back, _)) = &self.animated else {
            return;
        };
        let duration = f64::from(back.duration);
        let start = match self.animation_started {
            Some(s) if now - s < duration => s,
            _ => now,
        };
        self.animation_started = Some(start);
        let (b, f) = self.sample_animated_tracks(now - start);
        self.back_tile = b;
        self.front_tile = f;
    }

    /// The fill image of state `state`.
    pub fn configure_state_fill(&mut self, state: u32, sprite: u32) {
        if state != 0 && sprite != 0 {
            self.state_fill_sprites.retain(|(s, _)| *s != state);
            self.state_fill_sprites.push((state, sprite));
        }
    }

    /// The caption of state `state`.
    pub fn configure_state_label(&mut self, state: u32, text: &str, align: MeterLabelAlign) {
        if state != 0 && !text.is_empty() {
            self.state_labels.retain(|(s, _, _)| *s != state);
            self.state_labels.push((state, text.to_owned(), align));
        }
    }

    /// The detail icons and whether the detail states pass to children.
    pub fn configure_detail_overlay(
        &mut self,
        back: DetailOverlay,
        front: DetailOverlay,
        pass_to_children: bool,
    ) {
        self.detail_configured = back.sprite != 0 || front.sprite != 0;
        self.detail_back = back;
        self.detail_front = front;
        self.detail_pass_to_children = pass_to_children;
    }

    fn draw_piece(
        rc: &mut RenderContext<'_>,
        id: u32,
        piece_x: f32,
        piece_w: f32,
        native_w: f32,
        h: f32,
        clip_w: f32,
    ) {
        if id == 0 || piece_w <= 0.0 || native_w <= 0.0 {
            return;
        }
        let visible = piece_w.min(clip_w - piece_x);
        if visible <= 0.0 {
            return;
        }
        rc.sprite(
            TextureRef::Surface(id),
            piece_x,
            0.0,
            visible,
            h,
            (0.0, 0.0, visible / native_w, 1.0),
            Color::WHITE,
        );
    }

    #[allow(clippy::too_many_arguments)]
    fn draw_h_bar(
        rc: &mut RenderContext<'_>,
        w: f32,
        h: f32,
        left: u32,
        mid: u32,
        right: u32,
        clip_w: f32,
    ) {
        if clip_w <= 0.0 {
            return;
        }
        let size =
            |id: u32, rc: &RenderContext<'_>| if id != 0 { rc.sprite_size(id) } else { None };
        let (lw, mw, rw) = (
            size(left, rc).map(|s| s.0),
            size(mid, rc).map(|s| s.0),
            size(right, rc).map(|s| s.0),
        );
        let cap_l = lw.map_or(0.0, |lw| lw.min(w));
        let cap_r = rw.map_or(0.0, |rw| rw.min(w - cap_l));
        let mid_w = w - cap_l - cap_r;
        if let Some(lw) = lw {
            Self::draw_piece(rc, left, 0.0, cap_l, lw, h, clip_w);
        }
        if let Some(mw) = mw {
            Self::draw_piece(rc, mid, cap_l, mid_w, mw, h, clip_w);
        }
        if let Some(rw) = rw {
            Self::draw_piece(rc, right, w - cap_r, cap_r, rw, h, clip_w);
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn draw_v_bar(
        rc: &mut RenderContext<'_>,
        w: f32,
        h: f32,
        tile: u32,
        visible_h: f32,
        from_bottom: bool,
        is_fill: bool,
    ) {
        if tile == 0 || visible_h <= 0.0 || rc.sprite_size(tile).is_none() {
            return;
        }
        let visible_h = visible_h.min(h);
        let frac = if h > 0.0 { visible_h / h } else { 0.0 };
        let bottom_up = is_fill && from_bottom;
        let y = if bottom_up { h - visible_h } else { 0.0 };
        let (v0, v1) = if bottom_up {
            (1.0 - frac, 1.0)
        } else {
            (0.0, frac)
        };
        rc.sprite(
            TextureRef::Surface(tile),
            0.0,
            y,
            w,
            visible_h,
            (0.0, v0, 1.0, v1),
            Color::WHITE,
        );
    }

    fn draw_detail_icon(
        rc: &mut RenderContext<'_>,
        sprite: u32,
        rect: (f32, f32, f32, f32),
        meter_h: f32,
        clip_w: f32,
    ) {
        let (x, y, w, h) = rect;
        if sprite == 0 || w <= 0.0 || h <= 0.0 || rc.sprite_size(sprite).is_none() {
            return;
        }
        let visible = w.min(clip_w - x);
        if visible <= 0.0 {
            return;
        }
        let dh = h.min(meter_h - y);
        if dh <= 0.0 {
            return;
        }
        rc.sprite(
            TextureRef::Surface(sprite),
            x,
            y,
            visible,
            dh,
            (0.0, 0.0, visible / w, 1.0),
            Color::WHITE,
        );
    }
}

impl Widget for Meter {
    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        self.advance_animated_tracks(rc.media_seconds());
        let p = self.fill.map_or(0.0, |f| f.clamp(0.0, 1.0));
        let has_fill = self.fill.is_some() && p > 0.0;

        if self.back_left != 0 || self.back_tile != 0 || self.front_tile != 0 {
            if self.vertical {
                Self::draw_v_bar(rc, w, h, self.back_tile, h, self.fill_from_bottom, false);
                if has_fill {
                    Self::draw_v_bar(
                        rc,
                        w,
                        h,
                        self.front_tile,
                        h * p,
                        self.fill_from_bottom,
                        true,
                    );
                }
            } else {
                let detail =
                    self.detail_configured && self.active_retail_state_id == states::SHOW_DETAIL;
                Self::draw_h_bar(rc, w, h, self.back_left, self.back_tile, self.back_right, w);
                if detail {
                    Self::draw_detail_icon(
                        rc,
                        self.detail_back.sprite,
                        compute_detail_overlay_rect(&self.detail_back, w, h),
                        h,
                        w,
                    );
                }
                if has_fill {
                    Self::draw_h_bar(
                        rc,
                        w,
                        h,
                        self.front_left,
                        self.front_tile,
                        self.front_right,
                        w * p,
                    );
                    if detail {
                        Self::draw_detail_icon(
                            rc,
                            self.detail_front.sprite,
                            compute_detail_overlay_rect(&self.detail_front, w, h),
                            h,
                            w * p,
                        );
                    }
                }
            }
        } else if let Some(palette) = &self.theme_palette {
            let fill = if has_fill {
                compute_fill_rect(p, w, h)
            } else {
                (0.0, 0.0, 0.0, 0.0)
            };
            style::meter(rc, palette, w, h, fill, self.bar_color);
        } else {
            rc.fill(0.0, 0.0, w, h, self.bg_color);
            if has_fill {
                let (fx, fy, fw, fh) = compute_fill_rect(p, w, h);
                if fw > 0.0 {
                    rc.fill(fx, fy, fw, fh, self.bar_color);
                }
            }
        }

        let (label, align) = match self.label.as_deref().filter(|l| !l.is_empty()) {
            Some(l) => (Some(l.to_owned()), MeterLabelAlign::Center),
            None => match &self.active_state_label {
                Some((t, a)) => (Some(t.clone()), *a),
                None => (None, MeterLabelAlign::Center),
            },
        };
        if let (Some(label), Some(font)) = (label.filter(|l| !l.is_empty()), self.font.clone()) {
            let tx = align_x(align, w, font.measure_width(&label));
            let ty = (h - font.line_height) * 0.5;
            rc.string_dat(
                &font,
                &label,
                tx,
                ty,
                self.label_color,
                self.outline,
                self.outline_color,
            );
        }
    }

    fn consumes_dat_children(&self) -> bool {
        true
    }

    fn child_import(&self) -> ChildImport {
        // The slice containers are drawn by the meter; its captions and
        // other leaves are real children.
        ChildImport::NonContainers
    }

    fn retail_state(&self) -> Option<u32> {
        Some(self.active_retail_state_id)
    }

    fn set_retail_state(&mut self, ui: &mut UiRoot, me: ElementId, state: u32) -> bool {
        if self.detail_configured && (state == states::HIDE_DETAIL || state == states::SHOW_DETAIL)
        {
            self.active_retail_state_id = state;
            if self.detail_pass_to_children {
                for child in ui.children(me) {
                    ui.set_retail_state(child, state);
                }
            }
            return true;
        }
        let fill = self
            .state_fill_sprites
            .iter()
            .find(|(s, _)| *s == state)
            .map(|(_, f)| *f);
        let label = self
            .state_labels
            .iter()
            .find(|(s, _, _)| *s == state)
            .map(|(_, t, a)| (t.clone(), *a));
        if fill.is_none() && label.is_none() {
            return false;
        }
        self.active_retail_state_id = state;
        if let Some(sprite) = fill {
            self.front_left = 0;
            self.front_tile = sprite;
            self.front_right = 0;
        }
        self.active_state_label = label;
        true
    }
}
