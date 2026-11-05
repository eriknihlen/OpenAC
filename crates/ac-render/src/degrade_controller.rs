//! The automatic level-of-detail bias: the game measures the frame rate over
//! the last 20 frames and, with "automatic degrades" on, moves the degrade
//! multiplier (-1 to 1) toward fewer details when the rate is low and more
//! when it is high, committing a new value only once the candidate has held
//! steady for 30 frames. With it off, the "graphics performance" slider is
//! the multiplier.
//!
//! The arithmetic keeps the game's precision: a single-precision frame
//! history summed in double, the five fuzzy weights in double, and the
//! numerator stored to single after each term.

/// Frames the rate is measured over.
pub const FPS_HISTORY_LENGTH: usize = 20;
/// Frames a candidate must hold before it is committed.
pub const CANDIDATE_HISTORY_LENGTH: usize = 30;
/// Below this rate the bias moves toward fewer details fastest.
pub const MINIMUM_FPS: f32 = 8.0;
/// The rate the bias leaves alone.
pub const IDEAL_FPS: f32 = 10.0;
/// Above this rate the bias moves toward more details fastest.
pub const MAXIMUM_FPS: f32 = 20.0;

/// The frame-rate measurement and the automatic multiplier of one window.
#[derive(Debug, Clone)]
pub struct BuildingDegradeController {
    frame_seconds: [f32; FPS_HISTORY_LENGTH],
    candidate_history: [f32; CANDIDATE_HISTORY_LENGTH],
    automatic_multiplier: f32,
    fps: f32,
}

impl Default for BuildingDegradeController {
    fn default() -> Self {
        Self {
            frame_seconds: [0.0; FPS_HISTORY_LENGTH],
            candidate_history: [0.0; CANDIDATE_HISTORY_LENGTH],
            automatic_multiplier: 0.0,
            fps: 0.0,
        }
    }
}

impl BuildingDegradeController {
    /// The rate measured before the last frame.
    #[must_use]
    pub fn fps(&self) -> f32 {
        self.fps
    }

    /// The automatic multiplier (kept while automatic degrades are off).
    #[must_use]
    pub fn automatic_multiplier(&self) -> f32 {
        self.automatic_multiplier
    }

    /// The multiplier in force: the automatic one, or the slider's.
    #[must_use]
    pub fn active_multiplier(&self, automatic: bool, graphics_performance: f32) -> f32 {
        if automatic {
            self.automatic_multiplier
        } else {
            graphics_performance
        }
    }

    /// One frame of `elapsed_seconds` finished: the rate is measured over the
    /// 20 frames before it, then it joins the history and the automatic
    /// multiplier advances.
    pub fn tick(&mut self, elapsed_seconds: f64, automatic: bool) {
        let total: f64 = self.frame_seconds.iter().map(|&s| f64::from(s)).sum();
        self.fps = if total > f64::from(0.000_2_f32) {
            (FPS_HISTORY_LENGTH as f64 / total) as f32
        } else {
            0.0
        };
        advance_frame_history(&mut self.frame_seconds, elapsed_seconds as f32);
        self.automatic_multiplier = advance_automatic_multiplier(
            &mut self.candidate_history,
            automatic,
            self.fps,
            self.automatic_multiplier,
        );
    }
}

/// Move every slot one up (the oldest falls off) and store the frame at 0.
pub fn advance_frame_history(history: &mut [f32; FPS_HISTORY_LENGTH], frame_seconds: f32) {
    history.copy_within(..FPS_HISTORY_LENGTH - 1, 1);
    history[0] = frame_seconds;
}

/// Shift the candidate history down one and advance the multiplier: off,
/// it is kept; on, the candidate is committed only when every one of the 30
/// prior slots lies strictly within 0.01 of it.
pub fn advance_automatic_multiplier(
    history: &mut [f32; CANDIDATE_HISTORY_LENGTH],
    automatic: bool,
    fps: f32,
    mut current: f32,
) -> f32 {
    history.copy_within(1.., 0);
    if !automatic {
        history[CANDIDATE_HISTORY_LENGTH - 1] = current;
        return current;
    }
    let candidate = calculate_candidate(fps, current);
    if is_candidate_stable(history, candidate) {
        current = candidate;
    }
    history[CANDIDATE_HISTORY_LENGTH - 1] = current;
    current
}

