//! Automatic quality: the atmospheric rendering steps between Low, Medium
//! and High by its measured cost, slowly and with a cool-down so it does
//! not flicker between them, and gives up (back to the plain picture) when
//! even Low stays over its budget.

use ac_host::settings::render_pack::{AtmosphericPreset, QualitySemantic, preset_for};

use crate::profiler::FrameStatsBuffer;

/// A quality level automatic quality moves between.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum QualityLevel {
    /// Low.
    Low = 0,
    /// Medium.
    Medium = 1,
    /// High.
    High = 2,
}

impl QualityLevel {
    /// The preset the level runs.
    #[must_use]
    pub fn preset(self) -> &'static AtmosphericPreset {
        preset_for(match self {
            Self::Low => QualitySemantic::Low,
            Self::Medium => QualitySemantic::Medium,
            Self::High => QualitySemantic::High,
        })
    }

    /// The level of a Low, Medium or High preset.
    #[must_use]
    pub fn of(semantic: QualitySemantic) -> Option<Self> {
        match semantic {
            QualitySemantic::Low => Some(Self::Low),
            QualitySemantic::Medium => Some(Self::Medium),
            QualitySemantic::High => Some(Self::High),
            QualitySemantic::Automatic => None,
        }
    }

    fn down(self) -> Self {
        match self {
            Self::High => Self::Medium,
            _ => Self::Low,
        }
    }

    fn up(self) -> Self {
        match self {
            Self::Low => Self::Medium,
            _ => Self::High,
        }
    }
}

/// A level's budget: the most added GPU and CPU time (99th percentile,
/// milliseconds) and video memory.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct QualityBudget {
    /// GPU time, milliseconds.
    pub gpu_ms_p99: f64,
    /// CPU time, milliseconds.
    pub cpu_ms_p99: f64,
    /// Video memory, bytes.
    pub resident_gpu_bytes: u64,
}

impl QualityBudget {
    /// The budget a preset declares.
    #[must_use]
    pub fn of(preset: &AtmosphericPreset) -> Self {
        Self {
            gpu_ms_p99: preset.gpu_p99_ms,
            cpu_ms_p99: preset.cpu_p99_ms,
            resident_gpu_bytes: preset.max_resident_bytes,
        }
    }
}

/// One measurement the controller judges.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct QualityMeasurement {
    /// GPU time of the atmospheric frame, 99th percentile, milliseconds.
    pub gpu_ms_p99: f64,
    /// Added CPU time, 99th percentile, milliseconds.
    pub cpu_ms_p99: f64,
    /// Video memory, bytes.
    pub resident_gpu_bytes: u64,
    /// Taken at a settled frame (no resize or level change in it).
    pub stable: bool,
}

/// What the controller decided so far.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AutoQualitySnapshot {
    /// The level it runs.
    pub current: QualityLevel,
    /// Over-budget samples in a row.
    pub consecutive_over_budget: u32,
    /// Samples in a row with room for the next level.
    pub consecutive_headroom: u32,
    /// Samples left before it judges again after a change.
    pub cooldown_remaining: u32,
    /// Changes so far.
    pub change_generation: u64,
    /// Low itself stayed over budget: turn the atmospheric rendering off.
    pub fallback_to_plain: bool,
}

/// The automatic quality controller (OpenAC `AtmosphericAutoQualityController`).
#[derive(Debug, Clone)]
pub struct AutoQualityController {
    current: QualityLevel,
    minimum: QualityLevel,
    maximum: QualityLevel,
    budgets: [QualityBudget; 3],
    over_budget: u32,
    headroom: u32,
    cooldown: u32,
    generation: u64,
    fallback: bool,
}

impl AutoQualityController {
    /// Over-budget samples in a row before stepping down.
    pub const DOWNGRADE_HYSTERESIS_FRAMES: u32 = 180;
    /// Samples in a row with room before stepping up.
    pub const UPGRADE_HYSTERESIS_FRAMES: u32 = 900;
    /// Samples ignored after a change.
    pub const CHANGE_COOLDOWN_FRAMES: u32 = 300;

