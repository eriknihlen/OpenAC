//! The motion-table manager: owns an object's motion state, turns movement
//! requests into motion-table calls, and keeps the queue of motions still
//! waiting for their animations to finish, reporting each one as done.

use std::collections::VecDeque;
use std::fmt;
use std::sync::Arc;

use crate::command::{ACTION_BIT, MODIFIER_BIT, SUBSTATE_BIT};
use crate::motion_command;
use crate::motion_state::MotionState;
use crate::motion_table::MotionTableRules;
use crate::sequence::Sequence;

/// Kinds of movement request. Only the interpreted ones reach the motion
/// table; the others belong to the interpreter and the move-to manager.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u32)]
pub enum MovementType {
    /// No movement.
    Invalid = 0,
    /// A raw (input-level) command.
    RawCommand = 1,
    /// An interpreted command.
    InterpretedCommand = 2,
    /// Stop a raw command.
    StopRawCommand = 3,
    /// Stop an interpreted command.
    StopInterpretedCommand = 4,
    /// Stop everything.
    StopCompletely = 5,
    /// Move to an object.
    MoveToObject = 6,
    /// Move to a position.
    MoveToPosition = 7,
    /// Turn to face an object.
    TurnToObject = 8,
    /// Turn to a heading.
    TurnToHeading = 9,
}

/// A request for the motion table.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MotionTableMovement {
    /// What to do.
    pub kind: MovementType,
    /// Motion command.
    pub motion: u32,
    /// Speed factor.
    pub speed: f32,
}

impl MotionTableMovement {
    /// Start `motion` at `speed`.
    #[must_use]
    pub fn interpreted(motion: u32, speed: f32) -> Self {
        Self {
            kind: MovementType::InterpretedCommand,
            motion,
            speed,
        }
    }

    /// Stop `motion`.
    #[must_use]
    pub fn stop_interpreted(motion: u32, speed: f32) -> Self {
        Self {
            kind: MovementType::StopInterpretedCommand,
            motion,
            speed,
        }
    }

    /// Stop everything.
    #[must_use]
    pub fn stop_completely() -> Self {
        Self {
            kind: MovementType::StopCompletely,
            motion: 0,
            speed: 1.0,
        }
    }
}

/// Why a movement request failed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MotionTableError {
    /// The object has no motion table.
    NoTable,
    /// The table could not start or stop the motion.
    MotionFailed,
    /// The request is not a motion-table request.
    NotHandled,
}

impl MotionTableError {
    /// The game's error code: 7, `0x43`, or all bits set for "not handled".
    #[must_use]
    pub fn code(self) -> u32 {
        match self {
            Self::NoTable => 7,
            Self::MotionFailed => 0x43,
            Self::NotHandled => 0xFFFF_FFFF,
        }
    }
}

impl fmt::Display for MotionTableError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::NoTable => "no motion table",
            Self::MotionFailed => "the motion table refused the motion",
            Self::NotHandled => "not a motion-table request",
        })
    }
}

impl std::error::Error for MotionTableError {}

/// Told when a queued motion has finished (or was abandoned).
pub trait MotionDoneSink {
    /// `motion` finished; `success` is false when it was cut off.
    fn motion_done(&mut self, motion: u32, success: bool);
}

/// Ignores completions.
#[derive(Debug, Clone, Copy, Default)]
pub struct NoMotionDone;

impl MotionDoneSink for NoMotionDone {
    fn motion_done(&mut self, _motion: u32, _success: bool) {}
}

/// Records completions in order.
impl MotionDoneSink for Vec<(u32, bool)> {
    fn motion_done(&mut self, motion: u32, success: bool) {
        self.push((motion, success));
    }
}

/// A queued motion and how many animation completions it waits for.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PendingMotion {
    /// Motion command.
    pub motion: u32,
    /// Animation completions still awaited (wraps for a motion without
    /// animations, which then completes at once).
    pub num_anims: u32,
}

