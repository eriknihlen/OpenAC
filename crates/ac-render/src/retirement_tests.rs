use std::cell::Cell;
use std::rc::Rc;
use std::time::Duration;

use super::*;
use crate::work_budget::StreamingWorkBudget;

/// A clock the test moves by hand, in milliseconds.
#[derive(Debug, Clone, Default)]
struct TestClock(Rc<Cell<i64>>);

impl TestClock {
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

/// An ordinary frame's budget: 2 ms, four GPU retirements.
fn meter(clock: &TestClock) -> StreamingWorkMeter<TestClock> {
    let budget = StreamingWorkBudget::new(
        Duration::from_millis(2),
        64,
        1 << 20,
        4_096,
        1 << 20,
        4,
        0.75,
    )
    .unwrap();
    StreamingWorkMeter::new(budget, clock.clone(), false).unwrap()
}

fn lb(x: u8, y: u8) -> LandblockCoord {
    LandblockCoord { x, y }
}

/// Each item is the GPU resources its landblock holds.
fn retires(n: &i32) -> StreamingWorkCost {
    StreamingWorkCost {
        retire_operations: *n,
        ..StreamingWorkCost::default()
    }
}

/// Tear down what one frame admits, oldest first.
fn frame(queue: &mut RetirementQueue<i32>, clock: &TestClock, step_ms: i64) -> Vec<LandblockCoord> {
    let mut meter = meter(clock);
    let mut done = Vec::new();
    while let Some((coord, _)) = queue.next_admitted(&mut meter, retires) {
        clock.advance(step_ms);
        meter.complete().unwrap();
        done.push(coord);
    }
    meter.finish_frame().unwrap();
    done
}

#[test]
fn a_window_of_landblocks_is_torn_down_over_frames_within_the_budget() {
    let clock = TestClock::default();
    let mut queue = RetirementQueue::default();
    for x in 0..5 {
        queue.push(lb(x, 0), 2);
    }
    // Four GPU retirements a frame: two landblocks of two each.
    assert_eq!(frame(&mut queue, &clock, 0), [lb(0, 0), lb(1, 0)]);
    assert_eq!(frame(&mut queue, &clock, 0), [lb(2, 0), lb(3, 0)]);
    assert_eq!(frame(&mut queue, &clock, 0), [lb(4, 0)]);
    assert!(queue.is_empty());
    assert!(frame(&mut queue, &clock, 0).is_empty());
}

#[test]
fn the_time_limit_ends_a_frames_teardown() {
    let clock = TestClock::default();
    let mut queue = RetirementQueue::default();
    for x in 0..5 {
        queue.push(lb(x, 0), 0);
    }
    // A millisecond each against two: the third waits for the next frame.
    assert_eq!(frame(&mut queue, &clock, 1), [lb(0, 0), lb(1, 0)]);
    assert_eq!(queue.len(), 3);
}

#[test]
fn a_landblock_over_the_whole_budget_still_goes_first_in_its_frame() {
    let clock = TestClock::default();
    let mut queue = RetirementQueue::default();
    queue.push(lb(0, 0), 9);
    queue.push(lb(1, 0), 1);
    // A frame's first piece always runs, so the queue never stalls.
    assert_eq!(frame(&mut queue, &clock, 0), [lb(0, 0)]);
    assert_eq!(frame(&mut queue, &clock, 0), [lb(1, 0)]);
}

#[test]
fn a_landblock_wanted_again_is_taken_out_of_turn_whole() {
    let mut queue = RetirementQueue::default();
    queue.push(lb(0, 0), 10);
    queue.push(lb(1, 0), 11);
    queue.push(lb(2, 0), 12);
    // A short hop back: the landblock's teardown is handed back to be
    // finished at once, and the rest keep their order.
    assert_eq!(queue.take(lb(1, 0)), Some(11));
    assert_eq!(queue.take(lb(1, 0)), None);
    assert_eq!(queue.coords().collect::<Vec<_>>(), [lb(0, 0), lb(2, 0)]);
    assert_eq!(queue.take(lb(7, 7)), None);
}
