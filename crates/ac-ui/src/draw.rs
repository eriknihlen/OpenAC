//! The draw list the UI produces each frame, and the context widgets draw
//! through (origin, clip and opacity stacks).
//!
//! The UI never touches a GPU. A frame is a [`DrawList`]: fills, textured
//! quads, convex polygons, coloured triangles and glyph runs in screen
//! pixels, already clipped, in the order they must be painted. Textures are named, not uploaded: a
//! [`TextureRef::Surface`] is a dat render surface id the renderer decodes
//! and caches.

use std::sync::Arc;

use crate::font::{SharpFont, SharpFonts, UiFont};
use crate::shapes::{self, CornerRadii, ShapeOutcome};

/// A colour, straight (not premultiplied) alpha, components 0..=1.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Color {
    /// Red.
    pub r: f32,
    /// Green.
    pub g: f32,
    /// Blue.
    pub b: f32,
    /// Alpha.
    pub a: f32,
}

impl Color {
    /// Opaque white (no tint).
    pub const WHITE: Self = Self::rgba(1.0, 1.0, 1.0, 1.0);
    /// Opaque black.
    pub const BLACK: Self = Self::rgba(0.0, 0.0, 0.0, 1.0);
    /// Fully transparent.
    pub const TRANSPARENT: Self = Self::rgba(0.0, 0.0, 0.0, 0.0);

    /// A colour from its components.
    #[must_use]
    pub const fn rgba(r: f32, g: f32, b: f32, a: f32) -> Self {
        Self { r, g, b, a }
    }

    /// A dat colour; a stored alpha of 0 means opaque, as the client
    /// treats authored font colours.
    #[must_use]
    pub fn from_dat(c: ac_dat::types::ColorArgb) -> Self {
        let a = if c.alpha == 0 {
            1.0
        } else {
            f32::from(c.alpha) / 255.0
        };
        Self::rgba(
            f32::from(c.red) / 255.0,
            f32::from(c.green) / 255.0,
            f32::from(c.blue) / 255.0,
            a,
        )
    }

    /// The colour `t` of the way from `self` to `to`.
    #[must_use]
    pub fn lerp(self, to: Self, t: f32) -> Self {
        let mix = |a: f32, b: f32| a + (b - a) * t;
        Self::rgba(
            mix(self.r, to.r),
            mix(self.g, to.g),
            mix(self.b, to.b),
            mix(self.a, to.a),
        )
    }
}

/// The colour outlines use when none is authored.
pub const DEFAULT_OUTLINE_COLOR: Color = Color::BLACK;

/// A texture named by the draw list.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum TextureRef {
    /// A dat render surface (`0x06......`), decoded by the renderer.
    Surface(u32),
    /// A texture the presentation layer made itself (composited icons, 3-D
    /// viewports), by its own handle.
    Runtime(u32),
}

/// How a texture is sampled.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum Filter {
    /// Bilinear (UI art).
    #[default]
    Linear,
    /// Nearest texel (font glyphs).
    Nearest,
}

/// A textured rectangle: screen position and size, texture coordinates.
/// Coordinates past 1 repeat the texture (tiled UI art).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Quad {
    /// Left.
    pub x: f32,
    /// Top.
    pub y: f32,
    /// Width.
    pub w: f32,
    /// Height.
    pub h: f32,
    /// Texture u at the left edge.
    pub u0: f32,
    /// Texture v at the top edge.
    pub v0: f32,
    /// Texture u at the right edge.
    pub u1: f32,
    /// Texture v at the bottom edge.
    pub v1: f32,
}

/// A polygon corner: screen position and texture coordinate.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Vertex {
    /// X.
    pub x: f32,
    /// Y.
    pub y: f32,
    /// Texture u.
    pub u: f32,
    /// Texture v.
    pub v: f32,
}

impl Vertex {
    /// A corner.
    #[must_use]
    pub const fn new(x: f32, y: f32, u: f32, v: f32) -> Self {
        Self { x, y, u, v }
    }
}

/// A corner of an untextured triangle: screen position and its own colour,
/// blended across the triangle.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ColorVertex {
    /// X.
    pub x: f32,
    /// Y.
    pub y: f32,
    /// Colour (straight alpha).
    pub color: Color,
}

impl ColorVertex {
    /// A corner.
    #[must_use]
    pub const fn new(x: f32, y: f32, color: Color) -> Self {
        Self { x, y, color }
    }

    /// True when every number in it is finite.
    #[must_use]
    pub fn is_finite(&self) -> bool {
        let c = self.color;
        [self.x, self.y, c.r, c.g, c.b, c.a]
            .iter()
            .all(|n| n.is_finite())
    }
}

/// One drawing command, in screen pixels, already clipped.
#[derive(Debug, Clone, PartialEq)]
pub enum DrawCmd {
    /// A solid rectangle.
    Fill {
        /// Left.
        x: f32,
        /// Top.
        y: f32,
        /// Width.
        w: f32,
        /// Height.
        h: f32,
        /// Colour (opacity applied).
        color: Color,
    },
    /// A textured rectangle.
    Sprite {
        /// Texture.
        texture: TextureRef,
        /// Sampling.
        filter: Filter,
        /// Geometry and texture coordinates.
        quad: Quad,
        /// Multiplied into the texels (opacity applied).
        tint: Color,
    },
    /// A convex polygon (a thick line or a turned blit), drawn as a fan
    /// from the first corner.
    Polygon {
        /// Texture, or none for a solid fill.
        texture: Option<TextureRef>,
        /// Corners in order, 3 to 8.
        vertices: Vec<Vertex>,
        /// Colour or tint (opacity applied).
        color: Color,
    },
    /// Untextured triangles, three corners each, with a colour at every
    /// corner (gradients, soft edges).
    Triangles {
        /// The corners (opacity applied).
        vertices: Vec<ColorVertex>,
    },
    /// Glyphs of one pass of one string: same atlas, same tint, sampled
    /// nearest.
    Glyphs {
        /// The atlas.
        texture: TextureRef,
        /// Tint (opacity applied).
        tint: Color,
        /// One quad per glyph.
        quads: Vec<Quad>,
    },
}

/// One frame of UI drawing.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct DrawList {
    /// Window contents, painted first.
    pub main: Vec<DrawCmd>,
    /// Popups, tooltips and the drag ghost, painted over everything.
    pub overlay: Vec<DrawCmd>,
    /// Multiply every position by this before drawing (a fixed-canvas
    /// screen authored at another size); (1, 1) otherwise.
    pub canvas_scale: (f32, f32),
}

impl DrawList {
    /// Every command, main layer first.
    pub fn commands(&self) -> impl Iterator<Item = &DrawCmd> {
        self.main.iter().chain(self.overlay.iter())
    }
}

/// An axis-aligned clip rectangle in screen pixels.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ClipRect {
    /// Left.
    pub left: f32,
    /// Top.
    pub top: f32,
    /// Right (exclusive).
    pub right: f32,
    /// Bottom (exclusive).
    pub bottom: f32,
}

impl ClipRect {
    /// A clip rectangle from its edges.
    #[must_use]
    pub const fn new(left: f32, top: f32, right: f32, bottom: f32) -> Self {
        Self {
            left,
            top,
            right,
            bottom,
        }
    }

    /// True when nothing can be inside.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.right <= self.left || self.bottom <= self.top
    }

    /// The overlap of two clips.
    #[must_use]
    pub fn intersect(a: Self, b: Self) -> Self {
        Self::new(
            a.left.max(b.left),
            a.top.max(b.top),
            a.right.min(b.right),
            a.bottom.min(b.bottom),
        )
    }

    /// Crop a quad and its texture coordinates to the clip; `None` when
    /// nothing is left.
    #[must_use]
    pub fn clip_quad(&self, q: Quad) -> Option<Quad> {
        if self.is_empty() || q.w <= 0.0 || q.h <= 0.0 {
            return None;
        }
        let left = q.x.max(self.left);
        let top = q.y.max(self.top);
        let right = (q.x + q.w).min(self.right);
        let bottom = (q.y + q.h).min(self.bottom);
        if right <= left || bottom <= top {
            return None;
        }
        let tx0 = (left - q.x) / q.w;
        let tx1 = (right - q.x) / q.w;
        let ty0 = (top - q.y) / q.h;
        let ty1 = (bottom - q.y) / q.h;
        Some(Quad {
            x: left,
            y: top,
            w: right - left,
            h: bottom - top,
            u0: q.u0 + (q.u1 - q.u0) * tx0,
            u1: q.u0 + (q.u1 - q.u0) * tx1,
            v0: q.v0 + (q.v1 - q.v0) * ty0,
            v1: q.v0 + (q.v1 - q.v0) * ty1,
        })
    }

    /// Cut a convex polygon (at most 4 corners) to the clip, edge by edge;
    /// the result has 0 or 3..=8 corners.
    #[must_use]
    pub fn clip_polygon(&self, polygon: &[Vertex]) -> Vec<Vertex> {
        if polygon.len() < 3 || polygon.len() > 4 || self.is_empty() {
            return Vec::new();
        }
        let mut current = polygon.to_vec();
        for edge in [Edge::Left, Edge::Top, Edge::Right, Edge::Bottom] {
            current = clip_against(edge, self.boundary(edge), &current);
            if current.is_empty() {
                return current;
            }
        }
        current
    }

    /// Append what is left of a triangle inside the clip to `out`, as
    /// triangles: a corner made by a cut has the colour blended to match
    /// where the cut fell, so a gradient or a soft edge keeps its look.
    /// Most triangles are wholly inside or outside and need no cutting.
    pub fn clip_triangle(&self, triangle: [ColorVertex; 3], out: &mut Vec<ColorVertex>) {
        if self.is_empty() {
            return;
        }
        let [a, b, c] = triangle;
        if (a.x < self.left && b.x < self.left && c.x < self.left)
            || (a.x > self.right && b.x > self.right && c.x > self.right)
            || (a.y < self.top && b.y < self.top && c.y < self.top)
            || (a.y > self.bottom && b.y > self.bottom && c.y > self.bottom)
        {
            return;
        }
        let inside = |v: &ColorVertex| {
            v.x >= self.left && v.x <= self.right && v.y >= self.top && v.y <= self.bottom
        };
        if triangle.iter().all(inside) {
            out.extend_from_slice(&triangle);
            return;
        }
        let mut current = triangle.to_vec();
        for edge in [Edge::Left, Edge::Top, Edge::Right, Edge::Bottom] {
            current = clip_against(edge, self.boundary(edge), &current);
            if current.is_empty() {
                return;
            }
        }
        for i in 1..current.len().saturating_sub(1) {
            out.extend_from_slice(&[current[0], current[i], current[i + 1]]);
        }
    }

    fn boundary(&self, edge: Edge) -> f32 {
        match edge {
            Edge::Left => self.left,
            Edge::Top => self.top,
            Edge::Right => self.right,
            Edge::Bottom => self.bottom,
        }
    }
}

