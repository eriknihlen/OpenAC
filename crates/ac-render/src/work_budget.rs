//! The streaming work budget: how much loaded work the window adopts in one
//! frame, in time and in six counted dimensions (completions, CPU bytes,
//! entity operations, GPU upload bytes, retire operations), with a share
//! kept for the destination while a reveal waits on it.
//!
//! A meter is restarted every frame with that frame's budget; each piece of
//! work reserves its cost first and completes (or fails) after. The first
//! piece of a frame always runs, even when it alone exceeds a limit
//! (it "progresses oversized"), so streaming never stalls; any later piece
//! that would cross a limit yields to the next frame.

use std::time::{Duration, Instant};

/// One mebibyte.
pub const MIB: i64 = 1024 * 1024;

/// 100-nanosecond ticks per second (the budget's time unit).
const TICKS_PER_SECOND: i64 = 10_000_000;

/// What went wrong with a budget or a meter.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum WorkBudgetError {
    /// A budget dimension is not positive, or the reserve fraction is not
    /// strictly between 0 and 1.
    #[error("the streaming work budget is not a valid complete profile")]
    InvalidBudget,
    /// A cost dimension is negative.
    #[error("a streaming work cost is negative")]
    NegativeCost,
    /// A piece of work was reserved while another is still running.
    #[error("the prior streaming operation has not completed")]
    ReservationActive,
    /// Complete or fail without a reservation.
    #[error("no streaming operation is reserved")]
    NoReservation,
    /// A legacy completion count of zero or less.
    #[error("the completion count must be positive")]
    InvalidCompletionCount,
}

/// One frame's work limits.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct StreamingWorkBudget {
    max_update_ticks: i64,
    max_completion_admissions: i32,
    max_adopted_cpu_bytes: i64,
    max_entity_operations: i32,
    max_gpu_upload_bytes: i64,
    max_retire_operations: i32,
    destination_reserve_fraction: f32,
}

impl StreamingWorkBudget {
    /// A budget; every dimension positive and the reserve fraction in
    /// (0, 1).
    pub fn new(
        max_update_time: Duration,
        max_completion_admissions: i32,
        max_adopted_cpu_bytes: i64,
        max_entity_operations: i32,
        max_gpu_upload_bytes: i64,
        max_retire_operations: i32,
        destination_reserve_fraction: f32,
    ) -> Result<Self, WorkBudgetError> {
        let ticks = i64::try_from(max_update_time.as_nanos() / 100).unwrap_or(i64::MAX);
        let budget = Self {
            max_update_ticks: ticks,
            max_completion_admissions,
            max_adopted_cpu_bytes,
            max_entity_operations,
            max_gpu_upload_bytes,
            max_retire_operations,
            destination_reserve_fraction,
        };
        budget.validate().map(|()| budget)
    }

    fn validate(&self) -> Result<(), WorkBudgetError> {
        let f = self.destination_reserve_fraction;
        if self.max_update_ticks <= 0
            || self.max_completion_admissions <= 0
            || self.max_adopted_cpu_bytes <= 0
            || self.max_entity_operations <= 0
            || self.max_gpu_upload_bytes <= 0
            || self.max_retire_operations <= 0
            || !f.is_finite()
            || f <= 0.0
            || f >= 1.0
        {
            return Err(WorkBudgetError::InvalidBudget);
        }
        Ok(())
    }

    /// The time limit.
    #[must_use]
    pub fn max_update_time(&self) -> Duration {
        Duration::from_nanos(u64::try_from(self.max_update_ticks).unwrap_or(0) * 100)
    }

    /// Completed loads adopted per frame.
    #[must_use]
    pub fn max_completion_admissions(&self) -> i32 {
        self.max_completion_admissions
    }

    /// CPU bytes adopted per frame.
    #[must_use]
    pub fn max_adopted_cpu_bytes(&self) -> i64 {
        self.max_adopted_cpu_bytes
    }

    /// Entity operations per frame.
    #[must_use]
    pub fn max_entity_operations(&self) -> i32 {
        self.max_entity_operations
    }

    /// GPU upload bytes per frame.
    #[must_use]
    pub fn max_gpu_upload_bytes(&self) -> i64 {
        self.max_gpu_upload_bytes
    }

