//! Frame diagnostics: the frames-per-second line in the title bar, the
//! optional frame profiler (per-stage CPU time, GPU time, percentiles every
//! five seconds) and the optional frame-history CSV.

use std::fmt::Write as _;
use std::time::{Duration, Instant, SystemTime};

/// The parts of a frame whose CPU time is measured. The order and names are
/// the columns of the frame-history CSV.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FrameStage {
    /// Taking what the session sent (scene deltas, camera follow).
    Update = 0,
    /// Uploading what the loader prepared.
    Upload = 1,
    /// A developer overlay; there is none, so always zero (the column keeps
    /// the history file in OpenAC's shape).
    ImGui = 2,
    /// The software pacing wait (zero with vsync or uncapped).
    Pacing = 3,
    /// Building and recording the world draws.
    Render = 4,
    /// Acquiring the swapchain image, submitting and presenting.
    Present = 5,
    /// After the frame: screenshots and diagnostics publication.
    Diagnostics = 6,
}

/// Number of [`FrameStage`]s.
pub const STAGE_COUNT: usize = 7;

const STAGE_NAMES: [&str; STAGE_COUNT] = ["upd", "upl", "imgui", "pace", "rnd", "pres", "diag"];

/// A ring of the most recent samples with nearest-rank percentiles.
#[derive(Debug, Clone)]
pub struct FrameStatsBuffer {
    samples: Vec<i64>,
    cursor: usize,
    count: usize,
}

impl FrameStatsBuffer {
    /// A buffer of `capacity` samples (at least 1).
    #[must_use]
    pub fn new(capacity: usize) -> Self {
        Self {
            samples: vec![0; capacity.max(1)],
            cursor: 0,
            count: 0,
        }
    }

    /// Samples held.
    #[must_use]
    pub fn len(&self) -> usize {
        self.count
    }

    /// True when empty.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.count == 0
    }

    /// Add a sample, dropping the oldest when full.
    pub fn push(&mut self, value: i64) {
        self.samples[self.cursor] = value;
        self.cursor = (self.cursor + 1) % self.samples.len();
        self.count = (self.count + 1).min(self.samples.len());
    }

    /// Forget every sample.
    pub fn reset(&mut self) {
        self.cursor = 0;
        self.count = 0;
    }

    /// The nearest-rank percentile `q` (0..1); 0 when empty.
    #[must_use]
    pub fn percentile(&self, q: f64) -> i64 {
        if self.count == 0 {
            return 0;
        }
        let mut sorted = self.samples[..self.count].to_vec();
        sorted.sort_unstable();
        let rank = ((q * self.count as f64).ceil() as usize).clamp(1, self.count);
        sorted[rank - 1]
    }

    /// The largest sample; 0 when empty.
    #[must_use]
    pub fn max(&self) -> i64 {
        self.samples[..self.count]
            .iter()
            .copied()
            .max()
            .unwrap_or(0)
    }
}

/// The upper median and 95th percentile of a window of timings, in
/// hundredths of a microsecond.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct RollingPercentiles {
    /// Upper median.
    pub median: i64,
    /// 95th percentile.
    pub p95: i64,
}

/// A fixed window of timing samples where empty slots (zero or negative)
/// do not count.
#[derive(Debug, Clone)]
pub struct RollingTimingWindow {
    samples: Vec<i64>,
    cursor: usize,
}

impl RollingTimingWindow {
    /// A window of `capacity` samples (at least 1).
    #[must_use]
    pub fn new(capacity: usize) -> Self {
        Self {
            samples: vec![0; capacity.max(1)],
            cursor: 0,
        }
    }

    /// Slots in the window.
    #[must_use]
    pub fn capacity(&self) -> usize {
        self.samples.len()
    }

    /// Add a sample in hundredths of a microsecond.
    pub fn push_hundredths_us(&mut self, sample: i64) {
        self.samples[self.cursor] = sample;
        self.cursor = (self.cursor + 1) % self.samples.len();
    }