/// A cycle or stance motion later in the queue is not redundant past one of
/// these classes.
const CYCLE_TAIL_BLOCK_MASK: u32 = 0xB000_0000;
const STYLE_TAIL_BLOCK_MASK: u32 = 0x7000_0000;

/// The motion queued for every stop: back to ready.
pub const READY_SENTINEL: u32 = motion_command::READY;

/// Owns an object's motion state and its queue of pending motions.
#[derive(Debug, Clone)]
pub struct MotionTableManager {
    table: Option<Arc<MotionTableRules>>,
    state: MotionState,
    pending_animations: VecDeque<PendingMotion>,
    animation_counter: i32,
}

impl MotionTableManager {
    /// A manager over `table` (an object may have none) with a default
    /// state.
    #[must_use]
    pub fn new(table: Option<Arc<MotionTableRules>>) -> Self {
        Self {
            table,
            state: MotionState::default(),
            pending_animations: VecDeque::new(),
            animation_counter: 0,
        }
    }

    /// The motion table, if any.
    #[must_use]
    pub fn table(&self) -> Option<&Arc<MotionTableRules>> {
        self.table.as_ref()
    }

    /// The motion state.
    #[must_use]
    pub fn state(&self) -> &MotionState {
        &self.state
    }

    /// The motion state, for hosts that seed or reset it.
    pub fn state_mut(&mut self) -> &mut MotionState {
        &mut self.state
    }

    /// The pending queue, oldest first.
    pub fn pending_animations(&self) -> impl ExactSizeIterator<Item = &PendingMotion> {
        self.pending_animations.iter()
    }

    /// Animation completions counted towards the queue head.
    #[must_use]
    pub fn animation_counter(&self) -> i32 {
        self.animation_counter
    }

    /// Queue `motion` waiting for `ticks` completions, then collapse any
    /// queued motions it makes redundant.
    pub fn add_to_queue(&mut self, sequence: &mut Sequence, motion: u32, ticks: u32) {
        self.pending_animations.push_back(PendingMotion {
            motion,
            num_anims: ticks,
        });
        self.remove_redundant_links(sequence);
    }

    /// When the newest waiting motion (a cycle or a stance) repeats an
    /// earlier waiting one with nothing important between them, the
    /// motions between are no longer needed: their counts are zeroed and
    /// their link animations removed from `sequence`.
    pub fn remove_redundant_links(&mut self, sequence: &mut Sequence) {
        let mut tail = self.pending_animations.len().checked_sub(1);
        while let Some(t) = tail
            && self.pending_animations[t].num_anims == 0
        {
            tail = t.checked_sub(1);
        }
        let Some(tail) = tail else {
            return;
        };
        let motion = self.pending_animations[tail].motion;

        let mut matched = None;
        if motion & SUBSTATE_BIT != 0 && motion & MODIFIER_BIT == 0 {
            for i in (0..tail).rev() {
                let p = self.pending_animations[i];
                if p.motion == motion && p.num_anims != 0 {
                    matched = Some(i);
                    break;
                }
                if p.num_anims != 0 && p.motion & CYCLE_TAIL_BLOCK_MASK != 0 {
                    return;
                }
            }
        } else if (motion as i32) < 0 {
            for i in (0..tail).rev() {
                let p = self.pending_animations[i];
                if p.motion == motion {
                    matched = Some(i);
                    break;
                }
                if p.num_anims != 0 && p.motion & STYLE_TAIL_BLOCK_MASK != 0 {
                    return;
                }
            }
        }
        if let Some(stop_at) = matched {
            self.truncate_animation_list(sequence, stop_at);
        }
    }

    /// Zero every queued count after `stop_at` and remove that many link
    /// animations from `sequence`.
    fn truncate_animation_list(&mut self, sequence: &mut Sequence, stop_at: usize) {
        let mut removed_ticks: u32 = 0;
        for p in self.pending_animations.iter_mut().skip(stop_at + 1) {
            removed_ticks = removed_ticks.wrapping_add(p.num_anims);
            p.num_anims = 0;
        }
        sequence.remove_link_animations(removed_ticks as i32);
    }