#[derive(Clone, Copy)]
enum Edge {
    Left,
    Top,
    Right,
    Bottom,
}

/// A corner a clip edge can cut between: where it is, and what lies a share
/// of the way to another.
trait ClipCorner: Copy {
    fn position(&self) -> (f32, f32);
    fn toward(self, to: Self, t: f32) -> Self;
}

impl ClipCorner for Vertex {
    fn position(&self) -> (f32, f32) {
        (self.x, self.y)
    }

    fn toward(self, to: Self, t: f32) -> Self {
        let lerp = |a: f32, b: f32| a + (b - a) * t;
        Vertex::new(
            lerp(self.x, to.x),
            lerp(self.y, to.y),
            lerp(self.u, to.u),
            lerp(self.v, to.v),
        )
    }
}

impl ClipCorner for ColorVertex {
    fn position(&self) -> (f32, f32) {
        (self.x, self.y)
    }

    fn toward(self, to: Self, t: f32) -> Self {
        let lerp = |a: f32, b: f32| a + (b - a) * t;
        ColorVertex::new(
            lerp(self.x, to.x),
            lerp(self.y, to.y),
            self.color.lerp(to.color, t),
        )
    }
}

fn signed_distance<V: ClipCorner>(edge: Edge, boundary: f32, v: &V) -> f32 {
    let (x, y) = v.position();
    match edge {
        Edge::Left => x - boundary,
        Edge::Top => y - boundary,
        Edge::Right => boundary - x,
        Edge::Bottom => boundary - y,
    }
}

fn clip_against<V: ClipCorner>(edge: Edge, boundary: f32, input: &[V]) -> Vec<V> {
    let n = input.len();
    let mut out = Vec::with_capacity(n + 1);
    for i in 0..n {
        let current = input[i];
        let previous = input[(i + n - 1) % n];
        let cd = signed_distance(edge, boundary, &current);
        let pd = signed_distance(edge, boundary, &previous);
        let current_inside = cd >= 0.0;
        let previous_inside = pd >= 0.0;
        if current_inside {
            if !previous_inside {
                out.push(crossing(previous, current, pd, cd));
            }
            out.push(current);
        } else if previous_inside {
            out.push(crossing(previous, current, pd, cd));
        }
    }
    out
}

fn crossing<V: ClipCorner>(from: V, to: V, from_d: f32, to_d: f32) -> V {
    let span = from_d - to_d;
    // The ends straddle the edge, so the span is non-zero except for the
    // degenerate case of both distances exactly zero.
    let t = if span.abs() > f32::from_bits(1) {
        from_d / span
    } else {
        0.0
    };
    from.toward(to, t)
}

/// Widgets draw through this: it keeps the origin, clip and opacity of the
/// element being drawn and appends commands to the frame's list.
#[derive(Debug)]
pub struct RenderContext<'a> {
    list: &'a mut DrawList,
    overlay: bool,
    screen: (f32, f32),
    origin: (f32, f32),
    origins: Vec<(f32, f32)>,
    clip: Option<ClipRect>,
    clips: Vec<Option<ClipRect>>,
    alpha: f32,
    alphas: Vec<f32>,
    sprite_sizes: &'a dyn SpriteSizes,
    media_seconds: f64,
    device_pixel: f32,
    sharp_fonts: Option<Arc<SharpFonts>>,
}

/// Pixel sizes of render surfaces, so widgets can tile UI art. `None` for an
/// unknown surface (nothing is drawn for it).
pub trait SpriteSizes: std::fmt::Debug {
    /// Width and height of render surface `file`.
    fn sprite_size(&self, file: u32) -> Option<(u32, u32)>;
}

/// No sprite sizes: every sprite is unknown.
#[derive(Debug, Clone, Copy, Default)]
pub struct NoSprites;

impl SpriteSizes for NoSprites {
    fn sprite_size(&self, _file: u32) -> Option<(u32, u32)> {
        None
    }
}

/// `value` on a grid of `pixels` steps a unit, half up.
fn snap_to_grid(value: f32, pixels: f32) -> f32 {
    (value * pixels + 0.5).floor() / pixels
}

impl<'a> RenderContext<'a> {
    /// A context drawing into `list` for a screen of the given size.
    pub fn new(
        list: &'a mut DrawList,
        screen: (f32, f32),
        sprite_sizes: &'a dyn SpriteSizes,
    ) -> Self {
        Self {
            list,
            overlay: false,
            screen,
            origin: (0.0, 0.0),
            origins: Vec::new(),
            clip: None,
            clips: Vec::new(),
            alpha: 1.0,
            alphas: Vec::new(),
            sprite_sizes,
            media_seconds: 0.0,
            device_pixel: 1.0,
            sharp_fonts: None,
        }
    }

    /// With how many framebuffer pixels one unit of this context covers, for
    /// the width of a shape's soft edge. Anything but a positive number
    /// counts as one.
    #[must_use]
    pub fn with_pixels_per_unit(mut self, pixels: f32) -> Self {
        self.device_pixel = if pixels.is_finite() && pixels > 0.0 {
            1.0 / pixels
        } else {
            1.0
        };
        self
    }

    /// With the sharper font bakes to draw from where this context has more
    /// than one framebuffer pixel a unit.
    #[must_use]
    pub fn with_sharp_fonts(mut self, fonts: Option<Arc<SharpFonts>>) -> Self {
        self.sharp_fonts = fonts;
        self
    }

    /// With the UI media clock (seconds) animated media is sampled at.
    #[must_use]
    pub fn with_media_seconds(mut self, seconds: f64) -> Self {
        self.media_seconds = seconds;
        self
    }

    /// The UI media clock, seconds.
    #[must_use]
    pub fn media_seconds(&self) -> f64 {
        self.media_seconds
    }

    /// Screen size in pixels.
    #[must_use]
    pub fn screen_size(&self) -> (f32, f32) {
        self.screen
    }

    /// The size of render surface `file`; `None` for file 0 or an unknown
    /// surface. Callers guard on the id, never on a resolved texture.
    #[must_use]
    pub fn sprite_size(&self, file: u32) -> Option<(f32, f32)> {
        if file == 0 {
            return None;
        }
        self.sprite_sizes
            .sprite_size(file)
            .filter(|&(w, h)| w != 0 && h != 0)
            .map(|(w, h)| (w as f32, h as f32))
    }

    /// Current opacity multiplier.
    #[must_use]
    pub fn alpha(&self) -> f32 {
        self.alpha
    }

    /// Multiply the opacity by `a` until the matching pop.
    pub fn push_alpha(&mut self, a: f32) {
        self.alphas.push(self.alpha);
        self.alpha *= a;
    }

    /// Set the opacity to `a` until the matching pop.
    pub fn push_alpha_absolute(&mut self, a: f32) {
        self.alphas.push(self.alpha);
        self.alpha = a;
    }

    /// Undo the last opacity push (no-op without one).
    pub fn pop_alpha(&mut self) {
        if let Some(a) = self.alphas.pop() {
            self.alpha = a;
        }
    }

    /// Move the origin by (`dx`, `dy`) until the matching pop.
    pub fn push_transform(&mut self, dx: f32, dy: f32) {
        self.origins.push(self.origin);
        self.origin = (self.origin.0 + dx, self.origin.1 + dy);
    }

    /// Undo the last origin push (no-op without one).
    pub fn pop_transform(&mut self) {
        if let Some(o) = self.origins.pop() {
            self.origin = o;
        }
    }

    /// The current origin in screen pixels.
    #[must_use]
    pub fn origin(&self) -> (f32, f32) {
        self.origin
    }

    /// Intersect drawing with a local rectangle until the matching pop.
    pub fn push_clip(&mut self, x: f32, y: f32, w: f32, h: f32) {
        self.clips.push(self.clip);
        let next = ClipRect::new(
            self.origin.0 + x,
            self.origin.1 + y,
            self.origin.0 + x + w.max(0.0),
            self.origin.1 + y + h.max(0.0),
        );
        self.clip = Some(match self.clip {
            Some(current) => ClipRect::intersect(current, next),
            None => next,
        });
    }

    /// Clip to the whole screen, whatever the ancestors clip, until the
    /// matching pop (popups that escape their window).
    pub fn push_clip_unbounded(&mut self) {
        self.clips.push(self.clip);
        self.clip = Some(ClipRect::new(0.0, 0.0, self.screen.0, self.screen.1));
    }

    /// Undo the last clip push (no-op without one).
    pub fn pop_clip(&mut self) {
        if let Some(c) = self.clips.pop() {
            self.clip = c;
        }
    }

    /// True when the current clip leaves nothing visible.
    #[must_use]
    pub fn current_clip_is_empty(&self) -> bool {
        self.clip.is_some_and(|c| c.is_empty())
    }

    /// Stack depths (clip, origin, opacity), for balance checks.
    #[must_use]
    pub fn depths(&self) -> (usize, usize, usize) {
        (self.clips.len(), self.origins.len(), self.alphas.len())
    }

    /// Draw into the overlay layer from now on.
    pub fn begin_overlay(&mut self) {
        self.overlay = true;
    }

    /// Draw into the main layer again.
    pub fn end_overlay(&mut self) {
        self.overlay = false;
    }

    fn emit(&mut self, cmd: DrawCmd) {
        if self.overlay {
            self.list.overlay.push(cmd);
        } else {
            self.list.main.push(cmd);
        }
    }

    fn apply_alpha(&self, c: Color) -> Color {
        if self.alpha >= 1.0 {
            c
        } else {
            Color {
                a: c.a * self.alpha,
                ..c
            }
        }
    }