/// Every slot within 0.01 (a double constant) of `candidate`.
#[must_use]
pub fn is_candidate_stable(history: &[f32; CANDIDATE_HISTORY_LENGTH], candidate: f32) -> bool {
    history
        .iter()
        .all(|&h| (f64::from(h) - f64::from(candidate)).abs() < 0.01)
}

/// The multiplier the rate asks for: the current one moved by the five
/// weighted adjustments (-0.15, -0.02, 0, +0.01, +0.1), clamped to -1..1.
#[must_use]
pub fn calculate_candidate(fps: f32, current: f32) -> f32 {
    let fps_wide = f64::from(fps);
    let (min, ideal, max) = (
        f64::from(MINIMUM_FPS),
        f64::from(IDEAL_FPS),
        f64::from(MAXIMUM_FPS),
    );
    let w0 = low_shoulder(fps_wide, min * 0.75, (min + ideal) * 0.5);
    let w1 = triangle(fps_wide, min, min * 0.25 + ideal * 0.75);
    let w2 = triangle(fps_wide, (min + ideal) * 0.5, (ideal + max) * 0.5);
    let w3 = triangle(fps_wide, ideal * 0.75 + max * 0.25, max);
    let w4 = high_shoulder(fps_wide, (ideal + max) * 0.5, max * 1.25);
    let weight = w0 + w1 + w2 + w3 + w4;
    let after_w0 = (f64::from(-0.15_f32) * w0) as f32;
    let after_w1 = (f64::from(after_w0) - f64::from(0.02_f32) * w1) as f32;
    let after_w2 = (f64::from(after_w1) + f64::from(0.0_f32) * w2) as f32;
    let after_w3 = (f64::from(after_w2) + f64::from(0.01_f32) * w3) as f32;
    let numerator = f64::from(after_w3) + f64::from(0.1_f32) * w4;
    let adjustment = if weight > 0.0 {
        numerator / weight
    } else {
        0.0
    };
    (f64::from(current) + adjustment).clamp(-1.0, 1.0) as f32
}

fn triangle(value: f64, low: f64, high: f64) -> f64 {
    (1.0 - (2.0 * value - (high + low)).abs() / (high - low)).max(0.0)
}

fn low_shoulder(value: f64, peak: f64, zero: f64) -> f64 {
    if value < peak {
        1.0
    } else {
        (1.0 - (value - peak).abs() / (zero - peak)).max(0.0)
    }
}