    /// GPU resource retirements per frame.
    #[must_use]
    pub fn max_retire_operations(&self) -> i32 {
        self.max_retire_operations
    }

    /// The destination's share while a reveal waits on it.
    #[must_use]
    pub fn destination_reserve_fraction(&self) -> f32 {
        self.destination_reserve_fraction
    }

    /// The budget while a reveal holds for its destination: every dimension
    /// scaled up to the `ceiling_ms` time limit, the extra going to the
    /// destination (the ordinary share stays what it was). No change when
    /// the ceiling is not above the current limit or is not a number.
    #[must_use]
    pub fn widen_for_destination_hold(&self, ceiling_ms: f64) -> Self {
        if !ceiling_ms.is_finite() || ceiling_ms <= 0.0 {
            return *self;
        }
        let current_ms = self.max_update_ticks as f64 * 1000.0 / TICKS_PER_SECOND as f64;
        let scale = ceiling_ms / current_ms;
        if scale <= 1.0 {
            return *self;
        }
        let mut widened_reserve =
            (1.0 - (1.0 - f64::from(self.destination_reserve_fraction)) / scale) as f32;
        if widened_reserve >= 1.0 {
            widened_reserve = f32::from_bits(1.0f32.to_bits() - 1);
        }
        Self {
            max_update_ticks: (ceiling_ms * TICKS_PER_SECOND as f64 / 1000.0).round() as i64,
            max_completion_admissions: scale_i32(self.max_completion_admissions, scale),
            max_adopted_cpu_bytes: scale_i64(self.max_adopted_cpu_bytes, scale),
            max_entity_operations: scale_i32(self.max_entity_operations, scale),
            max_gpu_upload_bytes: scale_i64(self.max_gpu_upload_bytes, scale),
            max_retire_operations: scale_i32(self.max_retire_operations, scale),
            destination_reserve_fraction: widened_reserve,
        }
    }
}

fn scale_i32(value: i32, scale: f64) -> i32 {
    if scale >= f64::from(i32::MAX) / f64::from(value) {
        return i32::MAX;
    }
    ((f64::from(value) * scale).round() as i32).max(1)
}

fn scale_i64(value: i64, scale: f64) -> i64 {
    if scale >= i64::MAX as f64 / value as f64 {
        return i64::MAX;
    }
    ((value as f64 * scale).round() as i64).max(1)
}

/// The authored budget profile, before a quality preset scales it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct StreamingWorkBudgetOptions {
    /// Time limit, milliseconds.
    pub max_update_ms: f64,
    /// Completed loads adopted per frame.
    pub max_completion_admissions: i32,
    /// CPU bytes adopted per frame.
    pub max_adopted_cpu_bytes: i64,
    /// Entity operations per frame.
    pub max_entity_operations: i32,
    /// GPU upload bytes per frame.
    pub max_gpu_upload_bytes: i64,
    /// GPU resource retirements per frame.
    pub max_retire_operations: i32,
    /// The destination's share while a reveal waits on it.
    pub destination_reserve_fraction: f32,
    /// The time limit while a reveal holds for its destination, ms.
    pub hold_destination_ceiling_ms: f64,
}

impl Default for StreamingWorkBudgetOptions {
    fn default() -> Self {
        Self {
            max_update_ms: 2.0,
            max_completion_admissions: 64,
            max_adopted_cpu_bytes: 8 * MIB,
            max_entity_operations: 4_096,
            max_gpu_upload_bytes: 8 * MIB,
            max_retire_operations: 64,
            destination_reserve_fraction: 0.75,
            // While a reveal waits on its destination the frame may give the
            // streaming work twice its ordinary share, not more: the login
            // tunnel's frames also carry the tunnel's own drawing and the
            // session's first scene, and the destination readies in well
            // under a second of a tunnel that lasts five and a half.
            hold_destination_ceiling_ms: 4.0,
        }
    }
}

impl StreamingWorkBudgetOptions {
    /// The budget these options give.
    pub fn to_budget(&self) -> Result<StreamingWorkBudget, WorkBudgetError> {
        if !self.max_update_ms.is_finite() || self.max_update_ms <= 0.0 {
            return Err(WorkBudgetError::InvalidBudget);
        }
        StreamingWorkBudget::new(
            Duration::from_secs_f64(self.max_update_ms / 1000.0),
            self.max_completion_admissions,
            self.max_adopted_cpu_bytes,
            self.max_entity_operations,
            self.max_gpu_upload_bytes,
            self.max_retire_operations,
            self.destination_reserve_fraction,
        )
    }

