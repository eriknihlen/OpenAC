//! The shared retail window chrome: the bevel images every panel frame and
//! popup uses, and the nine-piece split of a frame into corners, edges and
//! centre.

/// Tiled interior fill (48×48).
pub const CENTER_FILL: u32 = 0x0600_4CC2;
/// Top edge (10×5, tiled across).
pub const TOP_EDGE: u32 = 0x0600_74BF;
/// Bottom edge (10×5).
pub const BOTTOM_EDGE: u32 = 0x0600_74C1;
/// Left edge (5×10, tiled down).
pub const LEFT_EDGE: u32 = 0x0600_74C0;
/// Right edge (5×10).
pub const RIGHT_EDGE: u32 = 0x0600_74C2;
/// Top-left corner.
pub const CORNER_TL: u32 = 0x0600_74C3;
/// Top-right corner.
pub const CORNER_TR: u32 = 0x0600_74C4;
/// Bottom-left corner.
pub const CORNER_BL: u32 = 0x0600_74C5;
/// Bottom-right corner.
pub const CORNER_BR: u32 = 0x0600_74C6;
/// Bevel thickness, pixels.
pub const BORDER: i32 = 5;

/// A rectangle, element-local pixels.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Rect {
    /// Left.
    pub x: f32,
    /// Top.
    pub y: f32,
    /// Width.
    pub w: f32,
    /// Height.
    pub h: f32,
}

impl Rect {
    const fn new(x: f32, y: f32, w: f32, h: f32) -> Self {
        Self { x, y, w, h }
    }
}

/// The nine pieces of a frame.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct FrameRects {
    /// The centre.
    pub center: Rect,
    /// The top edge between the corners.
    pub top: Rect,
    /// The bottom edge.
    pub bottom: Rect,
    /// The left edge.
    pub left: Rect,
    /// The right edge.
    pub right: Rect,
    /// Top-left corner.
    pub tl: Rect,
    /// Top-right corner.
    pub tr: Rect,
    /// Bottom-left corner.
    pub bl: Rect,
    /// Bottom-right corner.
    pub br: Rect,
}

/// Split a `w` × `h` frame with a `b`-pixel border into its nine pieces.
#[must_use]
pub fn compute_frame_rects(w: f32, h: f32, b: i32) -> FrameRects {
    let b = b as f32;
    let inner_w = w - 2.0 * b;
    let inner_h = h - 2.0 * b;
    FrameRects {
        center: Rect::new(b, b, inner_w, inner_h),
        top: Rect::new(b, 0.0, inner_w, b),
        bottom: Rect::new(b, h - b, inner_w, b),
        left: Rect::new(0.0, b, b, inner_h),
        right: Rect::new(w - b, b, b, inner_h),
        tl: Rect::new(0.0, 0.0, b, b),
        tr: Rect::new(w - b, 0.0, b, b),
        bl: Rect::new(0.0, h - b, b, b),
        br: Rect::new(w - b, h - b, b, b),
    }
}