    /// A solid local rectangle.
    pub fn fill(&mut self, x: f32, y: f32, w: f32, h: f32, color: Color) {
        let q = Quad {
            x: self.origin.0 + x,
            y: self.origin.1 + y,
            w,
            h,
            ..Quad::default()
        };
        let q = match self.clip {
            Some(clip) => match clip.clip_quad(q) {
                Some(q) => q,
                None => return,
            },
            None if w > 0.0 && h > 0.0 => q,
            None => return,
        };
        let color = self.apply_alpha(color);
        self.emit(DrawCmd::Fill {
            x: q.x,
            y: q.y,
            w: q.w,
            h: q.h,
            color,
        });
    }

    /// A rectangle outline `thickness` pixels wide, inside the rectangle.
    pub fn rect_outline(&mut self, x: f32, y: f32, w: f32, h: f32, color: Color, thickness: f32) {
        if thickness <= 0.0 || w <= 0.0 || h <= 0.0 {
            return;
        }
        let t = thickness.min(w.min(h) * 0.5);
        self.fill(x, y, w, t, color);
        self.fill(x, y + h - t, w, t, color);
        self.fill(x, y + t, t, h - 2.0 * t, color);
        self.fill(x + w - t, y + t, t, h - 2.0 * t, color);
    }

    /// A textured local rectangle (texture coordinates may repeat).
    #[allow(clippy::too_many_arguments)]
    pub fn sprite(
        &mut self,
        texture: TextureRef,
        x: f32,
        y: f32,
        w: f32,
        h: f32,
        uv: (f32, f32, f32, f32),
        tint: Color,
    ) {
        let q = Quad {
            x: self.origin.0 + x,
            y: self.origin.1 + y,
            w,
            h,
            u0: uv.0,
            v0: uv.1,
            u1: uv.2,
            v1: uv.3,
        };
        self.sprite_absolute(texture, Filter::Linear, q, tint);
    }

    /// Render surface `file` stretched (or tiled) over a local rectangle:
    /// one texel per pixel, repeating. Nothing for file 0 or an unknown
    /// surface.
    pub fn surface(&mut self, file: u32, x: f32, y: f32, w: f32, h: f32, tint: Color) {
        let Some((tw, th)) = self.sprite_size(file) else {
            return;
        };
        self.sprite(
            TextureRef::Surface(file),
            x,
            y,
            w,
            h,
            (0.0, 0.0, w / tw, h / th),
            tint,
        );
    }

    fn sprite_absolute(&mut self, texture: TextureRef, filter: Filter, q: Quad, tint: Color) {
        let q = match self.clip {
            Some(clip) => match clip.clip_quad(q) {
                Some(q) => q,
                None => return,
            },
            None => q,
        };
        let tint = self.apply_alpha(tint);
        self.emit(DrawCmd::Sprite {
            texture,
            filter,
            quad: q,
            tint,
        });
    }

    /// A straight segment `thickness` pixels wide centred on the line from
    /// (`x0`, `y0`) to (`x1`, `y1`); square ends, no caps.
    pub fn line(&mut self, x0: f32, y0: f32, x1: f32, y1: f32, color: Color, thickness: f32) {
        if thickness <= 0.0 {
            return;
        }
        let (dx, dy) = (x1 - x0, y1 - y0);
        let length = (dx * dx + dy * dy).sqrt();
        if length <= 0.0 || length.is_nan() {
            return;
        }
        let half = thickness * 0.5;
        let nx = -dy / length * half;
        let ny = dx / length * half;
        let (ax, ay) = (self.origin.0 + x0, self.origin.1 + y0);
        let (bx, by) = (self.origin.0 + x1, self.origin.1 + y1);
        let quad = [
            Vertex::new(ax + nx, ay + ny, 0.0, 0.0),
            Vertex::new(bx + nx, by + ny, 0.0, 0.0),
            Vertex::new(bx - nx, by - ny, 0.0, 0.0),
            Vertex::new(ax - nx, ay - ny, 0.0, 0.0),
        ];
        let color = self.apply_alpha(color);
        self.emit_convex(None, &quad, color);
    }

    /// A sub-image blitted into a local rectangle after scaling and turning
    /// that rectangle about `pivot` (pixels from the rectangle's top-left).
    /// Rotation is in radians, clockwise on screen (y points down).
    #[allow(clippy::too_many_arguments)]
    pub fn sprite_transformed(
        &mut self,
        texture: TextureRef,
        x: f32,
        y: f32,
        w: f32,
        h: f32,
        uv: (f32, f32, f32, f32),
        tint: Color,
        rotation: f32,
        scale: (f32, f32),
        pivot: (f32, f32),
    ) {
        if w == 0.0 || h == 0.0 || scale.0 == 0.0 || scale.1 == 0.0 {
            return;
        }
        let (cos, sin) = (rotation.cos(), rotation.sin());
        let px = self.origin.0 + x + pivot.0;
        let py = self.origin.1 + y + pivot.1;
        let left = self.origin.0 + x;
        let top = self.origin.1 + y;
        let corner = |cx: f32, cy: f32, u: f32, v: f32| {
            let ox = (cx - px) * scale.0;
            let oy = (cy - py) * scale.1;
            Vertex::new(px + ox * cos - oy * sin, py + ox * sin + oy * cos, u, v)
        };
        let quad = [
            corner(left, top, uv.0, uv.1),
            corner(left + w, top, uv.2, uv.1),
            corner(left + w, top + h, uv.2, uv.3),
            corner(left, top + h, uv.0, uv.3),
        ];
        let tint = self.apply_alpha(tint);
        self.emit_convex(Some(texture), &quad, tint);
    }

    fn emit_convex(&mut self, texture: Option<TextureRef>, quad: &[Vertex], color: Color) {
        let vertices = match self.clip {
            None => quad.to_vec(),
            Some(clip) => {
                if clip.is_empty() {
                    return;
                }
                let clipped = clip.clip_polygon(quad);
                if clipped.len() < 3 {
                    return;
                }
                clipped
            }
        };
        self.emit(DrawCmd::Polygon {
            texture,
            vertices,
            color,
        });
    }

    /// Local triangles, three corners each with its own colour, following
    /// the origin, the opacity and the clip like every other primitive. A
    /// triangle with anything but finite numbers in it is dropped, so what
    /// reaches the GPU is always something it can draw.
    pub fn triangles(&mut self, triangles: &[ColorVertex]) {
        if self.current_clip_is_empty() {
            return;
        }
        let mut vertices = Vec::with_capacity(triangles.len());
        for t in triangles.chunks_exact(3) {
            let placed = [self.place(t[0]), self.place(t[1]), self.place(t[2])];
            if !placed.iter().all(ColorVertex::is_finite) {
                continue;
            }
            match self.clip {
                Some(clip) => clip.clip_triangle(placed, &mut vertices),
                None => vertices.extend_from_slice(&placed),
            }
        }
        if !vertices.is_empty() {
            self.emit(DrawCmd::Triangles { vertices });
        }
    }

    fn place(&self, v: ColorVertex) -> ColorVertex {
        ColorVertex::new(
            self.origin.0 + v.x,
            self.origin.1 + v.y,
            self.apply_alpha(v.color),
        )
    }

    /// A rectangle with rounded corners and a smooth edge; zero radii give
    /// square corners.
    pub fn fill_rounded_rect(
        &mut self,
        x: f32,
        y: f32,
        w: f32,
        h: f32,
        radii: CornerRadii,
        color: Color,
    ) {
        let mut shape = Vec::new();
        shapes::fill_rounded_rect((x, y, w, h), radii, color, self.device_pixel, &mut shape);
        self.triangles(&shape);
    }

    /// A rounded rectangle's outline `thickness` wide, centred on the
    /// outline.
    #[allow(clippy::too_many_arguments)]
    pub fn stroke_rounded_rect(
        &mut self,
        x: f32,
        y: f32,
        w: f32,
        h: f32,
        radii: CornerRadii,
        color: Color,
        thickness: f32,
    ) {
        let mut shape = Vec::new();
        shapes::stroke_rounded_rect(
            (x, y, w, h),
            radii,
            color,
            thickness,
            self.device_pixel,
            &mut shape,
        );
        self.triangles(&shape);
    }

    /// An ellipse filling a local rectangle.
    pub fn fill_ellipse(&mut self, x: f32, y: f32, w: f32, h: f32, color: Color) {
        let mut shape = Vec::new();
        shapes::fill_ellipse((x, y, w, h), color, self.device_pixel, &mut shape);
        self.triangles(&shape);
    }

    /// The outline, `thickness` wide, of an ellipse filling a local
    /// rectangle.
    pub fn stroke_ellipse(&mut self, x: f32, y: f32, w: f32, h: f32, color: Color, thickness: f32) {
        let mut shape = Vec::new();
        shapes::stroke_ellipse(
            (x, y, w, h),
            color,
            thickness,
            self.device_pixel,
            &mut shape,
        );
        self.triangles(&shape);
    }

    /// A convex polygon with a colour at each corner and a smooth edge; what
    /// it came to says why nothing was drawn.
    pub fn fill_convex_polygon(&mut self, corners: &[ColorVertex]) -> ShapeOutcome {
        let mut shape = Vec::new();
        let outcome = shapes::fill_convex_polygon(corners, self.device_pixel, &mut shape);
        self.triangles(&shape);
        outcome
    }

    /// A straight segment `thickness` wide with smooth sides and square,
    /// flush ends.
    pub fn smooth_line(
        &mut self,
        x0: f32,
        y0: f32,
        x1: f32,
        y1: f32,
        color: Color,
        thickness: f32,
    ) {
        let (dx, dy) = (x1 - x0, y1 - y0);
        let length = (dx * dx + dy * dy).sqrt();
        if length.is_nan() || length <= 0.0 || thickness.is_nan() || thickness <= 0.0 {
            return;
        }
        let nx = -dy / length * thickness * 0.5;
        let ny = dx / length * thickness * 0.5;
        self.fill_convex_polygon(&[
            ColorVertex::new(x0 + nx, y0 + ny, color),
            ColorVertex::new(x1 + nx, y1 + ny, color),
            ColorVertex::new(x1 - nx, y1 - ny, color),
            ColorVertex::new(x0 - nx, y0 - ny, color),
        ]);
    }

    /// A rounded rectangle whose colour runs from `top` to `bottom`. Each
    /// corner of the shape takes the colour at its height; the soft edge
    /// keeps its own transparency.
    #[allow(clippy::too_many_arguments)]
    pub fn fill_vertical_gradient(
        &mut self,
        x: f32,
        y: f32,
        w: f32,
        h: f32,
        radii: CornerRadii,
        top: Color,
        bottom: Color,
    ) {
        self.fill_gradient((x, y, w, h), radii, top, bottom, |v| (v.y - y) / h);
    }