    /// The profile scaled for a quality preset's "completions per frame"
    /// (4 is the authored profile): every dimension by `count / 4`, the
    /// time no lower than a quarter millisecond.
    pub fn scale_for_legacy_completion_count(&self, count: i32) -> Result<Self, WorkBudgetError> {
        if count <= 0 {
            return Err(WorkBudgetError::InvalidCompletionCount);
        }
        let scale = f64::from(count) / 4.0;
        Ok(Self {
            max_update_ms: (self.max_update_ms * scale).max(0.25),
            max_completion_admissions: scale_i32(self.max_completion_admissions, scale),
            max_adopted_cpu_bytes: scale_i64(self.max_adopted_cpu_bytes, scale),
            max_entity_operations: scale_i32(self.max_entity_operations, scale),
            max_gpu_upload_bytes: scale_i64(self.max_gpu_upload_bytes, scale),
            max_retire_operations: scale_i32(self.max_retire_operations, scale),
            ..*self
        })
    }
}

/// What one piece of work costs.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct StreamingWorkCost {
    /// Completed loads adopted.
    pub completion_admissions: i32,
    /// CPU bytes adopted.
    pub adopted_cpu_bytes: i64,
    /// Entity operations.
    pub entity_operations: i32,
    /// GPU bytes uploaded.
    pub gpu_upload_bytes: i64,
    /// GPU resources retired.
    pub retire_operations: i32,
}

impl StreamingWorkCost {
    /// Whether it costs nothing.
    #[must_use]
    pub fn is_zero(&self) -> bool {
        *self == Self::default()
    }

    fn validate(&self) -> Result<(), WorkBudgetError> {
        if self.completion_admissions < 0
            || self.adopted_cpu_bytes < 0
            || self.entity_operations < 0
            || self.gpu_upload_bytes < 0
            || self.retire_operations < 0
        {
            return Err(WorkBudgetError::NegativeCost);
        }
        Ok(())
    }

    fn add(&self, other: &Self) -> Self {
        Self {
            completion_admissions: self
                .completion_admissions
                .saturating_add(other.completion_admissions),
            adopted_cpu_bytes: self
                .adopted_cpu_bytes
                .saturating_add(other.adopted_cpu_bytes),
            entity_operations: self
                .entity_operations
                .saturating_add(other.entity_operations),
            gpu_upload_bytes: self.gpu_upload_bytes.saturating_add(other.gpu_upload_bytes),
            retire_operations: self
                .retire_operations
                .saturating_add(other.retire_operations),
        }
    }
}

/// What a reservation was told.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StreamingWorkAdmission {
    /// Within the budget: go ahead.
    Admitted,
    /// Over a limit, but the frame's first (or one ensured) piece of work:
    /// go ahead.
    OversizedProgress,
    /// Over a limit: wait for the next frame.
    Yielded,
}

/// The limit a frame last ran into.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum StreamingWorkLimit {
    /// None.
    #[default]
    None,
    /// The time limit.
    Time,
    /// Completed loads.
    CompletionAdmissions,
    /// CPU bytes.
    AdoptedCpuBytes,
    /// Entity operations.
    EntityOperations,
    /// GPU upload bytes.
    GpuUploadBytes,
    /// GPU retirements.
    RetireOperations,
}

/// Whose work is being metered.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum StreamingWorkLane {
    /// Ordinary streaming.
    #[default]
    NonDestination,
    /// The destination a reveal is waiting on.
    Destination,
}

/// One frame's work, as observed.
#[derive(Debug, Clone, PartialEq)]
pub struct StreamingWorkMeterSnapshot {
    /// Everything reserved.
    pub used: StreamingWorkCost,
    /// Reserved in the destination lane.
    pub destination_used: StreamingWorkCost,
    /// Reserved in the ordinary lane.
    pub non_destination_used: StreamingWorkCost,
    /// Pieces of work reserved.
    pub operations: i32,
    /// Pieces completed.
    pub completed_operations: i32,
    /// Reservations that yielded.
    pub yield_count: i32,
    /// Frames over the time limit (0 or 1).
    pub overrun_count: i32,
    /// Pieces that progressed oversized.
    pub oversized_progress_count: i32,
    /// Pieces that failed.
    pub failure_count: i32,
    /// Milliseconds since the frame's start.
    pub elapsed_ms: f64,
    /// The longest piece, milliseconds.
    pub maximum_operation_ms: f64,
    /// The longest piece's stage.
    pub maximum_operation_stage: Option<&'static str>,
    /// The last stage reserved or limited.
    pub last_stage: Option<&'static str>,
    /// The last limit run into.
    pub last_limit: StreamingWorkLimit,
}