    /// Add a duration.
    pub fn push(&mut self, duration: Duration) {
        self.push_hundredths_us(i64::try_from(duration.as_nanos() / 10).unwrap_or(i64::MAX));
    }

    /// Percentiles over the populated (positive) samples.
    #[must_use]
    pub fn snapshot(&self) -> RollingPercentiles {
        let mut sorted = self.samples.clone();
        sorted.sort_unstable();
        let populated = sorted.iter().filter(|s| **s > 0).count();
        if populated == 0 {
            return RollingPercentiles::default();
        }
        let len = sorted.len();
        let median = sorted[len - 1 - (populated - 1) / 2];
        let p95_offset = ((populated - 1) as f64 * 0.05) as usize;
        RollingPercentiles {
            median,
            p95: sorted[len - 1 - p95_offset],
        }
    }
}

/// One frame of the history CSV.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FrameRecord {
    /// Frame number since the profiler started.
    pub frame: u64,
    /// Milliseconds since the profiler started.
    pub timestamp_ms: f64,
    /// Wall time between frame boundaries, microseconds.
    pub cpu_us: i64,
    /// GPU time of the frame's draws, microseconds; -1 when not measured.
    pub gpu_us: i64,
    /// Per-stage CPU time, microseconds, in [`FrameStage`] order.
    pub stages_us: [i64; STAGE_COUNT],
}

const WINDOW_CAPACITY: usize = 2048;
const REPORT_INTERVAL: Duration = Duration::from_secs(5);

/// The frame profiler. Off unless enabled; when off every call is a
/// branch.
#[derive(Debug)]
pub struct FrameProfiler {
    enabled: bool,
    history: Option<Vec<FrameRecord>>,
    started: Instant,
    started_wall: SystemTime,
    cpu_us: FrameStatsBuffer,
    gpu_us: FrameStatsBuffer,
    stage_us: Vec<FrameStatsBuffer>,
    stage_accum: [Duration; STAGE_COUNT],
    last_stage_us: [i64; STAGE_COUNT],
    last_boundary: Option<Instant>,
    frame_index: u64,
    frames_in_window: u32,
    last_report: Instant,
    gpu_active: bool,
    last_report_line: Option<String>,
}

impl FrameProfiler {
    /// A profiler; `enabled` turns on measurement and the five-second
    /// report, `keep_history` also keeps every frame for the CSV.
    #[must_use]
    pub fn new(enabled: bool, keep_history: bool) -> Self {
        let now = Instant::now();
        Self {
            enabled: enabled || keep_history,
            history: keep_history.then(Vec::new),
            started: now,
            started_wall: SystemTime::now(),
            cpu_us: FrameStatsBuffer::new(WINDOW_CAPACITY),
            gpu_us: FrameStatsBuffer::new(WINDOW_CAPACITY),
            stage_us: (0..STAGE_COUNT)
                .map(|_| FrameStatsBuffer::new(WINDOW_CAPACITY))
                .collect(),
            stage_accum: [Duration::ZERO; STAGE_COUNT],
            last_stage_us: [0; STAGE_COUNT],
            last_boundary: None,
            frame_index: 0,
            frames_in_window: 0,
            last_report: now,
            gpu_active: false,
            last_report_line: None,
        }
    }

    /// Whether anything is measured.
    #[must_use]
    pub fn is_enabled(&self) -> bool {
        self.enabled
    }

    /// The frame being measured.
    #[must_use]
    pub fn frame_index(&self) -> u64 {
        self.frame_index
    }

    /// Start timing a stage; hand the token back to [`FrameProfiler::end`].
    #[must_use]
    pub fn begin(&self, stage: FrameStage) -> Option<(FrameStage, Instant)> {
        self.enabled.then(|| (stage, Instant::now()))
    }

    /// Stop timing a stage started with [`FrameProfiler::begin`].
    pub fn end(&mut self, token: Option<(FrameStage, Instant)>) {
        if let Some((stage, start)) = token {
            self.stage_accum[stage as usize] += start.elapsed();
        }
    }

