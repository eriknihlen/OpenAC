//! Ported from OpenAC `tests/AcDream.App.Tests/Streaming/StreamingWorkBudgetTests.cs`
//! (the meter, budget and option cases; the controller cases belong to
//! OpenAC's worker-pool streaming controller, not ported).

use std::cell::Cell;
use std::rc::Rc;

use super::*;

/// A clock the test moves by hand, 1000 units a second (milliseconds).
#[derive(Debug, Clone, Default)]
struct TestClock(Rc<Cell<i64>>);

impl TestClock {
    fn set(&self, now: i64) {
        self.0.set(now);
    }

    fn advance(&self, by: i64) {
        self.0.set(self.0.get() + by);
    }
}

impl WorkClock for TestClock {
    fn timestamp(&self) -> i64 {
        self.0.get()
    }

    fn frequency(&self) -> i64 {
        1_000
    }
}

fn budget(
    milliseconds: f64,
    completions: i32,
    cpu_bytes: i64,
    entity_operations: i32,
    gpu_bytes: i64,
    retire_operations: i32,
) -> StreamingWorkBudget {
    StreamingWorkBudget::new(
        Duration::from_secs_f64(milliseconds / 1000.0),
        completions,
        cpu_bytes,
        entity_operations,
        gpu_bytes,
        retire_operations,
        0.75,
    )
    .unwrap()
}

fn default_budget() -> StreamingWorkBudget {
    budget(2.0, 4, 100, 8, 100, 4)
}

fn completions(n: i32) -> StreamingWorkCost {
    StreamingWorkCost {
        completion_admissions: n,
        ..StreamingWorkCost::default()
    }
}

fn cpu(n: i64) -> StreamingWorkCost {
    StreamingWorkCost {
        adopted_cpu_bytes: n,
        ..StreamingWorkCost::default()
    }
}

#[test]
fn the_default_profile_uses_elapsed_time_as_the_micro_operation_guard() {
    let defaults = StreamingWorkBudgetOptions::default();
    assert_eq!(defaults.max_entity_operations, 4_096);
    assert_eq!(defaults.max_update_ms, 2.0);
}

fn scripted_tick(
    meter: &mut StreamingWorkMeter<TestClock>,
    clock: &TestClock,
) -> StreamingWorkMeterSnapshot {
    assert_eq!(
        meter.try_reserve(completions(1), "load", false),
        Ok(StreamingWorkAdmission::Admitted)
    );
    clock.advance(2);
    meter.complete().unwrap();
    assert_eq!(
        meter.try_reserve(cpu(64), "adopt", false),
        Ok(StreamingWorkAdmission::Admitted)
    );
    clock.advance(1);
    meter.fail().unwrap();
    assert_eq!(
        meter.try_reserve(completions(8), "too big", false),
        Ok(StreamingWorkAdmission::Yielded)
    );
    assert_eq!(
        meter.try_reserve(completions(8), "oversized", true),
        Ok(StreamingWorkAdmission::OversizedProgress)
    );
    clock.advance(3);
    meter.complete().unwrap();
    meter.finish_frame().unwrap();
    meter.snapshot()
}

#[test]
fn a_restarted_meter_reports_what_a_fresh_one_would() {
    let clock = TestClock::default();
    let mut reused =
        StreamingWorkMeter::new(budget(1.0, 1, 1, 8, 100, 4), clock.clone(), true).unwrap();
    reused.set_lane(StreamingWorkLane::Destination).unwrap();
    assert_eq!(
        reused.try_reserve(completions(1), "first", false),
        Ok(StreamingWorkAdmission::Admitted)
    );
    clock.advance(50);
    reused.fail().unwrap();
    assert_eq!(
        reused.try_reserve(completions(4), "ensured", true),
        Ok(StreamingWorkAdmission::OversizedProgress)
    );
    reused.complete().unwrap();
    reused.set_lane(StreamingWorkLane::NonDestination).unwrap();
    reused.finish_frame().unwrap();
    clock.advance(500);

    let restart_at = clock.timestamp();
    reused
        .restart(budget(10.0, 2, 64, 8, 100, 4), false)
        .unwrap();
    let restarted = scripted_tick(&mut reused, &clock);

    clock.set(restart_at);
    let mut fresh =
        StreamingWorkMeter::new(budget(10.0, 2, 64, 8, 100, 4), clock.clone(), false).unwrap();
    let built = scripted_tick(&mut fresh, &clock);
    assert_eq!(built, restarted);
}