    /// A controller over the presets' budgets, starting at `initial`
    /// within `minimum..=maximum`.
    #[must_use]
    pub fn new(initial: QualityLevel, minimum: QualityLevel, maximum: QualityLevel) -> Self {
        Self::with_budgets(
            [
                QualityBudget::of(QualityLevel::Low.preset()),
                QualityBudget::of(QualityLevel::Medium.preset()),
                QualityBudget::of(QualityLevel::High.preset()),
            ],
            initial,
            minimum,
            maximum,
        )
    }

    /// A controller over given Low, Medium and High budgets.
    #[must_use]
    pub fn with_budgets(
        budgets: [QualityBudget; 3],
        initial: QualityLevel,
        minimum: QualityLevel,
        maximum: QualityLevel,
    ) -> Self {
        Self {
            current: initial.clamp(minimum, maximum),
            minimum,
            maximum,
            budgets,
            over_budget: 0,
            headroom: 0,
            cooldown: 0,
            generation: 0,
            fallback: false,
        }
    }

    /// Where it stands.
    #[must_use]
    pub fn snapshot(&self) -> AutoQualitySnapshot {
        AutoQualitySnapshot {
            current: self.current,
            consecutive_over_budget: self.over_budget,
            consecutive_headroom: self.headroom,
            cooldown_remaining: self.cooldown,
            change_generation: self.generation,
            fallback_to_plain: self.fallback,
        }
    }

    /// The current level's budget.
    #[must_use]
    pub fn current_budget(&self) -> QualityBudget {
        self.budgets[self.current as usize]
    }

    /// Judge one measurement.
    pub fn observe(&mut self, m: &QualityMeasurement) -> AutoQualitySnapshot {
        if !m.stable || self.fallback {
            return self.snapshot();
        }
        if self.cooldown > 0 {
            self.cooldown -= 1;
            self.over_budget = 0;
            self.headroom = 0;
            return self.snapshot();
        }
        let budget = self.budgets[self.current as usize];
        let over = m.gpu_ms_p99 > budget.gpu_ms_p99
            || m.cpu_ms_p99 > budget.cpu_ms_p99
            || m.resident_gpu_bytes > budget.resident_gpu_bytes;
        if over {
            self.over_budget += 1;
            self.headroom = 0;
            if self.over_budget >= Self::DOWNGRADE_HYSTERESIS_FRAMES {
                if self.current == self.minimum {
                    self.over_budget = Self::DOWNGRADE_HYSTERESIS_FRAMES;
                    self.headroom = 0;
                    self.cooldown = 0;
                    self.fallback = true;
                    self.generation += 1;
                } else {
                    self.change(self.current.down());
                }
            }
            return self.snapshot();
        }
        self.over_budget = 0;
        if self.current == self.maximum {
            self.headroom = 0;
            return self.snapshot();
        }
        let next = self.budgets[self.current.up() as usize];
        let room = m.gpu_ms_p99 <= next.gpu_ms_p99 * 0.70
            && m.cpu_ms_p99 <= next.cpu_ms_p99 * 0.70
            && m.resident_gpu_bytes <= (next.resident_gpu_bytes as f64 * 0.70) as u64;
        if !room {
            self.headroom = 0;
            return self.snapshot();
        }
        self.headroom += 1;
        if self.headroom >= Self::UPGRADE_HYSTERESIS_FRAMES {
            self.change(self.current.up());
        }
        self.snapshot()
    }

    fn change(&mut self, level: QualityLevel) {
        self.current = level;
        self.over_budget = 0;
        self.headroom = 0;
        self.cooldown = Self::CHANGE_COOLDOWN_FRAMES;
        self.generation += 1;
    }
}

/// The percentiles of a rolling window of measurements.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct PerformanceSnapshot {
    /// CPU samples held.
    pub cpu_samples: usize,
    /// GPU samples held.
    pub gpu_samples: usize,
    /// Added CPU time, milliseconds, 50th, 95th, 99th percentile.
    pub cpu_ms: [f64; 3],
    /// GPU time, milliseconds, 50th, 95th, 99th percentile.
    pub gpu_ms: [f64; 3],
    /// Video memory at the last sample, bytes.
    pub resident_gpu_bytes: u64,
}

/// A rolling window of the atmospheric frame's cost (OpenAC
/// `RenderPackPerformanceWindow`, without its receiver-CPU column).
#[derive(Debug, Clone)]
pub struct PerformanceWindow {
    cpu_us: FrameStatsBuffer,
    gpu_us: FrameStatsBuffer,
    resident: u64,
}