    /// One animation finished: count it, and report every queued motion
    /// whose count is reached.
    pub fn animation_done(&mut self, success: bool, sink: &mut dyn MotionDoneSink) {
        if self.pending_animations.is_empty() {
            return;
        }
        self.animation_counter = self.animation_counter.wrapping_add(1);
        while let Some(&head) = self.pending_animations.front() {
            // Counts are compared signed, so a wrapped count completes.
            let num_anims = head.num_anims as i32;
            if num_anims > self.animation_counter {
                break;
            }
            if head.motion & ACTION_BIT != 0 {
                self.state.remove_action_head();
            }
            sink.motion_done(head.motion, success);
            self.animation_counter = self.animation_counter.wrapping_sub(num_anims);
            self.pending_animations.pop_front();
        }
        if self.animation_counter != 0 && self.pending_animations.is_empty() {
            self.animation_counter = 0;
        }
    }

    /// Report every queued motion at the head that waits for nothing.
    pub fn check_for_completed_motions(&mut self, sink: &mut dyn MotionDoneSink) {
        while let Some(&head) = self.pending_animations.front() {
            if head.num_anims != 0 {
                break;
            }
            if head.motion & ACTION_BIT != 0 {
                self.state.remove_action_head();
            }
            sink.motion_done(head.motion, true);
            self.pending_animations.pop_front();
        }
    }

    /// Per-tick work: [`MotionTableManager::check_for_completed_motions`].
    pub fn use_time(&mut self, sink: &mut dyn MotionDoneSink) {
        self.check_for_completed_motions(sink);
    }

    /// Reset to the table's default state and queue ready.
    pub fn initialize_state(&mut self, sequence: &mut Sequence) {
        let ticks = self
            .table
            .as_ref()
            .and_then(|t| t.set_default_state(&mut self.state, sequence))
            .unwrap_or(0);
        self.add_to_queue(sequence, READY_SENTINEL, ticks);
    }

    /// Entering the world: drop the link animations and abandon every
    /// queued motion.
    pub fn handle_enter_world(&mut self, sequence: &mut Sequence, sink: &mut dyn MotionDoneSink) {
        sequence.remove_all_link_animations();
        self.drain_queue(sink);
    }

    /// Leaving the world: abandon every queued motion.
    pub fn handle_exit_world(&mut self, sink: &mut dyn MotionDoneSink) {
        self.drain_queue(sink);
    }

    fn drain_queue(&mut self, sink: &mut dyn MotionDoneSink) {
        while !self.pending_animations.is_empty() {
            self.animation_done(false, sink);
        }
    }

    /// Apply a movement request to the motion table and queue the result.
    pub fn perform_movement(
        &mut self,
        sequence: &mut Sequence,
        movement: MotionTableMovement,
    ) -> Result<(), MotionTableError> {
        let Some(table) = self.table.clone() else {
            return Err(MotionTableError::NoTable);
        };
        match movement.kind {
            MovementType::InterpretedCommand => {
                let ticks = table
                    .do_object_motion(movement.motion, &mut self.state, sequence, movement.speed)
                    .ok_or(MotionTableError::MotionFailed)?;
                self.add_to_queue(sequence, movement.motion, ticks);
                Ok(())
            }
            MovementType::StopInterpretedCommand => {
                let ticks = table
                    .stop_object_motion(movement.motion, movement.speed, &mut self.state, sequence)
                    .ok_or(MotionTableError::MotionFailed)?;
                self.add_to_queue(sequence, READY_SENTINEL, ticks);
                Ok(())
            }
            MovementType::StopCompletely => {
                // Ready is queued whether or not anything stopped.
                let ticks = table
                    .stop_object_completely(&mut self.state, sequence)
                    .unwrap_or(0);
                self.add_to_queue(sequence, READY_SENTINEL, ticks);
                Ok(())
            }
            _ => Err(MotionTableError::NotHandled),
        }
    }
}

#[cfg(test)]
mod tests;