#[test]
fn the_meter_yields_before_a_second_operation_that_exceeds_any_dimension() {
    let clock = TestClock::default();
    let mut meter = StreamingWorkMeter::new(default_budget(), clock, false).unwrap();
    let first = StreamingWorkCost {
        completion_admissions: 2,
        adopted_cpu_bytes: 60,
        entity_operations: 3,
        gpu_upload_bytes: 40,
        retire_operations: 1,
    };
    assert_eq!(
        meter.try_reserve(first, "first", false),
        Ok(StreamingWorkAdmission::Admitted)
    );
    meter.complete().unwrap();
    assert_eq!(
        meter.try_reserve(cpu(41), "second", false),
        Ok(StreamingWorkAdmission::Yielded)
    );
    let s = meter.snapshot();
    assert_eq!(s.operations, 1);
    assert_eq!(s.completed_operations, 1);
    assert_eq!(s.yield_count, 1);
    assert_eq!(s.last_limit, StreamingWorkLimit::AdoptedCpuBytes);
    assert_eq!(s.last_stage, Some("second"));
}

#[test]
fn the_first_oversized_operation_progresses_and_names_the_oversize() {
    let mut meter =
        StreamingWorkMeter::new(budget(2.0, 1, 100, 8, 100, 4), TestClock::default(), false)
            .unwrap();
    assert_eq!(
        meter.try_reserve(completions(2), "oversized", false),
        Ok(StreamingWorkAdmission::OversizedProgress)
    );
    meter.complete().unwrap();
    let s = meter.snapshot();
    assert_eq!(s.oversized_progress_count, 1);
    assert_eq!(s.last_limit, StreamingWorkLimit::CompletionAdmissions);
}

#[test]
fn ensure_progress_can_cross_the_limit_only_once_per_frame() {
    let mut meter =
        StreamingWorkMeter::new(budget(2.0, 4, 1, 8, 100, 4), TestClock::default(), false).unwrap();
    assert_eq!(
        meter.try_reserve(completions(1), "admission", false),
        Ok(StreamingWorkAdmission::Admitted)
    );
    meter.complete().unwrap();
    assert_eq!(
        meter.try_reserve(cpu(1), "first-execution", true),
        Ok(StreamingWorkAdmission::Admitted)
    );
    meter.complete().unwrap();
    assert_eq!(
        meter.try_reserve(cpu(1), "second-execution", true),
        Ok(StreamingWorkAdmission::Yielded)
    );
    assert_eq!(meter.snapshot().yield_count, 1);
    assert_eq!(
        meter.snapshot().last_limit,
        StreamingWorkLimit::AdoptedCpuBytes
    );
}

#[test]
fn an_active_reveal_protects_the_destination_share_in_every_dimension() {
    let mut meter =
        StreamingWorkMeter::new(budget(100.0, 4, 100, 8, 100, 4), TestClock::default(), true)
            .unwrap();
    let unreserved = StreamingWorkCost {
        completion_admissions: 1,
        adopted_cpu_bytes: 25,
        entity_operations: 2,
        gpu_upload_bytes: 25,
        retire_operations: 1,
    };
    assert_eq!(
        meter.try_reserve(unreserved, "ordinary", false),
        Ok(StreamingWorkAdmission::Admitted)
    );
    meter.complete().unwrap();
    assert_eq!(
        meter.try_reserve(completions(1), "ordinary-over-reserve", false),
        Ok(StreamingWorkAdmission::Yielded)
    );
    meter.set_lane(StreamingWorkLane::Destination).unwrap();
    let destination = StreamingWorkCost {
        completion_admissions: 3,
        adopted_cpu_bytes: 75,
        entity_operations: 6,
        gpu_upload_bytes: 75,
        retire_operations: 3,
    };
    assert_eq!(
        meter.try_reserve(destination, "destination", false),
        Ok(StreamingWorkAdmission::Admitted)
    );
    meter.complete().unwrap();
    let s = meter.snapshot();
    assert_eq!(s.non_destination_used, unreserved);
    assert_eq!(s.destination_used.completion_admissions, 3);
    assert_eq!(s.used.adopted_cpu_bytes, 100);
    assert_eq!(s.used.gpu_upload_bytes, 100);
}

#[test]
fn an_inactive_reveal_does_not_reserve_for_the_destination() {
    let mut meter = StreamingWorkMeter::new(default_budget(), TestClock::default(), false).unwrap();
    assert_eq!(
        meter.try_reserve(completions(4), "ordinary", false),
        Ok(StreamingWorkAdmission::Admitted)
    );
    meter.complete().unwrap();
    assert_eq!(
        meter.snapshot().non_destination_used.completion_admissions,
        4
    );
    assert_eq!(meter.snapshot().destination_used.completion_admissions, 0);
}

