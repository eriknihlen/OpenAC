//! The retail edge-mode layout: how an element's four edges follow its
//! parent when the parent changes size.
//!
//! Each edge has a mode 0..=4: keep the current edge (0), keep the authored
//! edge or add the parent's growth (1/2, by side), centre (3), or scale
//! proportionally (4).

use crate::geometry::PixelRect;

/// An edge mode outside 0..=4.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct InvalidEdgeMode(pub u32);

impl std::fmt::Display for InvalidEdgeMode {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "edge mode {} is outside 0..=4", self.0)
    }
}

impl std::error::Error for InvalidEdgeMode {}

fn validate(mode: u32) -> Result<u32, InvalidEdgeMode> {
    if mode > 4 {
        Err(InvalidEdgeMode(mode))
    } else {
        Ok(mode)
    }
}

/// The four edge modes and the authored rectangles they measure from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LayoutPolicy {
    left: u32,
    top: u32,
    right: u32,
    bottom: u32,
    /// The child's rectangle the modes measure from.
    pub original_child: PixelRect,
    /// The parent's rectangle at that time.
    pub original_parent: PixelRect,
    /// Apply mode 0 (keep current) even to an empty current rectangle.
    pub preserve_current_when_empty: bool,
}

impl LayoutPolicy {
    /// A policy with the given edge modes; a mode above 4 is refused.
    pub fn new(
        left: u32,
        top: u32,
        right: u32,
        bottom: u32,
        original_child: PixelRect,
        original_parent: PixelRect,
    ) -> Result<Self, InvalidEdgeMode> {
        Ok(Self {
            left: validate(left)?,
            top: validate(top)?,
            right: validate(right)?,
            bottom: validate(bottom)?,
            original_child,
            original_parent,
            preserve_current_when_empty: false,
        })
    }

    /// The modes, left, top, right, bottom.
    #[must_use]
    pub fn modes(&self) -> (u32, u32, u32, u32) {
        (self.left, self.top, self.right, self.bottom)
    }

    /// Measure from new rectangles from now on (after a deliberate move or
    /// resize, so the next layout pass does not undo it).
    pub fn rebase(&mut self, child: PixelRect, parent: PixelRect) {
        self.original_child = child;
        self.original_parent = parent;
    }

    /// Where the child goes in `current_parent`.
    #[must_use]
    pub fn apply(&self, current_child: PixelRect, current_parent: PixelRect) -> PixelRect {
        apply_modes(
            (self.left, self.top, self.right, self.bottom),
            self.original_child,
            self.original_parent,
            current_child,
            current_parent,
            self.preserve_current_when_empty,
        )
    }

    /// [`LayoutPolicy::apply`] for modes given directly; a mode above 4 is
    /// refused.
    pub fn apply_raw(
        modes: (u32, u32, u32, u32),
        original_child: PixelRect,
        original_parent: PixelRect,
        current_child: PixelRect,
        current_parent: PixelRect,
        preserve_current_when_empty: bool,
    ) -> Result<PixelRect, InvalidEdgeMode> {
        validate(modes.0)?;
        validate(modes.1)?;
        validate(modes.2)?;
        validate(modes.3)?;
        Ok(apply_modes(
            modes,
            original_child,
            original_parent,
            current_child,
            current_parent,
            preserve_current_when_empty,
        ))
    }
}

fn apply_modes(
    (left, top, right, bottom): (u32, u32, u32, u32),
    original_child: PixelRect,
    original_parent: PixelRect,
    current_child: PixelRect,
    current_parent: PixelRect,
    preserve_current_when_empty: bool,
) -> PixelRect {
    let delta_x = current_parent.width().wrapping_sub(original_parent.width());
    let delta_y = current_parent
        .height()
        .wrapping_sub(original_parent.height());
    let scale_x = if original_parent.width() != 0 {
        f64::from(current_parent.width()) / f64::from(original_parent.width())
    } else {
        0.0
    };
    let scale_y = if original_parent.height() != 0 {
        f64::from(current_parent.height()) / f64::from(original_parent.height())
    } else {
        0.0
    };

    let mut x0 = apply_near(
        left,
        original_child.x0,
        original_child.width(),
        current_parent.width(),
        delta_x,
        scale_x,
    );
    let mut y0 = apply_near(
        top,
        original_child.y0,
        original_child.height(),
        current_parent.height(),
        delta_y,
        scale_y,
    );
    let mut x1 = apply_far(
        right,
        original_child.x1,
        original_child.width(),
        current_parent.width(),
        delta_x,
        scale_x,
    );
    let mut y1 = apply_far(
        bottom,
        original_child.y1,
        original_child.height(),
        current_parent.height(),
        delta_y,
        scale_y,
    );

    if current_child.width() != 0 || current_child.height() != 0 || preserve_current_when_empty {
        if left == 0 {
            x0 = current_child.x0;
        }
        if top == 0 {
            y0 = current_child.y0;
        }
        if right == 0 {
            x1 = current_child.x1;
        }
        if bottom == 0 {
            y1 = current_child.y1;
        }
    }
    PixelRect::new(x0, y0, x1, y1)
}