fn high_shoulder(value: f64, zero: f64, peak: f64) -> f64 {
    if value > peak {
        1.0
    } else {
        (1.0 - (value - peak).abs() / (peak - zero)).max(0.0)
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/Rendering/BuildingDegradeControllerTests.cs`
    //! (bit patterns as OpenAC asserts them; the allocation case has no
    //! counterpart: the controller never allocates).

    use super::*;

    #[test]
    fn the_rate_uses_the_prior_twenty_frames_then_takes_the_finished_one() {
        let mut c = BuildingDegradeController::default();
        for _ in 0..20 {
            c.tick(0.05, false);
        }
        assert!((c.fps() - 20.0 / 0.95).abs() < 1e-4);
        c.tick(0.05, false);
        assert!((c.fps() - 20.0).abs() < 1e-4);
    }

    #[test]
    fn the_wide_sum_narrows_once_and_matches_the_retail_bits() {
        let mut c = BuildingDegradeController::default();
        for _ in 0..10 {
            c.tick(0.001, false);
            c.tick(0.005, false);
        }
        c.tick(0.0, false);
        assert_eq!(c.fps().to_bits(), 0x43A6_AAAB);
    }

    #[test]
    fn the_history_moves_up_and_stores_the_frame_at_slot_zero() {
        let mut history: [f32; 20] = std::array::from_fn(|i| (i + 1) as f32);
        advance_frame_history(&mut history, 99.0);
        assert_eq!(history[0], 99.0);
        for (i, h) in history.iter().enumerate().skip(1) {
            assert_eq!(*h, i as f32);
        }
    }

    #[test]
    fn manual_and_automatic_share_the_settings_without_resetting_the_history() {
        let mut c = BuildingDegradeController::default();
        for _ in 0..51 {
            c.tick(1.0 / 14.0, true);
        }
        let automatic = c.automatic_multiplier();
        assert!(automatic > 0.0);
        assert_eq!(c.active_multiplier(true, -0.4), automatic);
        c.tick(0.05, false);
        assert_eq!(c.active_multiplier(false, -0.4), -0.4);
        assert_eq!(c.automatic_multiplier(), automatic);
        assert_eq!(c.active_multiplier(true, -0.4), automatic);
    }

    #[test]
    fn exceptional_deltas_stay_defined() {
        let mut c = BuildingDegradeController::default();
        for _ in 0..100 {
            c.tick(1.0 / 60.0, true);
        }
        for d in [0.0, f64::NAN, f64::INFINITY, f64::NEG_INFINITY] {
            c.tick(d, true);
        }
        c.tick(1.0 / 60.0, true);
        assert!((-1.0..=1.0).contains(&c.automatic_multiplier()));
    }

    #[test]
    fn zero_tiny_and_exceptional_deltas_keep_the_float_history() {
        for (delta, following) in [
            (0.0, 0x41A8_6BCA_u32),
            (0.000_01, 0x41A8_6B56),
            (f64::NAN, 0),
            (f64::INFINITY, 0),
            (f64::NEG_INFINITY, 0),
        ] {
            let mut c = BuildingDegradeController::default();
            for _ in 0..20 {
                c.tick(0.05, true);
            }
            c.tick(delta, true);
            assert_eq!(c.fps().to_bits(), 0x41A0_0000, "{delta}");
            assert_eq!(c.automatic_multiplier().to_bits(), 0);
            c.tick(0.05, true);
            assert_eq!(c.fps().to_bits(), following, "{delta}");
            assert_eq!(c.automatic_multiplier().to_bits(), 0);
        }
    }

    #[test]
    fn a_new_controller_starts_at_zero() {
        let mut first = BuildingDegradeController::default();
        for _ in 0..51 {
            first.tick(1.0 / 14.0, true);
        }
        let restarted = BuildingDegradeController::default();
        assert_ne!(first.automatic_multiplier(), 0.0);
        assert_eq!(restarted.automatic_multiplier(), 0.0);
        assert_eq!(restarted.fps(), 0.0);
    }

    #[test]
    fn the_five_weight_formula_matches_the_fixed_vectors() {
        for (fps, expected) in [
            (0.0, -0.15_f32),
            (10.0, 0.0),
            (14.0, 0.005_454_545_4),
            (20.0, 0.1),
            (25.0, 0.1),
        ] {
            assert!(
                (calculate_candidate(fps, 0.0) - expected).abs() < 5e-8,
                "{fps}"
            );
        }
        assert_eq!(calculate_candidate(14.0, 0.0).to_bits(), 0x3BB2_BC0A);
        assert_eq!(calculate_candidate(16.25, 0.0).to_bits(), 0x3CA3_D70A);
    }

    #[test]
    fn stability_uses_the_double_constant() {
        let candidate = f32::from_bits(0x3C13_D70A);
        let prior = -f32::from_bits(0x3A80_0001);
        let history = [prior; 30];
        let difference = (f64::from(prior) - f64::from(candidate)).abs();
        assert!(difference > f64::from(0.01_f32));
        assert!(difference < 0.01);
        assert!(is_candidate_stable(&history, candidate));
    }

    #[test]
    fn a_commit_needs_all_thirty_slots_within_the_band() {
        let mut history = [0.1_f32; 30];
        history[15] = 0.0;
        assert_eq!(
            advance_automatic_multiplier(&mut history, true, 20.0, 0.0),
            0.0
        );
        assert_eq!(history[29], 0.0);
        let mut history = [0.1_f32; 30];
        let committed = advance_automatic_multiplier(&mut history, true, 20.0, 0.0);
        assert!((committed - 0.1).abs() < 1e-7);
        assert!(history.iter().all(|h| (h - 0.1).abs() < 1e-7));
    }

    #[test]
    fn the_clamp_and_the_manual_arm() {
        let mut history = [1.0_f32; 30];
        assert_eq!(
            advance_automatic_multiplier(&mut history, true, 25.0, 0.95),
            1.0
        );
        history[10] = -1.0;
        assert_eq!(
            advance_automatic_multiplier(&mut history, false, 0.0, 0.35),
            0.35
        );
        assert_eq!(history[29], 0.35);
    }
}
