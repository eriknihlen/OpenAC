//! The frame-rate display (`/framerate`, the show-FPS option): two lines in
//! the authored smart-box text at the top-left corner, the frames per
//! second over the last twenty frames and the level-of-detail multiplier.

use crate::import::ImportedLayout;
use crate::root::UiRoot;
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::Text;

/// The smart-box layout holding the display.
pub const LAYOUT_ID: u32 = 0x2100_000F;
/// The display text element.
pub const DISPLAY_ELEMENT_ID: u32 = 0x1000_0047;
/// How many frame times the rate averages.
pub const FPS_HISTORY_LENGTH: usize = 20;

/// The frame rate over the last [`FPS_HISTORY_LENGTH`] frames, as the
/// client's level-of-detail owner measures it.
#[derive(Debug, Clone, PartialEq)]
pub struct FrameRate {
    frame_seconds: [f32; FPS_HISTORY_LENGTH],
    fps: f32,
}

impl Default for FrameRate {
    fn default() -> Self {
        Self {
            frame_seconds: [0.0; FPS_HISTORY_LENGTH],
            fps: 0.0,
        }
    }
}

impl FrameRate {
    /// No frames yet (0 frames per second).
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The rate as of the last frame.
    #[must_use]
    pub fn fps(&self) -> f32 {
        self.fps
    }

    /// One frame took `elapsed_seconds`: the rate is computed from the
    /// history before it, then the frame enters the history.
    pub fn tick(&mut self, elapsed_seconds: f64) {
        let total: f64 = self.frame_seconds.iter().map(|s| f64::from(*s)).sum();
        // The client compares against the 32-bit 0.0002 (0.000199999995).
        self.fps = if total > f64::from(0.0002_f32) {
            (FPS_HISTORY_LENGTH as f64 / total) as f32
        } else {
            0.0
        };
        self.frame_seconds.copy_within(0..FPS_HISTORY_LENGTH - 1, 1);
        self.frame_seconds[0] = elapsed_seconds as f32;
    }
}

/// The two lines the display shows.
#[must_use]
pub fn lines_text(frames_per_second: f64, degrade_multiplier: f64) -> [String; 2] {
    [
        format!("FPS: {frames_per_second:.2}"),
        format!("DEG: {degrade_multiplier:.2}"),
    ]
}

/// The display's controller.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FpsController {
    display: ElementId,
}

impl FpsController {
    /// Bind to the imported smart box: the display sits one pixel in from
    /// the corner with one pixel of padding, not selectable. `None` when the
    /// layout has no display text.
    pub fn bind(ui: &mut UiRoot, layout: &ImportedLayout) -> Option<Self> {
        let display = layout
            .find(DISPLAY_ELEMENT_ID)
            .filter(|d| ui.widget::<Text>(*d).is_some())?;
        if let Some(e) = ui.element_mut(display) {
            e.left = 1.0;
            e.top = 1.0;
        }
        ui.with::<Text, _>(display, |t, ui| {
            t.padding = 1.0;
            if let Some(e) = ui.element_mut(display) {
                t.set_selectable(e, false);
            }
        });
        Some(Self { display })
    }

    /// The display text.
    #[must_use]
    pub fn display(&self) -> ElementId {
        self.display
    }

    /// Show or hide the display and write the numbers.
    pub fn update(
        &self,
        ui: &mut UiRoot,
        frames_per_second: f64,
        degrade_multiplier: f64,
        visible: bool,
    ) {
        ui.set_visible(self.display, visible);
        let Some(t) = ui.widget_mut::<Text>(self.display) else {
            return;
        };
        let color = t.default_color;
        let lines: Vec<Line> = lines_text(frames_per_second, degrade_multiplier)
            .into_iter()
            .map(|s| Line::new(s, color))
            .collect();
        if t.content_lines() != lines {
            t.set_lines(lines);
        }
    }
}
