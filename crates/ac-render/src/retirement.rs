//! Landblocks that left the streaming window, torn down over frames.
//!
//! A teleport moves the window away from every landblock it held, a few
//! hundred at once. Each leaves the window's resident set in the frame it
//! leaves (nothing of it is drawn, picked or waited for from then on); what
//! it still holds (its terrain buffer, its cells' meshes, its walk cells,
//! collision, lights, animations and effects) is released here, a whole
//! landblock per step, as many steps a frame as the streaming budget
//! admits. A landblock asked for again while it waits is torn down at once
//! first, so a loaded landblock never shares the window with a half-released
//! copy of itself.

use std::collections::VecDeque;

use crate::work_budget::{
    StreamingWorkAdmission, StreamingWorkCost, StreamingWorkMeter, WorkClock,
};
use crate::world_frame::LandblockCoord;

/// What a teardown step is metered as.
pub const STAGE: &str = "retire landblock";

/// Landblocks waiting to be torn down, oldest first.
#[derive(Debug)]
pub struct RetirementQueue<T> {
    pending: VecDeque<(LandblockCoord, T)>,
}

impl<T> Default for RetirementQueue<T> {
    fn default() -> Self {
        Self {
            pending: VecDeque::new(),
        }
    }
}

impl<T> RetirementQueue<T> {
    /// A landblock left the window: its teardown waits its turn.
    pub fn push(&mut self, coord: LandblockCoord, item: T) {
        self.pending.push_back((coord, item));
    }

    /// Nothing waits.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.pending.is_empty()
    }

    /// How many landblocks wait.
    #[must_use]
    pub fn len(&self) -> usize {
        self.pending.len()
    }

    /// The landblocks that wait, oldest first.
    pub fn coords(&self) -> impl Iterator<Item = LandblockCoord> + '_ {
        self.pending.iter().map(|(c, _)| *c)
    }

    /// Take a landblock's waiting teardown out of turn (it is wanted
    /// again): the caller finishes it before the landblock is loaded anew.
    pub fn take(&mut self, coord: LandblockCoord) -> Option<T> {
        let index = self.pending.iter().position(|(c, _)| *c == coord)?;
        self.pending.remove(index).map(|(_, item)| item)
    }

    /// The next landblock to tear down, when `meter` admits its `cost`;
    /// `None` when nothing waits or the frame's budget is spent. The caller
    /// tears it down whole, then completes the meter's reservation.
    pub fn next_admitted<C: WorkClock + Clone>(
        &mut self,
        meter: &mut StreamingWorkMeter<C>,
        cost: impl Fn(&T) -> StreamingWorkCost,
    ) -> Option<(LandblockCoord, T)> {
        let (_, item) = self.pending.front()?;
        match meter.try_reserve(cost(item), STAGE, false) {
            Ok(StreamingWorkAdmission::Admitted | StreamingWorkAdmission::OversizedProgress) => {
                self.pending.pop_front()
            }
            Ok(StreamingWorkAdmission::Yielded) | Err(_) => None,
        }
    }
}

#[cfg(test)]
#[path = "retirement_tests.rs"]
mod tests;
