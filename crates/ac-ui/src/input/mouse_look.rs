//! Instant mouse look (middle button held): the mouse's sideways motion
//! turns the character. Raw motion is gathered between frames into one
//! sample a frame; a motionless spell longer than 0.2 seconds sends one
//! zero sample (which ends the drift). The game ignores the first five
//! non-zero samples of each sideways motion (its "horizontal extent"), so
//! a small twitch does not turn the character.

/// Seconds without motion after which a zero sample is sent.
pub const IDLE_ZERO_DELAY_SECONDS: f32 = 0.2;

/// The default turn per pixel of sideways motion.
pub const DEFAULT_SENSITIVITY_PER_PIXEL: f32 = 0.066_666_67;

/// Mouse-look state.
#[derive(Debug, Clone, PartialEq)]
pub struct MouseLookState {
    active: bool,
    captured_cursor: (f32, f32),
    horizontal_extent: i32,
    queued: (f32, f32),
    last_mouse_input_time: f32,
    /// The turn adjustment per pixel.
    pub sensitivity_per_pixel: f32,
}

impl Default for MouseLookState {
    fn default() -> Self {
        Self {
            active: false,
            captured_cursor: (0.0, 0.0),
            horizontal_extent: 0,
            queued: (0.0, 0.0),
            last_mouse_input_time: 0.0,
            sensitivity_per_pixel: DEFAULT_SENSITIVITY_PER_PIXEL,
        }
    }
}

impl MouseLookState {
    /// Inactive, default sensitivity.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Mouse look is on.
    #[must_use]
    pub fn active(&self) -> bool {
        self.active
    }

    /// Where the cursor was when mouse look began.
    #[must_use]
    pub fn captured_cursor(&self) -> (f32, f32) {
        self.captured_cursor
    }

    /// The middle button went down at the cursor: mouse look begins, unless
    /// the UI has the pointer or it is already on.
    pub fn press(&mut self, cursor_x: f32, cursor_y: f32, want_capture_mouse: bool, now: f32) {
        if want_capture_mouse || self.active {
            return;
        }
        self.active = true;
        self.captured_cursor = (cursor_x, cursor_y);
        self.horizontal_extent = 0;
        self.queued = (0.0, 0.0);
        self.last_mouse_input_time = now;
    }

    /// The middle button came up: mouse look ends.
    pub fn release(&mut self) {
        if !self.active {
            return;
        }
        self.active = false;
        self.horizontal_extent = 0;
        self.queued = (0.0, 0.0);
    }

    /// The UI took the pointer: mouse look ends.
    pub fn on_want_capture_mouse_changed(&mut self, want_capture_mouse: bool) {
        if self.active && want_capture_mouse {
            self.release();
        }
    }

    /// Raw motion arrived (ignored while off or not finite).
    pub fn queue_delta(&mut self, dx: f32, dy: f32) {
        if self.active && dx.is_finite() && dy.is_finite() {
            self.queued.0 += dx;
            self.queued.1 += dy;
        }
    }

    /// This frame's sample: the motion gathered since the last one, or a
    /// zero sample once the mouse has been still for longer than
    /// [`IDLE_ZERO_DELAY_SECONDS`]; `None` when there is nothing to send.
    pub fn try_take_raw_sample(&mut self, now: f32) -> Option<(f32, f32)> {
        let (dx, dy) = std::mem::take(&mut self.queued);
        if !self.active {
            return None;
        }
        if dx != 0.0 || dy != 0.0 {
            self.last_mouse_input_time = now;
            return Some((dx, dy));
        }
        (now > self.last_mouse_input_time + IDLE_ZERO_DELAY_SECONDS).then_some((0.0, 0.0))
    }

