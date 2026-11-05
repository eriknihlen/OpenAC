//! Rectangles, edge sets and the pure resize and anchor arithmetic.

use std::ops::{BitAnd, BitAndAssign, BitOr, BitOrAssign, Not};

macro_rules! edge_set {
    ($(#[$doc:meta])* $name:ident { $($(#[$fdoc:meta])* $flag:ident = $bit:expr),* $(,)? }) => {
        $(#[$doc])*
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
        pub struct $name(pub u8);

        impl $name {
            /// No edge.
            pub const NONE: Self = Self(0);
            $($(#[$fdoc])* pub const $flag: Self = Self($bit);)*

            /// True when every edge of `other` is in `self`.
            #[must_use]
            pub fn contains(self, other: Self) -> bool {
                self.0 & other.0 == other.0
            }

            /// True when `self` and `other` share an edge.
            #[must_use]
            pub fn intersects(self, other: Self) -> bool {
                self.0 & other.0 != 0
            }

            /// True when no edge is set.
            #[must_use]
            pub fn is_empty(self) -> bool {
                self.0 == 0
            }
        }

        impl BitOr for $name {
            type Output = Self;
            fn bitor(self, rhs: Self) -> Self {
                Self(self.0 | rhs.0)
            }
        }

        impl BitOrAssign for $name {
            fn bitor_assign(&mut self, rhs: Self) {
                self.0 |= rhs.0;
            }
        }

        impl BitAnd for $name {
            type Output = Self;
            fn bitand(self, rhs: Self) -> Self {
                Self(self.0 & rhs.0)
            }
        }

        impl BitAndAssign for $name {
            fn bitand_assign(&mut self, rhs: Self) {
                self.0 &= rhs.0;
            }
        }

        impl Not for $name {
            type Output = Self;
            fn not(self) -> Self {
                Self(!self.0)
            }
        }
    };
}

edge_set! {
    /// Edges an element keeps its distance to when its parent resizes.
    AnchorEdges {
        /// Keep the left margin.
        LEFT = 1,
        /// Keep the top margin.
        TOP = 2,
        /// Keep the right margin.
        RIGHT = 4,
        /// Keep the bottom margin.
        BOTTOM = 8,
    }
}

impl AnchorEdges {
    /// Pinned top-left with a fixed size: the default.
    pub const TOP_LEFT: Self = Self(1 | 2);
    /// All four edges: stretch both ways.
    pub const ALL: Self = Self(1 | 2 | 4 | 8);
}

edge_set! {
    /// Window edges being (or able to be) dragged to resize.
    ResizeEdges {
        /// Left edge.
        LEFT = 1,
        /// Right edge.
        RIGHT = 2,
        /// Top edge.
        TOP = 4,
        /// Bottom edge.
        BOTTOM = 8,
    }
}

impl ResizeEdges {
    /// All four edges.
    pub const ALL: Self = Self(1 | 2 | 4 | 8);
    /// Left and right.
    pub const HORIZONTAL: Self = Self(1 | 2);
    /// Top and bottom.
    pub const VERTICAL: Self = Self(4 | 8);
}

/// An inclusive pixel rectangle: `x1` and `y1` are the last pixel inside,
/// so the width is `x1 - x0 + 1`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct PixelRect {
    /// Left pixel.
    pub x0: i32,
    /// Top pixel.
    pub y0: i32,
    /// Right pixel (inclusive).
    pub x1: i32,
    /// Bottom pixel (inclusive).
    pub y1: i32,
}

impl PixelRect {
    /// A rectangle from its corner pixels.
    #[must_use]
    pub const fn new(x0: i32, y0: i32, x1: i32, y1: i32) -> Self {
        Self { x0, y0, x1, y1 }
    }

    /// The rectangle at (`x`, `y`) of the given size.
    #[must_use]
    pub const fn from_position_and_size(x: i32, y: i32, width: i32, height: i32) -> Self {
        Self::new(
            x,
            y,
            x.wrapping_add(width).wrapping_sub(1),
            y.wrapping_add(height).wrapping_sub(1),
        )
    }

    /// Width in pixels.
    #[must_use]
    pub const fn width(&self) -> i32 {
        self.x1.wrapping_sub(self.x0).wrapping_add(1)
    }

    /// Height in pixels.
    #[must_use]
    pub const fn height(&self) -> i32 {
        self.y1.wrapping_sub(self.y0).wrapping_add(1)
    }
}

/// A float rectangle: position and size.
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
    /// A rectangle from position and size.
    #[must_use]
    pub const fn new(x: f32, y: f32, w: f32, h: f32) -> Self {
        Self { x, y, w, h }
    }
}

/// Where an anchored child lands in a parent of size (`parent_w`,
/// `parent_h`), given its margins and size when the anchors were captured.
/// Anchored on both sides of an axis it stretches; on the far side only it
/// keeps the far margin; otherwise it keeps the near margin. Negative sizes
/// clamp to 0.
#[allow(clippy::too_many_arguments)]
#[must_use]
pub fn compute_anchored_rect(
    a: AnchorEdges,
    margin_left: f32,
    margin_top: f32,
    margin_right: f32,
    margin_bottom: f32,
    w0: f32,
    h0: f32,
    parent_w: f32,
    parent_h: f32,
) -> Rect {
    let l = a.contains(AnchorEdges::LEFT);
    let r = a.contains(AnchorEdges::RIGHT);
    let (x, mut w) = if l && r {
        (margin_left, parent_w - margin_right - margin_left)
    } else if r {
        (parent_w - margin_right - w0, w0)
    } else {
        (margin_left, w0)
    };
    let t = a.contains(AnchorEdges::TOP);
    let b = a.contains(AnchorEdges::BOTTOM);
    let (y, mut h) = if t && b {
        (margin_top, parent_h - margin_bottom - margin_top)
    } else if b {
        (parent_h - margin_bottom - h0, h0)
    } else {
        (margin_top, h0)
    };
    if w < 0.0 {
        w = 0.0;
    }
    if h < 0.0 {
        h = 0.0;
    }
    Rect::new(x, y, w, h)
}

/// `value` clamped to `min..=max`; like .NET `Math.Clamp`, `min` wins when
/// the bounds cross.
#[must_use]
pub fn clamp(value: f32, min: f32, max: f32) -> f32 {
    if value < min {
        min
    } else if value > max {
        max.max(min)
    } else {
        value
    }
}

/// The window rectangle after dragging `edges` by (`dx`, `dy`) from the
/// start rectangle, each size clamped to its limits. A dragged left or top
/// edge moves the origin so the opposite edge stays put.
#[allow(clippy::too_many_arguments)]
#[must_use]
pub fn resize_rect(
    start: Rect,
    edges: ResizeEdges,
    dx: f32,
    dy: f32,
    min_w: f32,
    min_h: f32,
    max_w: f32,
    max_h: f32,
) -> Rect {
    let mut r = start;
    if edges.contains(ResizeEdges::RIGHT) {
        r.w = clamp(start.w + dx, min_w, max_w);
    }
    if edges.contains(ResizeEdges::BOTTOM) {
        r.h = clamp(start.h + dy, min_h, max_h);
    }
    if edges.contains(ResizeEdges::LEFT) {
        let nw = clamp(start.w - dx, min_w, max_w);
        r.x = start.x + (start.w - nw);
        r.w = nw;
    }
    if edges.contains(ResizeEdges::TOP) {
        let nh = clamp(start.h - dy, min_h, max_h);
        r.y = start.y + (start.h - nh);
        r.h = nh;
    }
    r
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiRootInputTests.cs`
    //! (`ResizeRect_*`, `ComputeAnchoredRect_*`).

    use super::*;

    #[test]
    fn resize_right_bottom_grows_size_only() {
        let r = resize_rect(
            Rect::new(10.0, 20.0, 100.0, 50.0),
            ResizeEdges::RIGHT | ResizeEdges::BOTTOM,
            30.0,
            15.0,
            40.0,
            40.0,
            f32::MAX,
            f32::MAX,
        );
        assert_eq!(r, Rect::new(10.0, 20.0, 130.0, 65.0));
    }

    #[test]
    fn resize_left_moves_origin_and_clamps_to_min() {
        let r = resize_rect(
            Rect::new(10.0, 20.0, 100.0, 50.0),
            ResizeEdges::LEFT,
            80.0,
            0.0,
            40.0,
            40.0,
            f32::MAX,
            f32::MAX,
        );
        assert_eq!((r.w, r.x), (40.0, 70.0));
    }

    #[test]
    fn resize_bottom_and_top_clamp_to_max_height() {
        let r = resize_rect(
            Rect::new(10.0, 20.0, 100.0, 50.0),
            ResizeEdges::BOTTOM,
            0.0,
            1000.0,
            40.0,
            40.0,
            f32::MAX,
            128.0,
        );
        assert_eq!((r.h, r.y), (128.0, 20.0));
        let r = resize_rect(
            Rect::new(10.0, 20.0, 100.0, 50.0),
            ResizeEdges::TOP,
            0.0,
            -1000.0,
            40.0,
            40.0,
            f32::MAX,
            128.0,
        );
        assert_eq!((r.h, r.y), (128.0, 20.0 + 50.0 - 128.0));
    }

    #[test]
    fn anchored_left_right_stretches_width() {
        let r = compute_anchored_rect(
            AnchorEdges::LEFT | AnchorEdges::RIGHT | AnchorEdges::TOP,
            8.0,
            24.0,
            12.0,
            58.0,
            200.0,
            14.0,
            300.0,
            96.0,
        );
        assert_eq!((r.x, r.w), (8.0, 280.0));
    }

    #[test]
    fn anchored_left_top_keeps_size_and_origin() {
        let r = compute_anchored_rect(
            AnchorEdges::TOP_LEFT,
            8.0,
            24.0,
            12.0,
            58.0,
            200.0,
            14.0,
            300.0,
            96.0,
        );
        assert_eq!(r, Rect::new(8.0, 24.0, 200.0, 14.0));
    }

    #[test]
    fn pixel_rect_is_inclusive() {
        let r = PixelRect::from_position_and_size(10, 20, 5, 3);
        assert_eq!((r.x1, r.y1, r.width(), r.height()), (14, 22, 5, 3));
    }

    #[test]
    fn clamp_prefers_min_when_bounds_cross() {
        assert_eq!(clamp(5.0, 10.0, 2.0), 10.0);
        assert_eq!(clamp(50.0, 10.0, 2.0), 10.0);
        assert_eq!(clamp(5.0, 0.0, 10.0), 5.0);
    }
}