/// Truncation toward zero, as the client's float-to-int conversion does.
fn truncate(value: f64) -> i32 {
    value as i32
}

fn apply_near(mode: u32, edge: i32, size: i32, parent: i32, delta: i32, scale: f64) -> i32 {
    match mode {
        2 => edge.wrapping_add(delta),
        3 => (parent / 2).wrapping_sub(size / 2),
        4 => truncate(f64::from(edge) * scale),
        _ => edge,
    }
}

fn apply_far(mode: u32, edge: i32, size: i32, parent: i32, delta: i32, scale: f64) -> i32 {
    match mode {
        1 => edge.wrapping_add(delta),
        3 => (parent / 2).wrapping_add(size / 2).wrapping_sub(1),
        4 => truncate(f64::from(edge) * scale),
        _ => edge,
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiLayoutPolicyTests.cs`
    //! (the pure cases; the element cases live with the tree).

    use super::*;

    const PARENT: PixelRect = PixelRect::from_position_and_size(0, 0, 800, 600);
    const CHILD: PixelRect = PixelRect::from_position_and_size(100, 50, 200, 100);

    fn apply(modes: (u32, u32, u32, u32), current: PixelRect, parent: PixelRect) -> PixelRect {
        LayoutPolicy::apply_raw(modes, CHILD, PARENT, current, parent, false).unwrap()
    }

    #[test]
    fn mode_zero_preserves_every_current_edge() {
        let current = PixelRect::new(111, 66, 333, 177);
        let resized = PixelRect::from_position_and_size(0, 0, 1600, 900);
        assert_eq!(apply((0, 0, 0, 0), current, resized), current);
    }

    #[test]
    fn mode_one_keeps_near_edges_and_adds_delta_to_far_edges() {
        let resized = PixelRect::from_position_and_size(0, 0, 1000, 700);
        let r = apply((1, 1, 1, 1), CHILD, resized);
        assert_eq!(r, PixelRect::new(100, 50, 499, 249));
        assert_eq!((r.width(), r.height()), (400, 200));
    }

    #[test]
    fn mixed_two_and_one_moves_a_fixed_size_child_with_the_far_side() {
        let resized = PixelRect::from_position_and_size(0, 0, 1000, 700);
        let r = apply((2, 2, 1, 1), CHILD, resized);
        assert_eq!(r, PixelRect::new(300, 150, 499, 249));
        assert_eq!((r.width(), r.height()), (CHILD.width(), CHILD.height()));
    }

    #[test]
    fn mode_three_uses_the_inclusive_pixel_centre() {
        let resized = PixelRect::from_position_and_size(0, 0, 1001, 701);
        assert_eq!(
            apply((3, 3, 3, 3), CHILD, resized),
            PixelRect::new(400, 300, 599, 399)
        );
    }

    #[test]
    fn mode_four_scales_each_coordinate_truncating_toward_zero() {
        let parent = PixelRect::from_position_and_size(0, 0, 10, 10);
        let child = PixelRect::new(-3, -3, -1, -1);
        let resized = PixelRect::from_position_and_size(0, 0, 15, 15);
        let r =
            LayoutPolicy::apply_raw((4, 4, 4, 4), child, parent, child, resized, false).unwrap();
        assert_eq!(r, PixelRect::new(-4, -4, -1, -1));
    }

    #[test]
    fn modes_above_four_are_refused() {
        assert_eq!(
            LayoutPolicy::new(5, 0, 0, 0, CHILD, PARENT),
            Err(InvalidEdgeMode(5))
        );
        assert!(
            LayoutPolicy::apply_raw((0, 0, 0, 9), CHILD, PARENT, CHILD, PARENT, false).is_err()
        );
    }
}