#[test]
fn an_active_reveal_protects_the_destination_wall_clock_after_the_ordinary_share() {
    let clock = TestClock::default();
    let mut meter =
        StreamingWorkMeter::new(budget(100.0, 4, 100, 8, 100, 4), clock.clone(), true).unwrap();
    assert_eq!(
        meter.try_reserve(StreamingWorkCost::default(), "ordinary", false),
        Ok(StreamingWorkAdmission::Admitted)
    );
    clock.set(25);
    meter.complete().unwrap();
    assert_eq!(
        meter.try_reserve(StreamingWorkCost::default(), "ordinary-after-25ms", false),
        Ok(StreamingWorkAdmission::Yielded)
    );
    meter.set_lane(StreamingWorkLane::Destination).unwrap();
    assert_eq!(
        meter.try_reserve(StreamingWorkCost::default(), "destination", false),
        Ok(StreamingWorkAdmission::Admitted)
    );
    clock.set(99);
    meter.complete().unwrap();
    assert_eq!(meter.snapshot().completed_operations, 2);
}

#[test]
fn widening_for_a_destination_hold_scales_every_dimension() {
    let widened = StreamingWorkBudgetOptions::default()
        .to_budget()
        .unwrap()
        .widen_for_destination_hold(8.0);
    assert_eq!(widened.max_update_time(), Duration::from_millis(8));
    assert_eq!(widened.max_completion_admissions(), 256);
    assert_eq!(widened.max_adopted_cpu_bytes(), 32 * MIB);
    assert_eq!(widened.max_entity_operations(), 16_384);
    assert_eq!(widened.max_gpu_upload_bytes(), 32 * MIB);
    assert_eq!(widened.max_retire_operations(), 256);
    assert_eq!(widened.destination_reserve_fraction(), 0.9375);
}

// OpenAC `FrameUploadAllowanceTests.RevealMeshLimits_WidenEveryDimensionWithTheFrameBudget`:
// the authored profile's reveal widens the 2 ms frame to its 4 ms hold
// ceiling, twice, and every limit with it.
#[test]
fn a_reveal_widens_the_authored_profile_to_four_milliseconds() {
    let options = StreamingWorkBudgetOptions::default();
    assert_eq!(options.hold_destination_ceiling_ms, 4.0);
    let widened = options
        .to_budget()
        .unwrap()
        .widen_for_destination_hold(options.hold_destination_ceiling_ms);
    assert_eq!(widened.max_update_time(), Duration::from_millis(4));
    assert_eq!(widened.max_completion_admissions(), 128);
    assert_eq!(widened.max_adopted_cpu_bytes(), 16 * MIB);
    assert_eq!(widened.max_gpu_upload_bytes(), 16 * MIB);
}

#[test]
fn widening_is_a_no_op_at_or_below_the_current_ceiling() {
    let authored = StreamingWorkBudgetOptions::default().to_budget().unwrap();
    for ceiling in [2.0, 1.0, 0.0, f64::NAN] {
        assert_eq!(authored.widen_for_destination_hold(ceiling), authored);
    }
}

#[test]
fn widening_does_not_widen_the_ordinary_lane() {
    let authored = StreamingWorkBudgetOptions::default().to_budget().unwrap();
    for b in [authored, authored.widen_for_destination_hold(8.0)] {
        let mut meter = StreamingWorkMeter::new(b, TestClock::default(), true).unwrap();
        assert_eq!(
            meter.try_reserve(completions(16), "ordinary", false),
            Ok(StreamingWorkAdmission::Admitted)
        );
        meter.complete().unwrap();
        assert_eq!(
            meter.try_reserve(completions(1), "ordinary-over-share", false),
            Ok(StreamingWorkAdmission::Yielded)
        );
        assert_eq!(
            meter.snapshot().last_limit,
            StreamingWorkLimit::CompletionAdmissions
        );
    }
    // The widened destination lane owns the remainder: 240 more.
    let mut meter = StreamingWorkMeter::new(
        authored.widen_for_destination_hold(8.0),
        TestClock::default(),
        true,
    )
    .unwrap();
    assert_eq!(
        meter.try_reserve(completions(16), "ordinary", false),
        Ok(StreamingWorkAdmission::Admitted)
    );
    meter.complete().unwrap();
    meter.set_lane(StreamingWorkLane::Destination).unwrap();
    assert_eq!(
        meter.try_reserve(completions(240), "destination", false),
        Ok(StreamingWorkAdmission::Admitted)
    );
    meter.complete().unwrap();
    assert_eq!(
        meter.try_reserve(completions(1), "destination-over-widened-total", false),
        Ok(StreamingWorkAdmission::Yielded)
    );
}