impl Default for PerformanceWindow {
    fn default() -> Self {
        Self::new(Self::DEFAULT_CAPACITY)
    }
}

impl PerformanceWindow {
    /// Samples kept.
    pub const DEFAULT_CAPACITY: usize = 2048;

    /// A window of `capacity` samples.
    #[must_use]
    pub fn new(capacity: usize) -> Self {
        Self {
            cpu_us: FrameStatsBuffer::new(capacity),
            gpu_us: FrameStatsBuffer::new(capacity),
            resident: 0,
        }
    }

    /// Add a frame: its added CPU time, its GPU time when measured, and the
    /// video memory held. Non-finite or negative values are refused.
    pub fn observe(&mut self, cpu_ms: f64, gpu_ms: Option<f64>, resident_gpu_bytes: u64) -> bool {
        if !cpu_ms.is_finite() || cpu_ms < 0.0 {
            return false;
        }
        if let Some(g) = gpu_ms
            && (!g.is_finite() || g < 0.0)
        {
            return false;
        }
        self.cpu_us.push((cpu_ms * 1000.0).round() as i64);
        if let Some(g) = gpu_ms {
            self.gpu_us.push((g * 1000.0).round() as i64);
        }
        self.resident = resident_gpu_bytes;
        true
    }

    /// The window's percentiles.
    #[must_use]
    pub fn snapshot(&self) -> PerformanceSnapshot {
        let ms = |b: &FrameStatsBuffer, q| b.percentile(q) as f64 / 1000.0;
        PerformanceSnapshot {
            cpu_samples: self.cpu_us.len(),
            gpu_samples: self.gpu_us.len(),
            cpu_ms: [
                ms(&self.cpu_us, 0.50),
                ms(&self.cpu_us, 0.95),
                ms(&self.cpu_us, 0.99),
            ],
            gpu_ms: [
                ms(&self.gpu_us, 0.50),
                ms(&self.gpu_us, 0.95),
                ms(&self.gpu_us, 0.99),
            ],
            resident_gpu_bytes: self.resident,
        }
    }