    /// Add time measured elsewhere to a stage.
    pub fn add(&mut self, stage: FrameStage, duration: Duration) {
        if self.enabled {
            self.stage_accum[stage as usize] += duration;
        }
    }

    /// Microseconds a stage took in the last closed frame.
    #[must_use]
    pub fn last_stage_us(&self, stage: FrameStage) -> i64 {
        self.last_stage_us[stage as usize]
    }

    /// The last five-second report line.
    #[must_use]
    pub fn last_report(&self) -> Option<&str> {
        self.last_report_line.as_deref()
    }

    /// Close the frame at `now`: record its time and stages, and every five
    /// seconds return the report line.
    pub fn frame_boundary(&mut self, now: Instant) -> Option<String> {
        if !self.enabled {
            return None;
        }
        let Some(previous) = self.last_boundary.replace(now) else {
            self.stage_accum = [Duration::ZERO; STAGE_COUNT];
            self.last_report = now;
            return None;
        };
        let cpu_us = micros(now.saturating_duration_since(previous));
        self.cpu_us.push(cpu_us);
        for (i, accum) in self.stage_accum.iter_mut().enumerate() {
            let us = micros(*accum);
            self.stage_us[i].push(us);
            self.last_stage_us[i] = us;
            *accum = Duration::ZERO;
        }
        self.frames_in_window += 1;
        if let Some(history) = &mut self.history {
            history.push(FrameRecord {
                frame: self.frame_index,
                timestamp_ms: now.saturating_duration_since(self.started).as_secs_f64() * 1000.0,
                cpu_us,
                gpu_us: -1,
                stages_us: self.last_stage_us,
            });
        }
        self.frame_index += 1;
        if now.saturating_duration_since(self.last_report) < REPORT_INTERVAL {
            return None;
        }
        let line = format_report(
            self.frames_in_window,
            &self.cpu_us,
            self.gpu_active.then_some(&self.gpu_us),
            &self.stage_us,
        );
        self.last_report = now;
        self.frames_in_window = 0;
        self.cpu_us.reset();
        self.gpu_us.reset();
        for s in &mut self.stage_us {
            s.reset();
        }
        self.last_report_line = Some(line.clone());
        Some(line)
    }

    /// The GPU time of frame `frame` arrived (a few frames late).
    pub fn record_gpu(&mut self, frame: u64, elapsed: Duration) {
        if !self.enabled {
            return;
        }
        self.gpu_active = true;
        let us = micros(elapsed);
        self.gpu_us.push(us);
        if let Some(row) = self
            .history
            .as_mut()
            .and_then(|h| usize::try_from(frame).ok().and_then(|i| h.get_mut(i)))
        {
            row.gpu_us = us;
        }
    }

    /// The history as CSV, `None` when history is off or empty.
    #[must_use]
    pub fn history_csv(&self) -> Option<String> {
        let history = self.history.as_ref().filter(|h| !h.is_empty())?;
        Some(history_csv(history, self.started_wall))
    }
}

fn micros(d: Duration) -> i64 {
    i64::try_from(d.as_micros()).unwrap_or(i64::MAX)
}

/// The five-second report line.
#[must_use]
pub fn format_report(
    frames: u32,
    cpu: &FrameStatsBuffer,
    gpu: Option<&FrameStatsBuffer>,
    stages: &[FrameStatsBuffer],
) -> String {
    let ms = |us: i64| us as f64 / 1000.0;
    let mut line = format!(
        "[frame-prof] n={frames} | cpu_ms p50={:.1} p95={:.1} p99={:.1} max={:.1}",
        ms(cpu.percentile(0.50)),
        ms(cpu.percentile(0.95)),
        ms(cpu.percentile(0.99)),
        ms(cpu.max())
    );
    match gpu {
        Some(gpu) => {
            let _ = write!(
                line,
                " | gpu_ms p50={:.1} p95={:.1}",
                ms(gpu.percentile(0.50)),
                ms(gpu.percentile(0.95))
            );
        }
        None => line.push_str(" | gpu=off"),
    }
    for (name, stage) in STAGE_NAMES.iter().zip(stages) {
        let _ = write!(
            line,
            " | {name} p50={:.1} p95={:.1}",
            ms(stage.percentile(0.50)),
            ms(stage.percentile(0.95))
        );
    }
    line
}