/// A monotonic clock: a timestamp and its ticks per second.
pub trait WorkClock {
    /// Now.
    fn timestamp(&self) -> i64;
    /// Timestamp units per second.
    fn frequency(&self) -> i64;
}

/// The process's monotonic clock, in nanoseconds since it was made.
#[derive(Debug, Clone, Copy)]
pub struct InstantClock(Instant);

impl Default for InstantClock {
    fn default() -> Self {
        Self(Instant::now())
    }
}

impl WorkClock for InstantClock {
    fn timestamp(&self) -> i64 {
        i64::try_from(self.0.elapsed().as_nanos()).unwrap_or(i64::MAX)
    }

    fn frequency(&self) -> i64 {
        1_000_000_000
    }
}

/// Meters one frame's streaming work against its budget.
#[derive(Debug)]
pub struct StreamingWorkMeter<C: WorkClock> {
    clock: C,
    budget: StreamingWorkBudget,
    start: i64,
    destination_reservation_active: bool,
    used: StreamingWorkCost,
    destination_used: StreamingWorkCost,
    non_destination_used: StreamingWorkCost,
    lane: StreamingWorkLane,
    destination_elapsed_ticks: i64,
    non_destination_elapsed_ticks: i64,
    active_operation_start: i64,
    maximum_operation_ms: f64,
    maximum_operation_stage: Option<&'static str>,
    operations: i32,
    completed: i32,
    yields: i32,
    overruns: i32,
    oversized_progress: i32,
    failures: i32,
    frame_overrun_recorded: bool,
    reservation_active: bool,
    ensured_progress_granted: bool,
    active_stage: Option<&'static str>,
    last_stage: Option<&'static str>,
    last_limit: StreamingWorkLimit,
}

impl<C: WorkClock + Clone> StreamingWorkMeter<C> {
    /// A meter for a frame starting now.
    pub fn new(
        budget: StreamingWorkBudget,
        clock: C,
        destination_reservation_active: bool,
    ) -> Result<Self, WorkBudgetError> {
        budget.validate()?;
        let start = clock.timestamp();
        Ok(Self::fresh(
            clock,
            budget,
            start,
            destination_reservation_active,
        ))
    }

    fn fresh(
        clock: C,
        budget: StreamingWorkBudget,
        start: i64,
        destination_reservation_active: bool,
    ) -> Self {
        Self {
            clock,
            budget,
            start,
            destination_reservation_active,
            used: StreamingWorkCost::default(),
            destination_used: StreamingWorkCost::default(),
            non_destination_used: StreamingWorkCost::default(),
            lane: StreamingWorkLane::default(),
            destination_elapsed_ticks: 0,
            non_destination_elapsed_ticks: 0,
            active_operation_start: 0,
            maximum_operation_ms: 0.0,
            maximum_operation_stage: None,
            operations: 0,
            completed: 0,
            yields: 0,
            overruns: 0,
            oversized_progress: 0,
            failures: 0,
            frame_overrun_recorded: false,
            reservation_active: false,
            ensured_progress_granted: false,
            active_stage: None,
            last_stage: None,
            last_limit: StreamingWorkLimit::None,
        }
    }

    /// Start a new frame with `budget`, forgetting the last one: the meter
    /// then reports exactly what a new one would.
    pub fn restart(
        &mut self,
        budget: StreamingWorkBudget,
        destination_reservation_active: bool,
    ) -> Result<(), WorkBudgetError> {
        budget.validate()?;
        let start = self.clock.timestamp();
        *self = Self::fresh(
            self.clock.clone(),
            budget,
            start,
            destination_reservation_active,
        );
        Ok(())
    }