    /// Process one sideways sample: the turn adjustment to apply (negative
    /// turns right), or `None` while the motion is still within its first
    /// five samples (a zero sample starts over) or mouse look is off.
    pub fn apply_delta(&mut self, dx: f32, extra_sensitivity: f32) -> Option<f32> {
        if !self.active {
            return None;
        }
        let adjustment = -dx * self.sensitivity_per_pixel * extra_sensitivity;
        if adjustment == 0.0 {
            self.horizontal_extent = 0;
            return None;
        }
        self.horizontal_extent += 1;
        (self.horizontal_extent > 5).then_some(adjustment)
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.UI.Abstractions.Tests/Input/MmbMouseLookTests.cs`.

    use super::*;

    fn pressed(sensitivity: Option<f32>) -> MouseLookState {
        let mut ml = MouseLookState::new();
        if let Some(s) = sensitivity {
            ml.sensitivity_per_pixel = s;
        }
        ml.press(0.0, 0.0, false, 0.0);
        ml
    }

    fn close(a: f32, b: f32) -> bool {
        (a - b).abs() < 1e-5
    }

    #[test]
    fn press_activates_and_captures_the_cursor_position() {
        let mut ml = MouseLookState::new();
        ml.press(320.0, 240.0, false, 0.0);
        assert!(ml.active());
        assert_eq!(ml.captured_cursor(), (320.0, 240.0));
        ml.press(999.0, 888.0, false, 0.0);
        assert_eq!(ml.captured_cursor(), (320.0, 240.0), "idempotent");
        ml.release();
        assert!(!ml.active());
    }

    #[test]
    fn the_ui_holding_the_pointer_blocks_or_ends_mouse_look() {
        let mut ml = MouseLookState::new();
        ml.press(0.0, 0.0, true, 0.0);
        assert!(!ml.active());
        ml.on_want_capture_mouse_changed(false);
        assert!(!ml.active());
        ml.press(0.0, 0.0, false, 0.0);
        ml.on_want_capture_mouse_changed(true);
        assert!(!ml.active());
    }

    #[test]
    fn the_sixth_sample_turns_right_for_rightward_motion() {
        let mut ml = pressed(Some(0.01));
        let applied: Vec<f32> = (0..6).filter_map(|_| ml.apply_delta(10.0, 1.0)).collect();
        assert_eq!(applied.len(), 1);
        assert!(close(applied[0], -0.1));
    }

    #[test]
    fn nothing_applies_while_off_or_after_release() {
        let mut ml = MouseLookState::new();
        assert_eq!(ml.apply_delta(100.0, 1.0), None);
        let mut ml = pressed(None);
        ml.release();
        assert_eq!(ml.apply_delta(50.0, 1.0), None);
    }

    #[test]
    fn one_sink_drives_the_character() {
        let mut ml = pressed(Some(0.005));
        let mut applied = Vec::new();
        for _ in 0..5 {
            applied.extend(ml.apply_delta(4.0, 0.5));
        }
        applied.extend(ml.apply_delta(4.0, 0.5));
        applied.extend(ml.apply_delta(-2.0, 0.5));
        assert_eq!(applied.len(), 2);
        assert!(close(applied.iter().sum::<f32>(), -0.005));
    }

    #[test]
    fn a_zero_sample_resets_the_horizontal_extent() {
        let mut ml = pressed(Some(0.01));
        let mut count = 0;
        for _ in 0..5 {
            count += ml.apply_delta(2.0, 1.0).iter().count();
        }
        count += ml.apply_delta(0.0, 1.0).iter().count();
        for _ in 0..5 {
            count += ml.apply_delta(2.0, 1.0).iter().count();
        }
        assert_eq!(count, 0);
    }

    #[test]
    fn the_idle_zero_uses_the_strict_delay_and_resets_the_extent() {
        let mut ml = MouseLookState::new();
        ml.sensitivity_per_pixel = 0.01;
        ml.press(0.0, 0.0, false, 1.0);
        let mut count = 0;
        for i in 0..5 {
            ml.queue_delta(2.0, 0.0);
            let now = 1.01 + i as f32 * 0.01;
            let (dx, dy) = ml.try_take_raw_sample(now).unwrap();
            assert_eq!(dy, 0.0);
            count += ml.apply_delta(dx, 1.0).iter().count();
        }
        assert_eq!(ml.try_take_raw_sample(1.25), None);
        let (idle_dx, idle_dy) = ml.try_take_raw_sample(1.2501).unwrap();
        assert_eq!((idle_dx, idle_dy), (0.0, 0.0));
        count += ml.apply_delta(idle_dx, 1.0).iter().count();
        ml.queue_delta(2.0, 0.0);
        let (resumed, _) = ml.try_take_raw_sample(1.26).unwrap();
        count += ml.apply_delta(resumed, 1.0).iter().count();
        assert_eq!(count, 0);
    }

    #[test]
    fn native_events_accumulate_into_one_frame_sample() {
        let mut ml = MouseLookState::new();
        ml.press(0.0, 0.0, false, 2.0);
        ml.queue_delta(2.0, 1.0);
        ml.queue_delta(3.0, -0.25);
        assert_eq!(ml.try_take_raw_sample(2.01), Some((5.0, 0.75)));
        assert_eq!(ml.try_take_raw_sample(2.02), None);
    }
}