    /// Forget every sample (after a resize or a level change).
    pub fn reset(&mut self) {
        self.cpu_us.reset();
        self.gpu_us.reset();
        self.resident = 0;
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/Rendering/Packs/AtmosphericAutoQualityControllerTests.cs`
    //! and `RenderPackPerformanceWindowTests.cs`.
    use super::*;

    const MIB: u64 = 1024 * 1024;

    fn high() -> AutoQualityController {
        AutoQualityController::new(QualityLevel::High, QualityLevel::Low, QualityLevel::High)
    }

    #[test]
    fn downgrade_requires_a_long_consecutive_over_budget_window() {
        let mut c = high();
        let m = QualityMeasurement {
            gpu_ms_p99: 7.0,
            cpu_ms_p99: 1.1,
            resident_gpu_bytes: 250 * MIB,
            stable: true,
        };
        for _ in 0..AutoQualityController::DOWNGRADE_HYSTERESIS_FRAMES - 1 {
            c.observe(&m);
        }
        assert_eq!(c.snapshot().current, QualityLevel::High);
        let changed = c.observe(&m);
        assert_eq!(changed.current, QualityLevel::Medium);
        assert_eq!(
            changed.cooldown_remaining,
            AutoQualityController::CHANGE_COOLDOWN_FRAMES
        );
    }

    #[test]
    fn unstable_frames_never_advance_hysteresis() {
        let mut c = high();
        let m = QualityMeasurement {
            gpu_ms_p99: 20.0,
            cpu_ms_p99: 20.0,
            resident_gpu_bytes: 0,
            stable: false,
        };
        for _ in 0..1000 {
            c.observe(&m);
        }
        assert_eq!(c.snapshot().current, QualityLevel::High);
        assert_eq!(c.snapshot().consecutive_over_budget, 0);
    }

    #[test]
    fn upgrade_requires_nine_hundred_headroom_frames() {
        let mut c =
            AutoQualityController::new(QualityLevel::Low, QualityLevel::Low, QualityLevel::High);
        let m = QualityMeasurement {
            gpu_ms_p99: 0.5,
            cpu_ms_p99: 0.1,
            resident_gpu_bytes: 16 * MIB,
            stable: true,
        };
        for _ in 0..AutoQualityController::UPGRADE_HYSTERESIS_FRAMES - 1 {
            c.observe(&m);
        }
        assert_eq!(c.snapshot().current, QualityLevel::Low);
        assert_eq!(c.observe(&m).current, QualityLevel::Medium);
    }

    #[test]
    fn low_over_budget_asks_to_fall_back_to_the_plain_picture() {
        let mut c =
            AutoQualityController::new(QualityLevel::Low, QualityLevel::Low, QualityLevel::High);
        let m = QualityMeasurement {
            gpu_ms_p99: 100.0,
            cpu_ms_p99: 100.0,
            resident_gpu_bytes: u64::MAX,
            stable: true,
        };
        for _ in 0..AutoQualityController::DOWNGRADE_HYSTERESIS_FRAMES - 1 {
            c.observe(&m);
        }
        assert_eq!(c.snapshot().current, QualityLevel::Low);
        assert!(!c.snapshot().fallback_to_plain);
        let fallback = c.observe(&m);
        assert_eq!(fallback.current, QualityLevel::Low);
        assert!(fallback.fallback_to_plain);
    }

    #[test]
    fn the_declared_budgets_are_the_authority() {
        let budgets = [
            QualityBudget {
                gpu_ms_p99: 0.5,
                cpu_ms_p99: 0.1,
                resident_gpu_bytes: 16 * MIB,
            },
            QualityBudget {
                gpu_ms_p99: 1.0,
                cpu_ms_p99: 0.2,
                resident_gpu_bytes: 32 * MIB,
            },
            QualityBudget {
                gpu_ms_p99: 1.5,
                cpu_ms_p99: 0.3,
                resident_gpu_bytes: 64 * MIB,
            },
        ];
        let mut c = AutoQualityController::with_budgets(
            budgets,
            QualityLevel::High,
            QualityLevel::Low,
            QualityLevel::High,
        );
        let m = QualityMeasurement {
            gpu_ms_p99: 1.6,
            cpu_ms_p99: 0.1,
            resident_gpu_bytes: 8 * MIB,
            stable: true,
        };
        for _ in 0..AutoQualityController::DOWNGRADE_HYSTERESIS_FRAMES {
            c.observe(&m);
        }
        assert_eq!(c.snapshot().current, QualityLevel::Medium);
    }

    #[test]
    fn the_window_reports_cpu_and_delayed_gpu_percentiles() {
        let mut w = PerformanceWindow::new(8);
        assert!(w.observe(1.0, None, 10));
        assert!(w.observe(2.0, Some(4.0), 11));
        assert!(w.observe(3.0, Some(6.0), 12));
        assert!(w.observe(4.0, Some(8.0), 13));
        let s = w.snapshot();
        assert_eq!((s.cpu_samples, s.gpu_samples), (4, 3));
        assert_eq!(s.cpu_ms, [2.0, 4.0, 4.0]);
        assert_eq!(s.gpu_ms, [6.0, 8.0, 8.0]);
        assert_eq!(s.resident_gpu_bytes, 13);
    }

    #[test]
    fn the_window_rolls_and_resets() {
        let mut w = PerformanceWindow::new(3);
        for i in 1..=4 {
            w.observe(f64::from(i), Some(f64::from(i * 2)), i as u64);
        }
        let s = w.snapshot();
        assert_eq!((s.cpu_samples, s.gpu_samples), (3, 3));
        assert_eq!(s.cpu_ms[0], 3.0);
        assert_eq!(s.cpu_ms[2], 4.0);
        assert_eq!(s.gpu_ms[0], 6.0);
        assert_eq!(s.gpu_ms[2], 8.0);
        w.reset();
        assert_eq!(w.snapshot(), PerformanceSnapshot::default());
    }

    #[test]
    fn invalid_measurements_are_refused() {
        let mut w = PerformanceWindow::default();
        assert!(!w.observe(-1.0, None, 0));
        assert!(!w.observe(f64::NAN, None, 0));
        assert!(!w.observe(0.0, Some(-1.0), 0));
        assert!(!w.observe(0.0, Some(f64::INFINITY), 0));
        assert_eq!(w.snapshot().cpu_samples, 0);
    }
}