    /// The lane later reservations are charged to (between operations).
    pub fn set_lane(&mut self, lane: StreamingWorkLane) -> Result<(), WorkBudgetError> {
        if self.reservation_active {
            return Err(WorkBudgetError::ReservationActive);
        }
        self.lane = lane;
        Ok(())
    }

    /// The lane reservations are charged to.
    #[must_use]
    pub fn lane(&self) -> StreamingWorkLane {
        self.lane
    }

    /// Reserve a piece of work. `ensure_progress` lets one piece per frame
    /// cross a limit even when it is not the frame's first.
    pub fn try_reserve(
        &mut self,
        cost: StreamingWorkCost,
        stage: &'static str,
        ensure_progress: bool,
    ) -> Result<StreamingWorkAdmission, WorkBudgetError> {
        cost.validate()?;
        if self.reservation_active {
            return Err(WorkBudgetError::ReservationActive);
        }
        let limit = self.find_limit(&cost);
        let grant_ensured = ensure_progress && !self.ensured_progress_granted;
        if limit != StreamingWorkLimit::None && self.operations != 0 && !grant_ensured {
            self.yields += 1;
            self.last_stage = Some(stage);
            self.last_limit = limit;
            return Ok(StreamingWorkAdmission::Yielded);
        }
        self.used = self.used.add(&cost);
        match self.lane {
            StreamingWorkLane::Destination => {
                self.destination_used = self.destination_used.add(&cost);
            }
            StreamingWorkLane::NonDestination => {
                self.non_destination_used = self.non_destination_used.add(&cost);
            }
        }
        self.operations += 1;
        self.reservation_active = true;
        self.active_stage = Some(stage);
        self.active_operation_start = self.clock.timestamp();
        if ensure_progress {
            self.ensured_progress_granted = true;
        }
        if limit != StreamingWorkLimit::None {
            self.last_stage = Some(stage);
            self.last_limit = limit;
            self.oversized_progress += 1;
            return Ok(StreamingWorkAdmission::OversizedProgress);
        }
        if self.last_limit == StreamingWorkLimit::None {
            self.last_stage = Some(stage);
        }
        Ok(StreamingWorkAdmission::Admitted)
    }

    /// The reserved piece finished.
    pub fn complete(&mut self) -> Result<(), WorkBudgetError> {
        self.end_operation()?;
        self.completed += 1;
        self.observe_overrun();
        self.active_stage = None;
        Ok(())
    }

    /// The reserved piece failed.
    pub fn fail(&mut self) -> Result<(), WorkBudgetError> {
        self.end_operation()?;
        self.failures += 1;
        self.observe_overrun();
        self.active_stage = None;
        Ok(())
    }

    /// The frame's streaming is over.
    pub fn finish_frame(&mut self) -> Result<(), WorkBudgetError> {
        if self.reservation_active {
            return Err(WorkBudgetError::ReservationActive);
        }
        self.observe_overrun();
        Ok(())
    }

    /// What the frame did so far.
    #[must_use]
    pub fn snapshot(&self) -> StreamingWorkMeterSnapshot {
        let now = self.clock.timestamp();
        StreamingWorkMeterSnapshot {
            used: self.used,
            destination_used: self.destination_used,
            non_destination_used: self.non_destination_used,
            operations: self.operations,
            completed_operations: self.completed,
            yield_count: self.yields,
            overrun_count: self.overruns,
            oversized_progress_count: self.oversized_progress,
            failure_count: self.failures,
            elapsed_ms: self.elapsed_ms(now),
            maximum_operation_ms: self.maximum_operation_ms,
            maximum_operation_stage: self.maximum_operation_stage,
            last_stage: self.last_stage,
            last_limit: self.last_limit,
        }
    }

    fn end_operation(&mut self) -> Result<(), WorkBudgetError> {
        if !self.reservation_active {
            return Err(WorkBudgetError::NoReservation);
        }
        let raw = (self.clock.timestamp() - self.active_operation_start).max(0);
        let elapsed_ms = raw as f64 * 1000.0 / self.clock.frequency() as f64;
        if self.maximum_operation_stage.is_none() || elapsed_ms > self.maximum_operation_ms {
            self.maximum_operation_ms = elapsed_ms;
            self.maximum_operation_stage = self.active_stage;
        }
        let elapsed = self.ticks_of(raw);
        match self.lane {
            StreamingWorkLane::Destination => {
                self.destination_elapsed_ticks =
                    self.destination_elapsed_ticks.saturating_add(elapsed);
            }
            StreamingWorkLane::NonDestination => {
                self.non_destination_elapsed_ticks =
                    self.non_destination_elapsed_ticks.saturating_add(elapsed);
            }
        }
        self.reservation_active = false;
        Ok(())
    }