    /// A rounded rectangle whose colour runs from `left` to `right`. Each
    /// corner of the shape takes the colour at its place across; the soft
    /// edge keeps its own transparency.
    #[allow(clippy::too_many_arguments)]
    pub fn fill_horizontal_gradient(
        &mut self,
        x: f32,
        y: f32,
        w: f32,
        h: f32,
        radii: CornerRadii,
        left: Color,
        right: Color,
    ) {
        self.fill_gradient((x, y, w, h), radii, left, right, |v| (v.x - x) / w);
    }

    /// A rounded rectangle whose corners each take the colour `along` says
    /// they are from `start` to `end`.
    fn fill_gradient(
        &mut self,
        rect: shapes::ShapeRect,
        radii: CornerRadii,
        start: Color,
        end: Color,
        along: impl Fn(&ColorVertex) -> f32,
    ) {
        let mut shape = Vec::new();
        shapes::fill_rounded_rect(rect, radii, Color::WHITE, self.device_pixel, &mut shape);
        for v in &mut shape {
            let c = start.lerp(end, along(v).clamp(0.0, 1.0));
            v.color = Color {
                a: c.a * v.color.a,
                ..c
            };
        }
        self.triangles(&shape);
    }

    /// The soft shadow of an opaque rounded rectangle at (`x`, `y`), dropped
    /// `drop` below it: `color` where it meets the rectangle, fading to
    /// nothing `spread` further out. Only what the caster leaves showing is
    /// drawn: the bands round the dropped rectangle, and the dropped rectangle
    /// itself from where the caster's bottom corners start to round off. Drawn
    /// with the clip lifted to the whole screen, because a shadow lies outside
    /// the element that casts it. Anything but finite numbers draws nothing.
    #[allow(clippy::too_many_arguments)]
    pub fn soft_shadow(
        &mut self,
        x: f32,
        y: f32,
        w: f32,
        h: f32,
        radius: f32,
        drop: f32,
        spread: f32,
        color: Color,
    ) {
        const LAYERS: u32 = 6;
        let numbers = [x, y, w, h, radius, drop, spread, color.a];
        if !numbers.iter().all(|n| n.is_finite()) || color.a <= 0.0 {
            return;
        }
        let layer = Color {
            a: color.a / LAYERS as f32,
            ..color
        };
        let radii = CornerRadii::all(radius);
        let top = y + drop.max(0.0);
        let rect = (x, top, w, h);
        self.push_clip_unbounded();
        let mut shape = Vec::new();
        shapes::shadow_bands(
            rect,
            radii,
            (spread, LAYERS),
            layer,
            self.device_pixel,
            &mut shape,
        );
        self.triangles(&shape);

        // Above the caster's bottom corners its straight sides cover the
        // dropped rectangle edge to edge.
        let shown = y + h - radius.clamp(0.0, (h * 0.5).max(0.0));
        self.push_clip(x, shown, w, top + h - shown);
        shape.clear();
        shapes::shadow_core(
            rect,
            radii,
            (spread, LAYERS),
            layer,
            self.device_pixel,
            &mut shape,
        );
        self.triangles(&shape);
        self.pop_clip();
        self.pop_clip();
    }

    /// Draw `text` with a dat font at local (`x`, `y`) (the line top):
    /// with `outline`, first the whole string's outline pass in
    /// `outline_color`, then the fill pass.
    #[allow(clippy::too_many_arguments)]
    pub fn string_dat(
        &mut self,
        font: &UiFont,
        text: &str,
        x: f32,
        y: f32,
        color: Color,
        outline: bool,
        outline_color: Color,
    ) {
        if text.is_empty() {
            return;
        }
        if outline {
            self.string_dat_pass(font, text, x, y, outline_color, true);
        }
        self.string_dat_pass(font, text, x, y, color, false);
    }

    /// One pass of [`RenderContext::string_dat`]: the fill, or the outline
    /// from the background atlas (inflated by the border pixels), or eight
    /// one-pixel offset copies of the fill when the font has no background.
    /// Each glyph lands on a whole pixel; the baseline is snapped once. A fill
    /// on a high-density display takes its glyph images from the font's sharper
    /// bake when it has one, and the pen still moves by the font's own metrics.
    pub fn string_dat_pass(
        &mut self,
        font: &UiFont,
        text: &str,
        x: f32,
        y: f32,
        tint: Color,
        outline_pass: bool,
    ) {
        if text.is_empty() {
            return;
        }
        let origin_x = self.origin.0 + x;
        let origin_y = self.origin.1 + y;
        let base_y = (origin_y + 0.5).floor();
        let texture = match (outline_pass, font.background) {
            (true, Some(bg)) => bg,
            _ => font.foreground,
        };
        let sharp = if outline_pass {
            None
        } else {
            self.sharp_font(font)
        };
        // The sharper glyphs sit on the framebuffer's pixels, so its baseline
        // must too: a whole unit is half a pixel out at 1.5, and nearest
        // sampling there would split every texel.
        let grid = sharp
            .as_ref()
            .map_or(1.0, |s| s.scale.min(1.0 / self.device_pixel));
        let sharp_base_y = snap_to_grid(base_y, grid);
        let mut quads = Vec::new();
        let mut sharp_quads = Vec::new();
        let mut pen = origin_x;
        for c in text.chars() {
            let Some(g) = font.glyph(c) else {
                continue;
            };
            if let Some(s) = &sharp
                && let Some(fine) = s.font.glyph(c)
                && fine.width > 0
                && fine.height > 0
            {
                let texel = 1.0 / s.scale;
                let down = f32::from(fine.vertical_offset_before) * texel * grid;
                let uv = atlas_uv(
                    i32::from(fine.offset_x),
                    i32::from(fine.offset_y),
                    i32::from(fine.width),
                    i32::from(fine.height),
                    s.font.foreground.width,
                    s.font.foreground.height,
                );
                self.push_glyph(
                    &mut sharp_quads,
                    snap_to_grid(pen + f32::from(fine.horizontal_offset_before) * texel, grid),
                    sharp_base_y + down.round_ties_even() / grid,
                    f32::from(fine.width) * texel,
                    f32::from(fine.height) * texel,
                    uv,
                );
                pen += crate::font::glyph_advance(g);
                continue;
            }
            let gx = (pen + f32::from(g.horizontal_offset_before) + 0.5).floor();
            let gy = base_y + f32::from(g.vertical_offset_before);
            let (gw, gh) = (f32::from(g.width), f32::from(g.height));
            if gw > 0.0 && gh > 0.0 {
                if outline_pass {
                    self.outline_glyph(font, g, (gx, gy, gw, gh), &mut quads);
                } else {
                    let uv = atlas_uv(
                        i32::from(g.offset_x),
                        i32::from(g.offset_y),
                        i32::from(g.width),
                        i32::from(g.height),
                        font.foreground.width,
                        font.foreground.height,
                    );
                    self.push_glyph(&mut quads, gx, gy, gw, gh, uv);
                }
            }
            pen += crate::font::glyph_advance(g);
        }
        let tint = self.apply_alpha(tint);
        if !quads.is_empty() {
            self.emit(DrawCmd::Glyphs {
                texture: texture.texture,
                tint,
                quads,
            });
        }
        if let Some(s) = sharp
            && !sharp_quads.is_empty()
        {
            self.emit(DrawCmd::Glyphs {
                texture: s.font.foreground.texture,
                tint,
                quads: sharp_quads,
            });
        }
    }

    /// The sharper bake to draw `font` from: none at one framebuffer pixel a
    /// unit or less, where it would be no sharper.
    fn sharp_font(&self, font: &UiFont) -> Option<SharpFont> {
        if self.device_pixel >= 1.0 {
            return None;
        }
        self.sharp_fonts.as_ref()?.get(&font.id).cloned()
    }

    /// Framebuffer pixels one unit of this context covers.
    #[must_use]
    pub fn pixels_per_unit(&self) -> f32 {
        1.0 / self.device_pixel
    }

    /// One pass of glyphs a caller laid out: `(x, y, w, h, uv)` from this
    /// context's origin, each snapped to a grid of `grid` cells a unit.
    pub fn placed_glyphs(
        &mut self,
        texture: TextureRef,
        tint: Color,
        grid: f32,
        glyphs: impl IntoIterator<Item = (f32, f32, f32, f32, (f32, f32, f32, f32))>,
    ) {
        let (ox, oy) = self.origin;
        let mut quads = Vec::new();
        for (x, y, w, h, uv) in glyphs {
            if w > 0.0 && h > 0.0 {
                let (x, y) = (snap_to_grid(ox + x, grid), snap_to_grid(oy + y, grid));
                self.push_glyph(&mut quads, x, y, w, h, uv);
            }
        }
        if !quads.is_empty() {
            let tint = self.apply_alpha(tint);
            self.emit(DrawCmd::Glyphs {
                texture,
                tint,
                quads,
            });
        }
    }

    fn push_glyph(
        &self,
        quads: &mut Vec<Quad>,
        x: f32,
        y: f32,
        w: f32,
        h: f32,
        uv: (f32, f32, f32, f32),
    ) {
        let q = Quad {
            x,
            y,
            w,
            h,
            u0: uv.0,
            v0: uv.1,
            u1: uv.2,
            v1: uv.3,
        };
        match self.clip {
            Some(clip) => {
                if let Some(q) = clip.clip_quad(q) {
                    quads.push(q);
                }
            }
            None => quads.push(q),
        }
    }