#[test]
fn the_meter_uses_its_clock_and_records_overrun_and_failure() {
    let clock = TestClock::default();
    let mut meter = StreamingWorkMeter::new(default_budget(), clock.clone(), false).unwrap();
    assert_eq!(
        meter.try_reserve(StreamingWorkCost::default(), "slow", false),
        Ok(StreamingWorkAdmission::Admitted)
    );
    clock.set(3);
    meter.fail().unwrap();
    let s = meter.snapshot();
    assert_eq!(s.elapsed_ms, 3.0);
    assert_eq!(s.overrun_count, 1);
    assert_eq!(s.failure_count, 1);
    assert_eq!(s.completed_operations, 0);
    assert_eq!(s.maximum_operation_ms, 3.0);
    assert_eq!(s.maximum_operation_stage, Some("slow"));
    assert_eq!(s.last_stage, Some("slow"));
    assert_eq!(s.last_limit, StreamingWorkLimit::Time);
}

#[test]
fn the_meter_rejects_reentrant_reservations_and_negative_costs() {
    let mut meter = StreamingWorkMeter::new(default_budget(), TestClock::default(), false).unwrap();
    meter
        .try_reserve(StreamingWorkCost::default(), "first", false)
        .unwrap();
    assert_eq!(
        meter.try_reserve(StreamingWorkCost::default(), "reentrant", false),
        Err(WorkBudgetError::ReservationActive)
    );
    meter.complete().unwrap();
    assert_eq!(
        meter.try_reserve(cpu(-1), "negative", false),
        Err(WorkBudgetError::NegativeCost)
    );
    assert_eq!(
        StreamingWorkBudget::new(Duration::ZERO, 1, 1, 1, 1, 1, 0.5),
        Err(WorkBudgetError::InvalidBudget)
    );
}

#[test]
fn the_legacy_completion_selector_scales_the_whole_profile() {
    let options = StreamingWorkBudgetOptions {
        max_update_ms: 2.0,
        max_completion_admissions: 4,
        max_adopted_cpu_bytes: 100,
        max_entity_operations: 8,
        max_gpu_upload_bytes: 100,
        max_retire_operations: 4,
        destination_reserve_fraction: 0.75,
        hold_destination_ceiling_ms: 8.0,
    };
    for (count, ms, admissions, cpu_bytes, entity_ops, gpu_bytes, retire_ops) in [
        (2, 1.0, 2, 50, 4, 50, 2),
        (4, 2.0, 4, 100, 8, 100, 4),
        (6, 3.0, 6, 150, 12, 150, 6),
    ] {
        let scaled = options.scale_for_legacy_completion_count(count).unwrap();
        assert_eq!(scaled.max_update_ms, ms);
        assert_eq!(scaled.max_completion_admissions, admissions);
        assert_eq!(scaled.max_adopted_cpu_bytes, cpu_bytes);
        assert_eq!(scaled.max_entity_operations, entity_ops);
        assert_eq!(scaled.max_gpu_upload_bytes, gpu_bytes);
        assert_eq!(scaled.max_retire_operations, retire_ops);
        assert_eq!(scaled.destination_reserve_fraction, 0.75);
    }
}

#[test]
fn the_legacy_completion_selector_rejects_zero_and_saturates_huge_profiles() {
    let options = StreamingWorkBudgetOptions {
        max_update_ms: 2.0,
        max_completion_admissions: i32::MAX,
        max_adopted_cpu_bytes: i64::MAX,
        max_entity_operations: i32::MAX,
        max_gpu_upload_bytes: i64::MAX,
        max_retire_operations: i32::MAX,
        destination_reserve_fraction: 0.75,
        hold_destination_ceiling_ms: 8.0,
    };
    assert_eq!(
        options.scale_for_legacy_completion_count(0),
        Err(WorkBudgetError::InvalidCompletionCount)
    );
    let scaled = options.scale_for_legacy_completion_count(i32::MAX).unwrap();
    assert_eq!(scaled.max_completion_admissions, i32::MAX);
    assert_eq!(scaled.max_adopted_cpu_bytes, i64::MAX);
    assert_eq!(scaled.max_entity_operations, i32::MAX);
    assert_eq!(scaled.max_gpu_upload_bytes, i64::MAX);
    assert_eq!(scaled.max_retire_operations, i32::MAX);
}