/// The frame history as CSV: a header, then one row per frame with its
/// time since the profiler started and as a UTC instant.
#[must_use]
pub fn history_csv(records: &[FrameRecord], started: SystemTime) -> String {
    let mut out = String::from(
        "frame,timestamp_ms,timestamp_utc,cpu_us,gpu_us,alloc_bytes,\
         update_us,upload_us,imgui_us,pacing_us,render_us,present_us,diagnostics_us\n",
    );
    for r in records {
        let at = started + Duration::from_secs_f64(r.timestamp_ms.max(0.0) / 1000.0);
        let _ = write!(
            out,
            "{},{:.3},{},{},{},0",
            r.frame,
            r.timestamp_ms,
            utc_iso8601(at),
            r.cpu_us,
            r.gpu_us
        );
        for us in r.stages_us {
            let _ = write!(out, ",{us}");
        }
        out.push('\n');
    }
    out
}

/// `t` as an ISO 8601 UTC timestamp with seven fractional digits.
#[must_use]
pub fn utc_iso8601(t: SystemTime) -> String {
    let since = t
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or(Duration::ZERO);
    let secs = since.as_secs();
    let (days, rem) = (secs / 86_400, secs % 86_400);
    let (year, month, day) = civil_from_days(days as i64);
    format!(
        "{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}.{:07}Z",
        rem / 3600,
        rem % 3600 / 60,
        rem % 60,
        since.subsec_nanos() / 100
    )
}

/// Days since 1970-01-01 to a calendar date (proleptic Gregorian).
fn civil_from_days(days: i64) -> (i64, u32, u32) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let month = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    let year = yoe + era * 400 + i64::from(month <= 2);
    (year, month, day)
}

/// The title bar's frame rate, refreshed twice a second.
#[derive(Debug, Default)]
pub struct FrameRateTitle {
    elapsed: f64,
    frames: u32,
}

/// How often the title's frame rate is refreshed, seconds.
pub const TITLE_INTERVAL_SECONDS: f64 = 0.5;

