//! An authored resize grip: a window border piece that resizes its window
//! from its own edges.

use crate::draw::{Color, RenderContext};
use crate::geometry::ResizeEdges;
use crate::root::UiRoot;
use crate::tree::{ElementId, Widget};

/// Where on the window border a grip sits.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum Border {
    /// Nowhere.
    #[default]
    None,
    /// Top-left corner.
    UpperLeft,
    /// Top run.
    Top,
    /// Top-right corner.
    UpperRight,
    /// Right run.
    Right,
    /// Bottom-right corner.
    LowerRight,
    /// Bottom run.
    Bottom,
    /// Bottom-left corner.
    LowerLeft,
    /// Left run.
    Left,
}

impl Border {
    /// The border piece named by the grip's four authored flags.
    #[must_use]
    pub fn decode(bottom: bool, left: bool, right: bool, top: bool) -> Self {
        if right {
            if top {
                Self::UpperRight
            } else if bottom {
                Self::LowerRight
            } else {
                Self::Right
            }
        } else if left {
            if top {
                Self::UpperLeft
            } else if bottom {
                Self::LowerLeft
            } else {
                Self::Left
            }
        } else if top {
            Self::Top
        } else if bottom {
            Self::Bottom
        } else {
            Self::None
        }
    }

    /// The window edges the piece drags.
    #[must_use]
    pub fn edges(self) -> ResizeEdges {
        match self {
            Self::UpperLeft => ResizeEdges::LEFT | ResizeEdges::TOP,
            Self::Top => ResizeEdges::TOP,
            Self::UpperRight => ResizeEdges::RIGHT | ResizeEdges::TOP,
            Self::Right => ResizeEdges::RIGHT,
            Self::LowerRight => ResizeEdges::RIGHT | ResizeEdges::BOTTOM,
            Self::Bottom => ResizeEdges::BOTTOM,
            Self::LowerLeft => ResizeEdges::LEFT | ResizeEdges::BOTTOM,
            Self::Left => ResizeEdges::LEFT,
            Self::None => ResizeEdges::NONE,
        }
    }
}

/// A resize grip with its authored image.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ResizeGrip {
    /// Where on the border it sits.
    pub border: Border,
    /// Its image (0: none).
    pub sprite: u32,
}

impl Widget for ResizeGrip {
    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        if self.sprite == 0 {
            return;
        }
        if let Some(e) = ui.element(me) {
            rc.surface(self.sprite, 0.0, 0.0, e.width, e.height, Color::WHITE);
        }
    }

    fn resize_grip_edges(&self) -> Option<ResizeEdges> {
        Some(self.border.edges())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn flags_decode_to_border_pieces() {
        assert_eq!(Border::decode(false, false, true, true), Border::UpperRight);
        assert_eq!(Border::decode(true, false, true, false), Border::LowerRight);
        assert_eq!(Border::decode(false, true, false, false), Border::Left);
        assert_eq!(Border::decode(true, false, false, false), Border::Bottom);
        assert_eq!(Border::decode(false, false, false, false), Border::None);
        assert_eq!(
            Border::UpperLeft.edges(),
            ResizeEdges::LEFT | ResizeEdges::TOP
        );
    }
}