    fn find_limit(&self, cost: &StreamingWorkCost) -> StreamingWorkLimit {
        let b = &self.budget;
        if self.elapsed_ticks(self.clock.timestamp()) >= b.max_update_ticks {
            return StreamingWorkLimit::Time;
        }
        if i64::from(self.used.completion_admissions) + i64::from(cost.completion_admissions)
            > i64::from(b.max_completion_admissions)
        {
            return StreamingWorkLimit::CompletionAdmissions;
        }
        if self.used.adopted_cpu_bytes > b.max_adopted_cpu_bytes - cost.adopted_cpu_bytes {
            return StreamingWorkLimit::AdoptedCpuBytes;
        }
        if i64::from(self.used.entity_operations) + i64::from(cost.entity_operations)
            > i64::from(b.max_entity_operations)
        {
            return StreamingWorkLimit::EntityOperations;
        }
        if self.used.gpu_upload_bytes > b.max_gpu_upload_bytes - cost.gpu_upload_bytes {
            return StreamingWorkLimit::GpuUploadBytes;
        }
        if i64::from(self.used.retire_operations) + i64::from(cost.retire_operations)
            > i64::from(b.max_retire_operations)
        {
            return StreamingWorkLimit::RetireOperations;
        }
        if self.destination_reservation_active && self.lane == StreamingWorkLane::NonDestination {
            let share = 1.0 - f64::from(b.destination_reserve_fraction);
            let nd = &self.non_destination_used;
            if self.non_destination_elapsed_ticks >= reserved_limit(b.max_update_ticks, share) {
                return StreamingWorkLimit::Time;
            }
            if i64::from(nd.completion_admissions) + i64::from(cost.completion_admissions)
                > reserved_limit(i64::from(b.max_completion_admissions), share)
            {
                return StreamingWorkLimit::CompletionAdmissions;
            }
            if nd.adopted_cpu_bytes
                > reserved_limit(b.max_adopted_cpu_bytes, share) - cost.adopted_cpu_bytes
            {
                return StreamingWorkLimit::AdoptedCpuBytes;
            }
            if i64::from(nd.entity_operations) + i64::from(cost.entity_operations)
                > reserved_limit(i64::from(b.max_entity_operations), share)
            {
                return StreamingWorkLimit::EntityOperations;
            }
            if nd.gpu_upload_bytes
                > reserved_limit(b.max_gpu_upload_bytes, share) - cost.gpu_upload_bytes
            {
                return StreamingWorkLimit::GpuUploadBytes;
            }
            if i64::from(nd.retire_operations) + i64::from(cost.retire_operations)
                > reserved_limit(i64::from(b.max_retire_operations), share)
            {
                return StreamingWorkLimit::RetireOperations;
            }
        }
        StreamingWorkLimit::None
    }

    fn observe_overrun(&mut self) {
        if !self.frame_overrun_recorded
            && self.elapsed_ticks(self.clock.timestamp()) > self.budget.max_update_ticks
        {
            self.frame_overrun_recorded = true;
            self.overruns += 1;
            self.last_limit = StreamingWorkLimit::Time;
            self.last_stage = self.active_stage.or(self.last_stage);
        }
    }

    fn ticks_of(&self, raw: i64) -> i64 {
        if raw > i64::MAX / TICKS_PER_SECOND {
            i64::MAX
        } else {
            raw * TICKS_PER_SECOND / self.clock.frequency()
        }
    }

    fn elapsed_ticks(&self, timestamp: i64) -> i64 {
        self.ticks_of((timestamp - self.start).max(0))
    }

    fn elapsed_ms(&self, timestamp: i64) -> f64 {
        (timestamp - self.start).max(0) as f64 * 1000.0 / self.clock.frequency() as f64
    }
}

fn reserved_limit(total: i64, fraction: f64) -> i64 {
    ((total as f64 * fraction).floor() as i64).max(1)
}

#[cfg(test)]
#[path = "work_budget_tests.rs"]
mod tests;