impl FrameRateTitle {
    /// A frame of `delta_seconds` was presented; every half second returns
    /// the frame rate and the mean frame time in milliseconds.
    pub fn frame(&mut self, delta_seconds: f64) -> Option<(f64, f64)> {
        self.elapsed += delta_seconds;
        self.frames += 1;
        if self.elapsed < TITLE_INTERVAL_SECONDS {
            return None;
        }
        let fps = f64::from(self.frames) / self.elapsed;
        let mean_ms = self.elapsed / f64::from(self.frames) * 1000.0;
        self.elapsed = 0.0;
        self.frames = 0;
        Some((fps, mean_ms))
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.App.Tests/Rendering/RollingTimingSampleWindowTests.cs`
    //! and the `FrameProfiler` / `FrameStatsBuffer` / `RenderFrameDiagnosticsController`
    //! behaviour.
    use super::*;

    #[test]
    fn stats_buffer_uses_nearest_rank_percentiles() {
        let mut b = FrameStatsBuffer::new(4);
        assert_eq!((b.percentile(0.5), b.max()), (0, 0));
        for v in [40, 10, 30, 20] {
            b.push(v);
        }
        assert_eq!(b.percentile(0.5), 20);
        assert_eq!(b.percentile(0.95), 40);
        assert_eq!(b.percentile(0.0), 10);
        assert_eq!(b.max(), 40);
        b.push(5); // overwrites the oldest (40)
        assert_eq!(b.max(), 30);
        b.reset();
        assert!(b.is_empty());
    }

    #[test]
    fn rolling_window_ignores_empty_samples() {
        let mut w = RollingTimingWindow::new(4);
        w.push_hundredths_us(0);
        w.push_hundredths_us(-10);
        assert_eq!(w.snapshot(), RollingPercentiles::default());
    }

    #[test]
    fn rolling_window_uses_the_upper_median_and_p95() {
        let mut w = RollingTimingWindow::new(8);
        w.push_hundredths_us(100);
        w.push_hundredths_us(200);
        assert_eq!(
            w.snapshot(),
            RollingPercentiles {
                median: 200,
                p95: 200
            }
        );
    }

    #[test]
    fn rolling_window_wraps_and_keeps_the_newest() {
        let mut w = RollingTimingWindow::new(3);
        for v in [100, 200, 300, 400] {
            w.push_hundredths_us(v);
        }
        assert_eq!(w.capacity(), 3);
        assert_eq!(
            w.snapshot(),
            RollingPercentiles {
                median: 300,
                p95: 400
            }
        );
    }

    #[test]
    fn rolling_window_takes_durations_in_hundredths_of_a_microsecond() {
        let mut w = RollingTimingWindow::new(1);
        w.push(Duration::from_secs(1));
        assert_eq!(
            w.snapshot(),
            RollingPercentiles {
                median: 100_000_000,
                p95: 100_000_000
            }
        );
    }

    #[test]
    fn a_disabled_profiler_records_nothing() {
        let mut p = FrameProfiler::new(false, false);
        let t = Instant::now();
        assert!(p.begin(FrameStage::Render).is_none());
        assert_eq!(p.frame_boundary(t), None);
        assert_eq!(p.frame_boundary(t + Duration::from_secs(6)), None);
        assert_eq!(p.history_csv(), None);
    }

    #[test]
    fn the_profiler_attributes_stages_and_reports_every_five_seconds() {
        let mut p = FrameProfiler::new(true, true);
        let t0 = Instant::now();
        assert_eq!(p.frame_boundary(t0), None);
        p.add(FrameStage::Render, Duration::from_millis(3));
        p.add(FrameStage::Pacing, Duration::from_millis(10));
        assert_eq!(p.frame_boundary(t0 + Duration::from_millis(16)), None);
        assert_eq!(p.last_stage_us(FrameStage::Render), 3000);
        assert_eq!(p.last_stage_us(FrameStage::Pacing), 10_000);
        p.record_gpu(0, Duration::from_micros(2500));
        let line = p
            .frame_boundary(t0 + Duration::from_secs(6))
            .expect("five seconds passed");
        assert!(line.starts_with("[frame-prof] n=2 | cpu_ms"));
        assert!(line.contains("gpu_ms"));
        assert!(line.contains("| rnd p50="));
        let csv = p.history_csv().unwrap();
        let mut rows = csv.lines();
        assert_eq!(
            rows.next().unwrap(),
            "frame,timestamp_ms,timestamp_utc,cpu_us,gpu_us,alloc_bytes,update_us,upload_us,\
             imgui_us,pacing_us,render_us,present_us,diagnostics_us"
        );
        let first: Vec<&str> = rows.next().unwrap().split(',').collect();
        assert_eq!(first[0], "0");
        assert_eq!(first[3], "16000");
        assert_eq!(first[4], "2500");
        assert_eq!(first[9], "10000");
        assert_eq!(first[10], "3000");
    }

    #[test]
    fn utc_timestamps_are_iso_8601() {
        let t = SystemTime::UNIX_EPOCH + Duration::from_millis(1_760_270_400_123);
        assert_eq!(utc_iso8601(t), "2025-10-12T12:00:00.1230000Z");
        assert_eq!(
            utc_iso8601(SystemTime::UNIX_EPOCH),
            "1970-01-01T00:00:00.0000000Z"
        );
    }

    #[test]
    fn the_title_rate_is_published_every_half_second() {
        let mut t = FrameRateTitle::default();
        // Eighths of a second add up exactly.
        for _ in 0..3 {
            assert_eq!(t.frame(0.125), None);
        }
        let (fps, ms) = t.frame(0.125).unwrap();
        assert_eq!(fps, 8.0);
        assert_eq!(ms, 125.0);
        // The count starts again after a refresh.
        assert_eq!(t.frame(0.25), None);
        assert_eq!(t.frame(0.25), Some((4.0, 250.0)));
    }
}