    fn outline_glyph(
        &self,
        font: &UiFont,
        g: &ac_dat::objects::FontCharDesc,
        (gx, gy, gw, gh): (f32, f32, f32, f32),
        quads: &mut Vec<Quad>,
    ) {
        if let Some(bg) = font.background {
            let (bx, by) = (font.border_x as f32, font.border_y as f32);
            let (iw, ih) = (gw + 2.0 * bx, gh + 2.0 * by);
            let (mut src_x, mut src_y) = (f32::from(g.offset_x) - bx, f32::from(g.offset_y) - by);
            let (mut src_w, mut src_h) = (iw, ih);
            let (mut dst_x, mut dst_y, mut dst_w, mut dst_h) = (gx - bx, gy - by, iw, ih);
            if src_x < 0.0 {
                dst_x -= src_x;
                dst_w += src_x;
                src_w += src_x;
                src_x = 0.0;
            }
            if src_y < 0.0 {
                dst_y -= src_y;
                dst_h += src_y;
                src_h += src_y;
                src_y = 0.0;
            }
            let (aw, ah) = (bg.width as f32, bg.height as f32);
            if src_x + src_w > aw {
                let over = src_x + src_w - aw;
                src_w -= over;
                dst_w -= over;
            }
            if src_y + src_h > ah {
                let over = src_y + src_h - ah;
                src_h -= over;
                dst_h -= over;
            }
            if src_w <= 0.0 || src_h <= 0.0 {
                return;
            }
            let uv = atlas_uv(
                src_x as i32,
                src_y as i32,
                src_w as i32,
                src_h as i32,
                bg.width,
                bg.height,
            );
            self.push_glyph(quads, dst_x, dst_y, dst_w, dst_h, uv);
        } else {
            let uv = atlas_uv(
                i32::from(g.offset_x),
                i32::from(g.offset_y),
                i32::from(g.width),
                i32::from(g.height),
                font.foreground.width,
                font.foreground.height,
            );
            for dy in -1..=1 {
                for dx in -1..=1 {
                    if dx == 0 && dy == 0 {
                        continue;
                    }
                    self.push_glyph(quads, gx + dx as f32, gy + dy as f32, gw, gh, uv);
                }
            }
        }
    }
}

fn atlas_uv(x: i32, y: i32, w: i32, h: i32, atlas_w: u32, atlas_h: u32) -> (f32, f32, f32, f32) {
    if atlas_w == 0 || atlas_h == 0 {
        return (0.0, 0.0, 0.0, 0.0);
    }
    let (aw, ah) = (atlas_w as f32, atlas_h as f32);
    (
        x as f32 / aw,
        y as f32 / ah,
        (x + w) as f32 / aw,
        (y + h) as f32 / ah,
    )
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiClipRectTests.cs`,
    //! `UiRenderContextAlphaTests.cs`, `UiRenderContextPrimitiveTests.cs`,
    //! `UiRenderContextDrawStringDatOutlineTests.cs` and
    //! `UiRenderContextReuseTests.cs`. OpenAC inspects its GPU batcher; these
    //! inspect the draw list (the batching tests belong to the renderer).

    use super::*;
    use crate::font::Atlas;
    use crate::font::test_fonts::glyph;
    use std::f32::consts::PI;

    const A: TextureRef = TextureRef::Surface(7);
    const FG: TextureRef = TextureRef::Surface(1);
    const BG: TextureRef = TextureRef::Surface(2);

    fn with_ctx(f: impl FnOnce(&mut RenderContext<'_>)) -> DrawList {
        let mut list = DrawList::default();
        let mut ctx = RenderContext::new(&mut list, (800.0, 600.0), &NoSprites);
        f(&mut ctx);
        list
    }

    fn polygon(list: &DrawList) -> &[Vertex] {
        match &list.main[0] {
            DrawCmd::Polygon { vertices, .. } => vertices,
            other => panic!("not a polygon: {other:?}"),
        }
    }

    fn has_corner(v: &[Vertex], x: f32, y: f32) -> bool {
        v.iter()
            .any(|p| (p.x - x).abs() < 0.001 && (p.y - y).abs() < 0.001)
    }

    fn font(background: bool, border: i32, glyphs: &[ac_dat::objects::FontCharDesc]) -> UiFont {
        let atlas = |t| Atlas {
            texture: t,
            width: 64,
            height: 64,
        };
        UiFont::new(
            0x4000_0000,
            atlas(FG),
            background.then(|| atlas(BG)),
            16.0,
            12.0,
            glyphs.iter().copied(),
            border,
            border,
        )
    }

    fn glyphs(cmd: &DrawCmd) -> (TextureRef, Color, &[Quad]) {
        match cmd {
            DrawCmd::Glyphs {
                texture,
                tint,
                quads,
            } => (*texture, *tint, quads),
            other => panic!("not glyphs: {other:?}"),
        }
    }

    #[test]
    fn clip_quad_crops_geometry_and_matching_uvs() {
        let clip = ClipRect::new(0.0, 0.0, 100.0, 100.0);
        let q = clip
            .clip_quad(Quad {
                x: -8.0,
                y: 88.0,
                w: 32.0,
                h: 32.0,
                u0: 0.0,
                v0: 0.0,
                u1: 1.0,
                v1: 1.0,
            })
            .unwrap();
        assert_eq!((q.x, q.y, q.w, q.h), (0.0, 88.0, 24.0, 12.0));
        assert_eq!((q.u0, q.v0, q.u1, q.v1), (0.25, 0.0, 1.0, 0.375));
    }

    #[test]
    fn disjoint_clips_intersect_to_empty() {
        let i = ClipRect::intersect(
            ClipRect::new(0.0, 0.0, 20.0, 20.0),
            ClipRect::new(30.0, 30.0, 40.0, 40.0),
        );
        assert!(i.is_empty());
        assert!(
            i.clip_quad(Quad {
                w: 10.0,
                h: 10.0,
                ..Quad::default()
            })
            .is_none()
        );
    }

    #[test]
    fn opacity_multiplies_and_nests_and_unmatched_pop_is_harmless() {
        let list = with_ctx(|c| {
            c.pop_alpha();
            c.sprite(
                A,
                0.0,
                0.0,
                10.0,
                10.0,
                (0.0, 0.0, 1.0, 1.0),
                Color::rgba(1.0, 1.0, 1.0, 0.8),
            );
            c.push_alpha(0.5);
            c.sprite(A, 0.0, 0.0, 10.0, 10.0, (0.0, 0.0, 1.0, 1.0), Color::WHITE);
            c.push_alpha(0.5);
            c.sprite(
                A,
                0.0,
                0.0,
                10.0,
                10.0,
                (0.0, 0.0, 1.0, 1.0),
                Color::rgba(1.0, 1.0, 1.0, 0.8),
            );
        });
        let alphas: Vec<f32> = list
            .main
            .iter()
            .map(|c| match c {
                DrawCmd::Sprite { tint, .. } => tint.a,
                _ => unreachable!(),
            })
            .collect();
        assert_eq!(alphas, [0.8, 0.5, 0.2]);
    }

    #[test]
    fn opacity_multiplies_both_glyph_passes() {
        let f = font(true, 4, &[glyph('A', (4, 4), (8, 8), 0, 0, 0)]);
        let list = with_ctx(|c| {
            c.push_alpha(0.5);
            c.string_dat(&f, "A", 0.0, 0.0, Color::WHITE, true, Color::BLACK);
        });
        assert_eq!(glyphs(&list.main[0]).1.a, 0.5);
        assert_eq!(glyphs(&list.main[1]).1.a, 0.5);
    }

    #[test]
    fn a_horizontal_segment_covers_half_its_width_on_each_side() {
        let list = with_ctx(|c| c.line(10.0, 20.0, 110.0, 20.0, Color::WHITE, 4.0));
        let v = polygon(&list);
        assert_eq!(v.len(), 4);
        let xs = v.iter().map(|p| p.x);
        let ys: Vec<f32> = v.iter().map(|p| p.y).collect();
        assert_eq!(xs.clone().fold(f32::MAX, f32::min), 10.0);
        assert_eq!(xs.fold(f32::MIN, f32::max), 110.0);
        assert_eq!(ys.iter().copied().fold(f32::MAX, f32::min), 18.0);
        assert_eq!(ys.iter().copied().fold(f32::MIN, f32::max), 22.0);
    }

    #[test]
    fn a_diagonal_segment_is_perpendicularly_thick() {
        let list = with_ctx(|c| c.line(0.0, 0.0, 100.0, 100.0, Color::WHITE, 2.0));
        let v = polygon(&list);
        let o = std::f32::consts::FRAC_1_SQRT_2;
        assert!(has_corner(v, -o, o));
        assert!(has_corner(v, o, -o));
        assert!(has_corner(v, 100.0 - o, 100.0 + o));
        assert!(has_corner(v, 100.0 + o, 100.0 - o));
    }

    #[test]
    fn a_segment_follows_the_origin_and_opacity() {
        let list = with_ctx(|c| {
            c.push_transform(30.0, 40.0);
            c.push_alpha(0.5);
            c.line(0.0, 0.0, 10.0, 0.0, Color::WHITE, 2.0);
        });
        let v = polygon(&list);
        assert!(has_corner(v, 30.0, 39.0) && has_corner(v, 40.0, 41.0));
        match &list.main[0] {
            DrawCmd::Polygon { color, .. } => assert_eq!(color.a, 0.5),
            _ => unreachable!(),
        }
    }

    #[test]
    fn a_segment_with_no_length_or_width_draws_nothing() {
        let list = with_ctx(|c| {
            c.line(5.0, 5.0, 5.0, 5.0, Color::WHITE, 3.0);
            c.line(0.0, 0.0, 50.0, 0.0, Color::WHITE, 0.0);
        });
        assert!(list.main.is_empty());
    }

    #[test]
    fn a_segment_is_trimmed_to_the_clip() {
        let list = with_ctx(|c| {
            c.push_clip(0.0, 0.0, 50.0, 600.0);
            c.line(0.0, 100.0, 200.0, 100.0, Color::WHITE, 4.0);
        });
        let max_x = polygon(&list).iter().map(|p| p.x).fold(f32::MIN, f32::max);
        assert_eq!(max_x, 50.0);
    }

    #[test]
    fn an_unturned_blit_lands_on_the_plain_rectangle() {
        let list = with_ctx(|c| {
            c.sprite_transformed(
                A,
                100.0,
                100.0,
                40.0,
                20.0,
                (0.0, 0.0, 1.0, 1.0),
                Color::WHITE,
                0.0,
                (1.0, 1.0),
                (20.0, 10.0),
            );
        });
        let v = polygon(&list);
        assert_eq!(v.len(), 4);
        for (x, y) in [
            (100.0, 100.0),
            (140.0, 100.0),
            (140.0, 120.0),
            (100.0, 120.0),
        ] {
            assert!(has_corner(v, x, y));
        }
    }

    #[test]
    fn a_quarter_turn_about_the_middle_swaps_width_and_height() {
        let list = with_ctx(|c| {
            c.sprite_transformed(
                A,
                100.0,
                100.0,
                40.0,
                20.0,
                (0.0, 0.0, 1.0, 1.0),
                Color::WHITE,
                PI / 2.0,
                (1.0, 1.0),
                (20.0, 10.0),
            );
        });
        let v = polygon(&list);
        for (x, y) in [(130.0, 90.0), (130.0, 130.0), (110.0, 130.0), (110.0, 90.0)] {
            assert!(has_corner(v, x, y), "{x},{y} in {v:?}");
        }
    }

    #[test]
    fn scale_grows_away_from_the_pivot_only() {
        let list = with_ctx(|c| {
            c.sprite_transformed(
                A,
                10.0,
                10.0,
                40.0,
                20.0,
                (0.0, 0.0, 1.0, 1.0),
                Color::WHITE,
                0.0,
                (2.0, 3.0),
                (0.0, 0.0),
            );
        });
        let v = polygon(&list);
        assert!(has_corner(v, 10.0, 10.0) && has_corner(v, 90.0, 70.0));
    }

    #[test]
    fn a_turned_blit_is_trimmed_and_keeps_its_texture_coordinates() {
        let list = with_ctx(|c| {
            c.push_clip(0.0, 0.0, 50.0, 100.0);
            c.sprite_transformed(
                A,
                0.0,
                0.0,
                100.0,
                100.0,
                (0.0, 0.0, 1.0, 1.0),
                Color::WHITE,
                0.0,
                (1.0, 1.0),
                (0.0, 0.0),
            );
        });
        let v = polygon(&list);
        let max_x = v.iter().map(|p| p.x).fold(f32::MIN, f32::max);
        let max_u = v.iter().map(|p| p.u).fold(f32::MIN, f32::max);
        let min_u = v.iter().map(|p| p.u).fold(f32::MAX, f32::min);
        assert!((max_x - 50.0).abs() < 1e-3 && (max_u - 0.5).abs() < 1e-3 && min_u.abs() < 1e-3);
    }

    #[test]
    fn a_corner_cut_off_the_clip_gives_five_corners() {
        let list = with_ctx(|c| {
            c.push_clip(0.0, 0.0, 800.0, 600.0);
            c.sprite_transformed(
                A,
                0.0,
                100.0,
                40.0,
                40.0,
                (0.0, 0.0, 1.0, 1.0),
                Color::WHITE,
                PI / 4.0,
                (1.0, 1.0),
                (20.0, 20.0),
            );
        });
        let v = polygon(&list);
        assert_eq!(v.len(), 5);
        assert!(v.iter().all(|p| p.x >= -0.001));
    }

    #[test]
    fn a_blit_entirely_outside_the_clip_draws_nothing() {
        let list = with_ctx(|c| {
            c.push_clip(0.0, 0.0, 50.0, 50.0);
            c.sprite_transformed(
                A,
                200.0,
                200.0,
                40.0,
                40.0,
                (0.0, 0.0, 1.0, 1.0),
                Color::WHITE,
                0.3,
                (1.0, 1.0),
                (0.0, 0.0),
            );
        });
        assert!(list.main.is_empty());
    }

    #[test]
    fn a_turned_blit_moves_with_the_origin() {
        let list = with_ctx(|c| {
            c.push_transform(200.0, 300.0);
            c.sprite_transformed(
                A,
                0.0,
                0.0,
                10.0,
                10.0,
                (0.0, 0.0, 1.0, 1.0),
                Color::WHITE,
                0.0,
                (1.0, 1.0),
                (0.0, 0.0),
            );
        });
        let v = polygon(&list);
        assert!(has_corner(v, 200.0, 300.0) && has_corner(v, 210.0, 310.0));
    }

    #[test]
    fn outlined_string_is_one_outline_run_then_one_fill_run() {
        let f = font(
            true,
            4,
            &[
                glyph('A', (4, 4), (8, 8), 0, 0, 0),
                glyph('B', (4, 4), (8, 8), 1, 0, 0),
            ],
        );
        let list = with_ctx(|c| c.string_dat(&f, "AB", 0.0, 0.0, Color::WHITE, true, Color::BLACK));
        assert_eq!(list.main.len(), 2);
        let (t0, _, q0) = glyphs(&list.main[0]);
        let (t1, _, q1) = glyphs(&list.main[1]);
        assert_eq!((t0, q0.len(), t1, q1.len()), (BG, 2, FG, 2));
    }

    #[test]
    fn no_outline_runs_only_the_fill_pass() {
        let f = font(true, 4, &[glyph('A', (4, 4), (8, 8), 0, 0, 0)]);
        let list = with_ctx(|c| c.string_dat(&f, "A", 0.0, 0.0, Color::WHITE, false, Color::BLACK));
        assert_eq!(list.main.len(), 1);
        assert_eq!(glyphs(&list.main[0]).0, FG);
    }

    #[test]
    fn the_outline_uses_the_outline_colour() {
        let f = font(true, 4, &[glyph('A', (4, 4), (8, 8), 0, 0, 0)]);
        let fill = Color::rgba(1.0, 1.0, 0.247, 1.0);
        let outline = Color::rgba(0.2, 0.4, 0.6, 1.0);
        let list = with_ctx(|c| c.string_dat(&f, "A", 0.0, 0.0, fill, true, outline));
        assert_eq!(glyphs(&list.main[0]).1, outline);
        assert_eq!(glyphs(&list.main[1]).1, fill);
        assert_eq!(DEFAULT_OUTLINE_COLOR, Color::BLACK);
    }

    #[test]
    fn the_background_pass_inflates_by_the_border_pixels() {
        let mut f = font(true, 0, &[glyph('A', (10, 8), (9, 8), 0, 0, 0)]);
        (f.border_x, f.border_y) = (4, 3);
        f.foreground.width = 100;
        f.foreground.height = 100;
        f.background = Some(Atlas {
            texture: BG,
            width: 100,
            height: 100,
        });
        let list = with_ctx(|c| c.string_dat(&f, "A", 20.0, 5.0, Color::WHITE, true, Color::BLACK));
        let bg = glyphs(&list.main[0]).2[0];
        let fg = glyphs(&list.main[1]).2[0];
        assert_eq!((fg.w, fg.h), (9.0, 8.0));
        assert_eq!((fg.u0, fg.v0, fg.u1, fg.v1), (0.10, 0.08, 0.19, 0.16));
        assert_eq!(
            (bg.x, bg.y, bg.w, bg.h),
            (fg.x - 4.0, fg.y - 3.0, fg.w + 8.0, fg.h + 6.0)
        );
        assert_eq!((bg.u0, bg.v0), (6.0 / 100.0, 5.0 / 100.0));
        assert_eq!((bg.u1, bg.v1), (23.0 / 100.0, 19.0 / 100.0));
    }

    #[test]
    fn a_zero_border_background_pass_matches_the_fill_rectangle() {
        let f = font(true, 0, &[glyph('A', (4, 4), (8, 8), 0, 0, 0)]);
        let list = with_ctx(|c| c.string_dat(&f, "A", 0.0, 0.0, Color::WHITE, true, Color::BLACK));
        assert_eq!(glyphs(&list.main[0]).2[0], glyphs(&list.main[1]).2[0]);
    }

    #[test]
    fn without_a_background_atlas_the_outline_is_eight_offset_copies() {
        let f = font(false, 0, &[glyph('A', (4, 4), (8, 8), 0, 0, 0)]);
        let list =
            with_ctx(|c| c.string_dat(&f, "A", 20.0, 10.0, Color::WHITE, true, Color::BLACK));
        let (t0, _, outline) = glyphs(&list.main[0]);
        let (t1, _, fill) = glyphs(&list.main[1]);
        assert_eq!((t0, t1), (FG, FG));
        let mut seen = std::collections::HashSet::new();
        for q in outline.iter().chain(fill.iter()) {
            let (dx, dy) = ((q.x - 20.0).round() as i32, (q.y - 10.0).round() as i32);
            assert!((-1..=1).contains(&dx) && (-1..=1).contains(&dy));
            assert!(seen.insert((dx, dy)));
        }
        assert_eq!(seen.len(), 9);
    }

    /// A sharper bake of font `font_id` at `scale`, with a texture of its own.
    fn sharp_twin(
        font_id: u32,
        glyphs: &[ac_dat::objects::FontCharDesc],
        scale: f32,
    ) -> Arc<SharpFonts> {
        let twin = UiFont::new(
            font_id + 1,
            Atlas {
                texture: TextureRef::Runtime(22),
                width: 128,
                height: 128,
            },
            None,
            32.0,
            24.0,
            glyphs.iter().copied(),
            0,
            0,
        );
        Arc::new(SharpFonts::from([(
            font_id,
            SharpFont {
                font: Arc::new(twin),
                scale,
            },
        )]))
    }

    fn sharp_ctx(
        pixels: f32,
        fonts: Arc<SharpFonts>,
        f: impl FnOnce(&mut RenderContext<'_>),
    ) -> DrawList {
        let mut list = DrawList::default();
        let mut ctx = RenderContext::new(&mut list, (800.0, 600.0), &NoSprites)
            .with_pixels_per_unit(pixels)
            .with_sharp_fonts(Some(fonts));
        f(&mut ctx);
        list
    }

    fn fill(c: &mut RenderContext<'_>, f: &UiFont, text: &str, x: f32, y: f32) {
        c.string_dat(f, text, x, y, Color::WHITE, false, Color::BLACK);
    }

    #[test]
    fn a_sharper_bake_draws_at_its_fraction_of_the_size_with_its_own_texture() {
        let f = font(false, 0, &[glyph('A', (0, 0), (5, 8), 1, 2, 3)]);
        let twin = sharp_twin(f.id, &[glyph('A', (10, 20), (10, 16), 2, 4, 6)], 2.0);
        let width = f.measure_width("AA");
        let list = sharp_ctx(2.0, twin, |c| fill(c, &f, "AA", 0.0, 0.0));
        assert_eq!(f.measure_width("AA"), width);
        assert_eq!(list.main.len(), 1);
        let (texture, _, q) = glyphs(&list.main[0]);
        assert_eq!(texture, TextureRef::Runtime(22));
        // The image is half its texels' size; the pen moves by the font's own
        // advance of 8, not the sharper bake's.
        assert_eq!((q[0].x, q[0].y, q[0].w, q[0].h), (1.0, 3.0, 5.0, 8.0));
        assert_eq!((q[1].x, q[1].y), (9.0, 3.0));
        // Its texture coordinates are the sharper atlas's box.
        assert_eq!(
            (q[0].u0, q[0].v0, q[0].u1, q[0].v1),
            (10.0 / 128.0, 20.0 / 128.0, 20.0 / 128.0, 36.0 / 128.0)
        );
    }

    #[test]
    fn at_one_framebuffer_pixel_a_unit_the_sharper_bake_is_not_used() {
        let f = font(false, 0, &[glyph('A', (0, 0), (5, 8), 1, 2, 3)]);
        let twin = sharp_twin(f.id, &[glyph('A', (10, 20), (10, 16), 2, 4, 6)], 2.0);
        let plain = with_ctx(|c| fill(c, &f, "AA", 0.4, 2.5));
        let list = sharp_ctx(1.0, twin, |c| fill(c, &f, "AA", 0.4, 2.5));
        assert_eq!(list, plain);
    }

    #[test]
    fn a_font_without_a_sharper_bake_draws_as_it_always_has() {
        let f = font(false, 0, &[glyph('A', (0, 0), (5, 8), 1, 2, 3)]);
        let twin = sharp_twin(f.id + 100, &[glyph('A', (10, 20), (10, 16), 2, 4, 6)], 2.0);
        let plain = with_ctx(|c| fill(c, &f, "AA", 0.4, 2.5));
        let list = sharp_ctx(2.0, twin, |c| fill(c, &f, "AA", 0.4, 2.5));
        assert_eq!(list, plain);
    }

    /// At 1.5 pixels a unit a whole unit is half a pixel out, so every edge
    /// must land on the finer grid or nearest sampling would split texels.
    #[test]
    fn at_one_and_a_half_each_texel_lands_on_one_framebuffer_pixel() {
        let f = font(
            false,
            0,
            &[
                glyph('A', (0, 0), (5, 8), 1, 2, 3),
                glyph('B', (0, 0), (5, 8), 1, 1, 4),
            ],
        );
        let twin = sharp_twin(
            f.id,
            &[
                glyph('A', (10, 20), (9, 12), 1, 3, 5),
                glyph('B', (30, 20), (9, 12), 2, 2, 7),
            ],
            1.5,
        );
        let list = sharp_ctx(1.5, twin, |c| fill(c, &f, "ABAB", 3.3, 7.1));
        let (_, _, q) = glyphs(&list.main[0]);
        assert_eq!(q.len(), 4);
        let on_grid = |v: f32| ((v * 1.5).round() - v * 1.5).abs() < 1e-3;
        for g in q {
            assert!(on_grid(g.x) && on_grid(g.y), "{g:?}");
            assert!(on_grid(g.x + g.w) && on_grid(g.y + g.h), "{g:?}");
            assert_eq!((g.w, g.h), (6.0, 8.0));
        }
    }

    #[test]
    fn a_glyph_the_sharper_bake_has_no_image_for_keeps_the_fonts_own() {
        // A space has no image at any size; a glyph that did not fit the finer
        // atlas keeps an empty record.
        let f = font(
            false,
            0,
            &[
                glyph('A', (0, 0), (5, 8), 1, 2, 3),
                glyph('C', (8, 0), (5, 8), 0, 0, 3),
            ],
        );
        let twin = sharp_twin(
            f.id,
            &[
                glyph('A', (10, 20), (10, 16), 2, 4, 6),
                glyph('C', (0, 0), (0, 0), 0, 0, 0),
            ],
            2.0,
        );
        let list = sharp_ctx(2.0, twin, |c| fill(c, &f, "AC", 0.0, 0.0));
        assert_eq!(list.main.len(), 2);
        let (coarse, _, own) = glyphs(&list.main[0]);
        assert_eq!(coarse, f.foreground.texture);
        assert_eq!((own[0].x, own[0].w), (8.0, 5.0));
        let (fine, _, sharp) = glyphs(&list.main[1]);
        assert_eq!(fine, TextureRef::Runtime(22));
        assert_eq!(sharp.len(), 1);
    }

    #[test]
    fn an_outline_stays_in_the_fonts_own_glyphs() {
        let f = font(true, 1, &[glyph('A', (4, 4), (5, 8), 1, 2, 3)]);
        let twin = sharp_twin(f.id, &[glyph('A', (10, 20), (10, 16), 2, 4, 6)], 2.0);
        let list = sharp_ctx(2.0, twin, |c| {
            c.string_dat(&f, "A", 0.0, 0.0, Color::WHITE, true, Color::BLACK);
        });
        assert_eq!(list.main.len(), 2);
        assert_eq!(glyphs(&list.main[0]).0, BG);
        assert_eq!(glyphs(&list.main[1]).0, TextureRef::Runtime(22));
    }

    #[test]
    fn a_sharper_glyph_is_cut_by_the_clip_and_faded_by_the_opacity() {
        let f = font(false, 0, &[glyph('A', (0, 0), (10, 8), 0, 0, 0)]);
        let twin = sharp_twin(f.id, &[glyph('A', (0, 0), (20, 16), 0, 0, 0)], 2.0);
        let list = sharp_ctx(2.0, twin, |c| {
            c.push_clip(0.0, 0.0, 4.0, 100.0);
            c.push_alpha(0.5);
            fill(c, &f, "A", 0.0, 0.0);
        });
        let (_, tint, q) = glyphs(&list.main[0]);
        assert_eq!(tint.a, 0.5);
        assert_eq!(q[0].w, 4.0);
        assert_eq!(q[0].u1, 8.0 / 128.0);
    }

    #[test]
    fn glyphs_snap_to_whole_pixels_from_a_snapped_baseline() {
        let f = font(false, 0, &[glyph('A', (0, 0), (5, 8), 1, 0, 4)]);
        let list =
            with_ctx(|c| c.string_dat(&f, "AA", 0.4, 2.5, Color::WHITE, false, Color::BLACK));
        let q = glyphs(&list.main[0]).2;
        assert_eq!((q[0].x, q[0].y), (1.0, 7.0));
        assert_eq!((q[1].x, q[1].y), (7.0, 7.0));
    }

    const RED: Color = Color::rgba(1.0, 0.0, 0.0, 1.0);
    const BLUE: Color = Color::rgba(0.0, 0.0, 1.0, 1.0);
    const PURPLE: Color = Color::rgba(0.5, 0.0, 0.5, 1.0);

    fn cv(x: f32, y: f32, color: Color) -> ColorVertex {
        ColorVertex::new(x, y, color)
    }

    fn cut(clip: ClipRect, triangle: [ColorVertex; 3]) -> Vec<ColorVertex> {
        let mut out = Vec::new();
        clip.clip_triangle(triangle, &mut out);
        out
    }

    fn assert_near(actual: ColorVertex, expected: ColorVertex) {
        let c = (actual.color, expected.color);
        let far = [
            actual.x - expected.x,
            actual.y - expected.y,
            c.0.r - c.1.r,
            c.0.g - c.1.g,
            c.0.b - c.1.b,
            c.0.a - c.1.a,
        ]
        .iter()
        .any(|d| d.abs() > 1e-4);
        assert!(!far, "{actual:?} is not {expected:?}");
    }

    #[test]
    fn a_triangle_wholly_inside_the_clip_comes_back_unchanged() {
        let t = [cv(1.0, 1.0, RED), cv(9.0, 1.0, BLUE), cv(1.0, 9.0, RED)];
        assert_eq!(cut(ClipRect::new(0.0, 0.0, 10.0, 10.0), t), t);
    }

    #[test]
    fn a_triangle_wholly_outside_or_an_empty_clip_leaves_nothing() {
        let outside = [cv(20.0, 1.0, RED), cv(29.0, 1.0, BLUE), cv(20.0, 9.0, RED)];
        assert!(cut(ClipRect::new(0.0, 0.0, 10.0, 10.0), outside).is_empty());
        let inside = [cv(1.0, 1.0, RED), cv(2.0, 1.0, RED), cv(1.0, 2.0, RED)];
        assert!(cut(ClipRect::new(0.0, 0.0, 0.0, 10.0), inside).is_empty());
    }

    #[test]
    fn a_cut_gains_corners_on_the_edge_with_the_colour_blended() {
        let out = cut(
            ClipRect::new(0.0, 0.0, 10.0, 100.0),
            [cv(0.0, 0.0, RED), cv(20.0, 0.0, BLUE), cv(0.0, 20.0, RED)],
        );
        // Four corners left, as two triangles from the first.
        assert_eq!(out.len(), 6);
        let expected = [
            cv(0.0, 0.0, RED),
            cv(10.0, 0.0, PURPLE),
            cv(10.0, 10.0, PURPLE),
            cv(0.0, 0.0, RED),
            cv(10.0, 10.0, PURPLE),
            cv(0.0, 20.0, RED),
        ];
        for (actual, expected) in out.iter().zip(expected) {
            assert_near(*actual, expected);
        }
    }

    #[test]
    fn a_triangle_cut_on_every_edge_has_seven_corners_all_inside() {
        let out = cut(
            ClipRect::new(0.0, 0.0, 20.0, 20.0),
            [cv(-5.0, -5.0, RED), cv(25.0, 5.0, BLUE), cv(5.0, 25.0, RED)],
        );
        // Seven corners make five triangles.
        assert_eq!(out.len(), 15);
        for v in out {
            assert!((-1e-4..=20.0 + 1e-4).contains(&v.x), "{v:?}");
            assert!((-1e-4..=20.0 + 1e-4).contains(&v.y), "{v:?}");
        }
    }

    fn triangle() -> [ColorVertex; 3] {
        [cv(0.0, 0.0, RED), cv(20.0, 0.0, BLUE), cv(0.0, 20.0, RED)]
    }

    fn triangles(list: &DrawList) -> &[ColorVertex] {
        match &list.main[0] {
            DrawCmd::Triangles { vertices } => vertices,
            other => panic!("not triangles: {other:?}"),
        }
    }

    #[test]
    fn triangles_move_with_the_origin_and_fade_with_the_opacity() {
        let list = with_ctx(|c| {
            c.push_transform(10.0, 20.0);
            c.push_alpha(0.5);
            c.triangles(&triangle());
        });
        let v = triangles(&list);
        assert_eq!(v.len(), 3);
        assert_eq!((v[0].x, v[0].y), (10.0, 20.0));
        assert_eq!((v[1].x, v[1].y), (30.0, 20.0));
        assert_eq!((v[0].color.a, v[1].color.a, v[2].color.a), (0.5, 0.5, 0.5));
    }

    #[test]
    fn a_clip_cuts_triangles_and_blends_the_colour_at_the_cut() {
        let list = with_ctx(|c| {
            c.push_clip(0.0, 0.0, 10.0, 100.0);
            c.triangles(&triangle());
        });
        let v = triangles(&list);
        assert_eq!(v.len(), 6);
        for p in v {
            assert!(p.x <= 10.0 + 1e-4);
            if (p.x - 10.0).abs() < 1e-4 {
                assert!((p.color.r - 0.5).abs() < 1e-4);
            }
        }
    }

    #[test]
    fn a_triangle_that_is_not_all_numbers_is_dropped_and_the_rest_are_kept() {
        for bad in [f32::NAN, f32::INFINITY, f32::NEG_INFINITY] {
            let mut bad_position = triangle();
            bad_position[1].x = bad;
            let mut bad_colour = triangle();
            bad_colour[2].color.g = bad;
            for broken in [bad_position, bad_colour] {
                let list = with_ctx(|c| {
                    c.triangles(&[broken, triangle()].concat());
                });
                assert_eq!(triangles(&list), triangle());
                let clipped = with_ctx(|c| {
                    c.push_clip(0.0, 0.0, 100.0, 100.0);
                    c.triangles(&[broken, triangle()].concat());
                });
                assert_eq!(triangles(&clipped), triangle());
            }
        }
    }

    #[test]
    fn triangles_outside_the_clip_or_under_an_empty_one_draw_nothing() {
        let list = with_ctx(|c| {
            c.push_clip(50.0, 50.0, 10.0, 10.0);
            c.triangles(&triangle());
            c.pop_clip();
            c.push_clip(0.0, 0.0, 0.0, 10.0);
            c.triangles(&triangle());
        });
        assert!(list.main.is_empty());
    }

    #[test]
    fn a_shape_moves_with_the_origin_and_is_cut_by_the_clip() {
        let list = with_ctx(|c| {
            c.push_transform(100.0, 100.0);
            c.push_clip(0.0, 0.0, 10.0, 40.0);
            c.fill_rounded_rect(0.0, 0.0, 20.0, 20.0, CornerRadii::all(4.0), RED);
        });
        for v in triangles(&list) {
            assert!(v.x >= 99.5 && v.x <= 110.0 + 1e-4, "{v:?}");
            assert!(v.y >= 99.5 && v.y <= 120.5, "{v:?}");
        }
    }

    #[test]
    fn a_shapes_soft_edge_is_one_device_pixel_wide() {
        let rim = |pixels: f32| {
            let mut list = DrawList::default();
            let mut c = RenderContext::new(&mut list, (800.0, 600.0), &NoSprites)
                .with_pixels_per_unit(pixels);
            c.fill_rounded_rect(10.0, 10.0, 20.0, 20.0, CornerRadii::default(), RED);
            triangles(&list)
                .iter()
                .map(|v| v.x)
                .fold(f32::MAX, f32::min)
        };
        assert_eq!(rim(1.0), 9.5);
        assert_eq!(rim(2.0), 9.75);
        // A scale that means nothing counts as one.
        assert_eq!(rim(0.0), 9.5);
        assert_eq!(rim(f32::NAN), 9.5);
    }

    #[test]
    fn a_polygon_says_what_it_came_to_and_draws_only_when_it_drew() {
        let list = with_ctx(|c| {
            let line = [cv(0.0, 0.0, RED), cv(10.0, 0.0, RED), cv(20.0, 0.0, RED)];
            assert_eq!(c.fill_convex_polygon(&line), ShapeOutcome::Nothing);
            assert_eq!(
                c.fill_convex_polygon(&line[..2]),
                ShapeOutcome::TooFewPoints
            );
        });
        assert!(list.main.is_empty());
        let list = with_ctx(|c| {
            assert_eq!(c.fill_convex_polygon(&triangle()), ShapeOutcome::Drawn);
        });
        assert_eq!(list.main.len(), 1);
    }

    #[test]
    fn a_smooth_line_has_soft_sides_round_its_thickness() {
        let list = with_ctx(|c| c.smooth_line(10.0, 20.0, 110.0, 20.0, RED, 4.0));
        let ys: Vec<f32> = triangles(&list).iter().map(|v| v.y).collect();
        assert_eq!(ys.iter().copied().fold(f32::MAX, f32::min), 17.5);
        assert_eq!(ys.iter().copied().fold(f32::MIN, f32::max), 22.5);
        let list = with_ctx(|c| {
            c.smooth_line(5.0, 5.0, 5.0, 5.0, RED, 3.0);
            c.smooth_line(0.0, 0.0, 50.0, 0.0, RED, 0.0);
        });
        assert!(list.main.is_empty());
    }

    #[test]
    fn a_gradient_takes_the_colour_at_its_height_and_keeps_its_soft_edge() {
        let list = with_ctx(|c| {
            c.fill_vertical_gradient(0.0, 100.0, 50.0, 100.0, CornerRadii::default(), RED, BLUE);
        });
        let v = triangles(&list);
        let solid_top = v
            .iter()
            .find(|p| p.y == 100.5 && p.color.a == 1.0)
            .expect("the solid ring's top");
        assert!((solid_top.color.r - 0.995).abs() < 1e-4);
        let solid_bottom = v
            .iter()
            .find(|p| p.y == 199.5 && p.color.a == 1.0)
            .expect("the solid ring's bottom");
        assert!((solid_bottom.color.b - 0.995).abs() < 1e-4);
        // The rim beyond the shape holds the end colour, faded out.
        let rim = v.iter().find(|p| p.y == 99.5).expect("the top rim");
        assert_eq!(rim.color, Color { a: 0.0, ..RED });
    }

    #[test]
    fn a_horizontal_gradient_takes_the_colour_at_its_place_and_keeps_its_soft_edge() {
        let list = with_ctx(|c| {
            c.fill_horizontal_gradient(100.0, 0.0, 100.0, 50.0, CornerRadii::default(), RED, BLUE);
        });
        let v = triangles(&list);
        let solid_left = v
            .iter()
            .find(|p| p.x == 100.5 && p.color.a == 1.0)
            .expect("the solid ring's left");
        assert!((solid_left.color.r - 0.995).abs() < 1e-4);
        let solid_right = v
            .iter()
            .find(|p| p.x == 199.5 && p.color.a == 1.0)
            .expect("the solid ring's right");
        assert!((solid_right.color.b - 0.995).abs() < 1e-4);
        // The rim beyond the shape holds the end colour, faded out.
        let rim = v.iter().find(|p| p.x == 99.5).expect("the left rim");
        assert_eq!(rim.color, Color { a: 0.0, ..RED });
        let rim = v.iter().find(|p| p.x == 200.5).expect("the right rim");
        assert_eq!(rim.color, Color { a: 0.0, ..BLUE });
    }

    /// Rounded corners keep to the line between the two colours, and a shape
    /// with no width draws nothing rather than dividing by it.
    #[test]
    fn a_horizontal_gradient_follows_rounded_corners_and_ignores_an_empty_shape() {
        let list = with_ctx(|c| {
            c.fill_horizontal_gradient(0.0, 0.0, 100.0, 50.0, CornerRadii::all(10.0), RED, BLUE);
        });
        for p in triangles(&list) {
            let t = (p.x / 100.0).clamp(0.0, 1.0);
            assert!((p.color.b - t).abs() < 1e-4, "{p:?}");
            assert!((p.color.r - (1.0 - t)).abs() < 1e-4, "{p:?}");
        }
        let empty = with_ctx(|c| {
            c.fill_horizontal_gradient(0.0, 0.0, 0.0, 50.0, CornerRadii::default(), RED, BLUE);
        });
        assert!(empty.main.is_empty());
    }

    #[test]
    fn a_shadow_escapes_the_clip_and_leaves_the_stacks_as_they_were() {
        let black = Color::rgba(0.0, 0.0, 0.0, 0.5);
        let mut list = DrawList::default();
        let mut c = RenderContext::new(&mut list, (800.0, 600.0), &NoSprites);
        c.push_clip(100.0, 100.0, 40.0, 40.0);
        let before = c.depths();
        c.soft_shadow(100.0, 100.0, 40.0, 40.0, 6.0, 4.0, 14.0, black);
        assert_eq!(c.depths(), before);
        let reach = list.commands().flat_map(|cmd| match cmd {
            DrawCmd::Triangles { vertices } => vertices.iter().map(|v| v.x).collect(),
            _ => Vec::new(),
        });
        assert!(reach.fold(f32::MAX, f32::min) < 100.0);
        // The bands, then the strip of the dropped rectangle showing below
        // the caster.
        assert_eq!(list.main.len(), 2);
        let DrawCmd::Triangles { vertices } = &list.main[1] else {
            panic!("the strip is triangles");
        };
        for v in vertices {
            assert!(v.y >= 134.0 - 1e-3 && v.y <= 144.0 + 1e-3, "{v:?}");
            assert!(v.x >= 100.0 - 1e-3 && v.x <= 140.0 + 1e-3, "{v:?}");
        }
    }

    #[test]
    fn a_shadow_with_no_colour_draws_nothing() {
        let list = with_ctx(|c| {
            c.soft_shadow(0.0, 0.0, 10.0, 10.0, 2.0, 2.0, 4.0, Color::TRANSPARENT);
            c.soft_shadow(0.0, 0.0, 10.0, 10.0, 2.0, 2.0, 0.0, Color::BLACK);
        });
        assert!(list.main.is_empty());
    }

    #[test]
    fn stacks_report_their_depths() {
        with_ctx(|c| {
            c.push_clip(0.0, 0.0, 10.0, 10.0);
            c.push_transform(1.0, 1.0);
            c.push_alpha(0.5);
            assert_eq!(c.depths(), (1, 1, 1));
            c.pop_clip();
            c.pop_transform();
            c.pop_alpha();
            assert_eq!(c.depths(), (0, 0, 0));
            assert_eq!(c.origin(), (0.0, 0.0));
        });
    }

    #[test]
    fn overlay_commands_go_to_the_overlay_layer() {
        let list = with_ctx(|c| {
            c.fill(0.0, 0.0, 1.0, 1.0, Color::WHITE);
            c.begin_overlay();
            c.fill(0.0, 0.0, 2.0, 2.0, Color::WHITE);
            c.end_overlay();
        });
        assert_eq!((list.main.len(), list.overlay.len()), (1, 1));
    }
}
