//! Scripted movement: moves the client carries out one quantum at a time,
//! on three channels that combine the way held movement keys do (travel,
//! strafe, turn), and charged jumps.
//!
//! The runtime asks [`ScriptedMovement::advance`] once per quantum, before
//! the player's movement input is read. It returns the input the body acts
//! on while a move or jump is held, or `None` when nothing scripted is held
//! and the player's own input applies untouched.

use ac_physics::movement::MovementInput;
use glam::{Vec2, Vec3};

/// Which way a scripted move goes.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum MoveDirection {
    /// Forward.
    Forward,
    /// Backward.
    Backward,
    /// Sideways to the left.
    StrafeLeft,
    /// Sideways to the right.
    StrafeRight,
    /// Turning left (anticlockwise seen from above).
    TurnLeft,
    /// Turning right (clockwise seen from above).
    TurnRight,
}

/// How fast a scripted move goes.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum MovePace {
    /// At a walk.
    Walk,
    /// At a run.
    Run,
}

/// What a scripted move's amount counts.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum MoveUnit {
    /// Metres going forward, backward or sideways, and degrees for a turn.
    #[default]
    MetersOrDegrees,
    /// Seconds.
    Seconds,
}

/// The parts of movement that combine the way held movement keys do. Each
/// carries one scripted move.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum MoveChannel {
    /// Going forward or backward.
    Travel,
    /// Going sideways.
    Strafe,
    /// Turning.
    Turn,
}

/// Where a channel's most recent scripted move stands.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum ScriptedMoveState {
    /// No move yet.
    #[default]
    None,
    /// Under way.
    Moving,
    /// It covered its distance, angle or time.
    Completed,
    /// A stop command ended it.
    Stopped,
    /// Time ran out: the limit for a move without an amount, or a move too
    /// slow to cover its distance or angle.
    TimeLimit,
    /// It stopped making progress.
    Blocked,
    /// The player moved the character.
    Interrupted,
    /// The character entered portal space or left the world.
    Lost,
}

/// A move for the client to carry out: a direction, a pace, and an amount
/// that counts `unit`. An amount of zero keeps going until stopped.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MoveRequest {
    /// Which way.
    pub direction: MoveDirection,
    /// How fast.
    pub pace: MovePace,
    /// How much; zero keeps going until stopped (at most 30 s).
    pub amount: f32,
    /// What the amount counts.
    pub unit: MoveUnit,
}

impl Default for MoveRequest {
    fn default() -> Self {
        Self::new(MoveDirection::Forward, MovePace::Walk, 0.0)
    }
}

impl MoveRequest {
    /// A move of `amount` metres or degrees.
    #[must_use]
    pub const fn new(direction: MoveDirection, pace: MovePace, amount: f32) -> Self {
        Self {
            direction,
            pace,
            amount,
            unit: MoveUnit::MetersOrDegrees,
        }
    }

    /// A move of `amount` seconds.
    #[must_use]
    pub const fn for_seconds(direction: MoveDirection, pace: MovePace, amount: f32) -> Self {
        Self {
            direction,
            pace,
            amount,
            unit: MoveUnit::Seconds,
        }
    }

    /// The channel the move goes on.
    #[must_use]
    pub const fn channel(&self) -> MoveChannel {
        match self.direction {
            MoveDirection::Forward | MoveDirection::Backward => MoveChannel::Travel,
            MoveDirection::StrafeLeft | MoveDirection::StrafeRight => MoveChannel::Strafe,
            MoveDirection::TurnLeft | MoveDirection::TurnRight => MoveChannel::Turn,
        }
    }
}

/// One channel's most recent scripted move. `covered` is metres, or degrees
/// for a turn, whatever the move's unit.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct MoveChannelSnapshot {
    /// The move's sequence number.
    pub sequence: i64,
    /// Where it stands.
    pub state: ScriptedMoveState,
    /// What was asked.
    pub request: MoveRequest,
    /// How far it got.
    pub covered: f32,
    /// How long it has taken, seconds.
    pub elapsed_seconds: f32,
}

/// The most recent scripted move on each channel, and the most recent jump.
/// Move sequences grow by one for every move begun on any channel, and
/// `jump_sequence` by one for every jump.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ScriptedMoveSnapshot {
    /// The travel channel.
    pub travel: MoveChannelSnapshot,
    /// The strafe channel.
    pub strafe: MoveChannelSnapshot,
    /// The turn channel.
    pub turn: MoveChannelSnapshot,
    /// How many jumps have been begun.
    pub jump_sequence: i64,
    /// Whether a jump is charging.
    pub jump_charging: bool,
}

impl ScriptedMoveSnapshot {
    /// One channel's snapshot.
    #[must_use]
    pub fn channel(&self, channel: MoveChannel) -> MoveChannelSnapshot {
        match channel {
            MoveChannel::Travel => self.travel,
            MoveChannel::Strafe => self.strafe,
            MoveChannel::Turn => self.turn,
        }
    }
}

/// One quantum's view of the body, with `offset_from_start` measured from
/// where it stood when scripted movement last began.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ScriptedMoveSample {
    /// Where the body is, from where it stood when scripted movement began.
    pub offset_from_start: Vec3,
    /// Its compass heading, degrees clockwise from north.
    pub heading_degrees: f32,
    /// The session clock, seconds.
    pub time_seconds: f64,
    /// Whether the character is in portal space.
    pub in_portal_space: bool,
    /// Whether the player holds a movement key this quantum.
    pub manual_movement_held: bool,
}

/// What [`ScriptedMovement::advance`] wants this quantum: the input the body
/// acts on (`None` when the player's own input applies), and the compass
/// heading to set the body to at once when a turn has just passed its angle.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ScriptedMoveOutput {
    /// The input to act on, or `None` for the player's own.
    pub input: Option<MovementInput>,
    /// The heading to snap to, degrees.
    pub heading: Option<f32>,
}

/// The longest distance a move may ask for, metres.
pub const MAXIMUM_MOVE_METERS: f32 = 500.0;
/// The largest angle a turn may ask for, degrees.
pub const MAXIMUM_TURN_DEGREES: f32 = 3600.0;
/// The longest time a move may ask for, seconds.
pub const MAXIMUM_MOVE_SECONDS: f32 = 300.0;
/// How long a move with no amount lasts, seconds.
pub const OPEN_ENDED_SECONDS: f64 = 30.0;
/// How often a move's progress is checked, seconds.
pub const PROGRESS_WINDOW_SECONDS: f64 = 1.5;
/// The least a move must cover in each window, metres.
pub const MINIMUM_PROGRESS_METERS: f32 = 0.3;
/// The least a turn must cover in each window, degrees.
pub const MINIMUM_PROGRESS_DEGREES: f32 = 5.0;
/// The slowest a move with an amount may go before it runs out of time.
pub const SLOWEST_METERS_PER_SECOND: f32 = 1.0;
/// The slowest a turn with an amount may go before it runs out of time.
pub const SLOWEST_DEGREES_PER_SECOND: f32 = 30.0;
/// How long a full jump charge takes, seconds.
pub const FULL_JUMP_CHARGE_SECONDS: f64 = 1.0;
/// A distance ends within this of its amount, or half a step.
pub const ARRIVAL_TOLERANCE_METERS: f32 = 0.05;
/// A body step longer than this in one quantum is a correction, not
/// movement, metres.
pub const LONGEST_STEP_METERS: f32 = 10.0;

/// One channel's move.
#[derive(Debug, Clone, Copy, Default)]
struct Channel {
    started: bool,
    started_at: f64,
    checkpoint_at: f64,
    checkpoint_covered: f32,
    /// The latest quantum's progress, kept across moves as the size of the
    /// next step.
    last_step: f32,
    sequence: i64,
    state: ScriptedMoveState,
    request: MoveRequest,
    covered: f32,
    elapsed: f64,
}

impl Channel {
    fn is_moving(&self) -> bool {
        self.state == ScriptedMoveState::Moving
    }

    fn snapshot(&self) -> MoveChannelSnapshot {
        MoveChannelSnapshot {
            sequence: self.sequence,
            state: self.state,
            request: self.request,
            covered: self.covered,
            elapsed_seconds: self.elapsed as f32,
        }
    }

    fn holds(&self, direction: MoveDirection) -> bool {
        self.is_moving() && self.request.direction == direction
    }

    fn begin(&mut self, sequence: i64, request: MoveRequest) {
        self.sequence = sequence;
        self.state = ScriptedMoveState::Moving;
        self.request = request;
        self.covered = 0.0;
        self.elapsed = 0.0;
        self.started = false;
    }

    fn end(&mut self, state: ScriptedMoveState) -> bool {
        if !self.is_moving() {
            return false;
        }
        self.state = state;
        true
    }

    /// Counts one quantum's progress toward the move and ends it when it is
    /// done. A distance ends within half a step of its amount. Returns how
    /// far a turn went past its angle on the quantum it arrives, so the body
    /// can land on the angle.
    fn advance(&mut self, time: f64, toward: f32) -> Option<f32> {
        if !self.started {
            self.started = true;
            self.started_at = time;
            self.checkpoint_at = time;
            self.checkpoint_covered = 0.0;
            return None;
        }

        let turn = self.request.channel() == MoveChannel::Turn;
        let progress = toward.max(0.0);
        self.covered += progress;
        if progress > 0.0 {
            self.last_step = progress;
        }
        self.elapsed = time - self.started_at;

        if self.request.amount > 0.0 {
            if self.request.unit == MoveUnit::Seconds {
                if self.elapsed >= f64::from(self.request.amount) {
                    self.state = ScriptedMoveState::Completed;
                    return None;
                }
            } else if turn {
                if self.covered >= self.request.amount {
                    let overshoot = self.covered - self.request.amount;
                    self.covered = self.request.amount;
                    self.state = ScriptedMoveState::Completed;
                    return Some(overshoot);
                }
            } else if self.covered
                >= self.request.amount - ARRIVAL_TOLERANCE_METERS.max(self.last_step * 0.5)
            {
                self.state = ScriptedMoveState::Completed;
                return None;
            }
        }

        let limit = if self.request.amount <= 0.0 {
            OPEN_ENDED_SECONDS
        } else if self.request.unit == MoveUnit::Seconds {
            f64::INFINITY
        } else {
            f64::from(
                self.request.amount
                    / if turn {
                        SLOWEST_DEGREES_PER_SECOND
                    } else {
                        SLOWEST_METERS_PER_SECOND
                    },
            ) + PROGRESS_WINDOW_SECONDS * 2.0
        };
        if self.elapsed >= limit {
            self.state = ScriptedMoveState::TimeLimit;
            return None;
        }

        if time - self.checkpoint_at >= PROGRESS_WINDOW_SECONDS {
            let least = if turn {
                MINIMUM_PROGRESS_DEGREES
            } else {
                MINIMUM_PROGRESS_METERS
            };
            if self.covered - self.checkpoint_covered < least {
                self.state = ScriptedMoveState::Blocked;
                return None;
            }
            self.checkpoint_at = time;
            self.checkpoint_covered = self.covered;
        }
        None
    }
}

/// Carries out scripted moves one quantum at a time. A travel, a strafe and
/// a turn move are held together, each until its distance, angle or time is
/// covered, and each ends early when it stops making progress or runs out
/// of time. Travel and strafe progress is measured along and across the
/// body's facing, so arcs and diagonals count their whole path. A turn that
/// passes its angle reports the exact heading to land on. The player moving
/// the character, or portal space, ends every move. A jump is held for its
/// charge and then released, alongside any moves.
#[derive(Debug, Clone, Default)]
pub struct ScriptedMovement {
    channels: [Channel; 3],
    sequence: i64,
    jump_sequence: i64,
    jump_charging: bool,
    jump_hold_seconds: f64,
    /// The power the jump under way was asked for, which it is released at
    /// exactly.
    jump_power: f32,
    jump_release_at: Option<f64>,
    jump_leave_at: Option<MovePace>,
    held_last_frame: bool,
    /// Whether the input last held ran. Letting go of a move keeps that pace
    /// for the quantum it ends, since a walk let go of with run held stops
    /// with a lurch well past where a walk stops.
    held_run: bool,
    sampled: bool,
    last_offset: Vec2,
    last_heading: f32,
}

fn channel_index(channel: MoveChannel) -> usize {
    match channel {
        MoveChannel::Travel => 0,
        MoveChannel::Strafe => 1,
        MoveChannel::Turn => 2,
    }
}

impl ScriptedMovement {
    /// No moves yet.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The most recent move on each channel and the most recent jump.
    #[must_use]
    pub fn snapshot(&self) -> ScriptedMoveSnapshot {
        ScriptedMoveSnapshot {
            travel: self.channels[0].snapshot(),
            strafe: self.channels[1].snapshot(),
            turn: self.channels[2].snapshot(),
            jump_sequence: self.jump_sequence,
            jump_charging: self.jump_charging,
        }
    }

    /// Whether a move or jump is held, or a release still has to reach the
    /// body.
    #[must_use]
    pub fn is_active(&self) -> bool {
        self.is_moving() || self.jump_charging || self.held_last_frame
    }

    fn is_moving(&self) -> bool {
        self.channels.iter().any(Channel::is_moving)
    }

    /// Whether a request is one a move can be begun with: a finite amount,
    /// not below zero and at most its limit.
    #[must_use]
    pub fn is_valid(request: &MoveRequest) -> bool {
        if !request.amount.is_finite() || request.amount < 0.0 {
            return false;
        }
        let most = if request.unit == MoveUnit::Seconds {
            MAXIMUM_MOVE_SECONDS
        } else if request.channel() == MoveChannel::Turn {
            MAXIMUM_TURN_DEGREES
        } else {
            MAXIMUM_MOVE_METERS
        };
        request.amount <= most
    }

    /// Begins a move on its channel, replacing only a move already on that
    /// channel. False for a request that is not valid.
    pub fn begin(&mut self, request: MoveRequest) -> bool {
        if !Self::is_valid(&request) {
            return false;
        }
        self.sequence += 1;
        let sequence = self.sequence;
        self.channels[channel_index(request.channel())].begin(sequence, request);
        true
    }

    /// Ends every move in progress; false when none was.
    pub fn stop(&mut self) -> bool {
        self.end_all(ScriptedMoveState::Stopped)
    }

    /// Ends the move on one channel, if there is one.
    pub fn stop_channel(&mut self, channel: MoveChannel) -> bool {
        self.channels[channel_index(channel)].end(ScriptedMoveState::Stopped)
    }

    /// Ends every move because the player moved the character.
    pub fn interrupt(&mut self) -> bool {
        self.end_all(ScriptedMoveState::Interrupted)
    }

    /// Ends every move and any charging jump, because the body is in portal
    /// space or gone.
    pub fn lose(&mut self) -> bool {
        let jump = self.jump_charging;
        self.jump_charging = false;
        self.jump_release_at = None;
        self.jump_leave_at = None;
        self.end_all(ScriptedMoveState::Lost) | jump
    }

    /// Charges a jump for `power` of a full charge, then releases it. With
    /// `leave_at`, the body charges standing and presses forward at that pace
    /// from the quantum the jump releases, so it leaves the ground from where
    /// it stood, moving.
    pub fn begin_jump(&mut self, power: f32, leave_at: Option<MovePace>) -> bool {
        if !power.is_finite() || power <= 0.0 || power > 1.0 || self.jump_charging {
            return false;
        }
        self.jump_hold_seconds = f64::from(power) * FULL_JUMP_CHARGE_SECONDS;
        self.jump_power = power;
        self.jump_release_at = None;
        self.jump_leave_at = leave_at;
        self.jump_sequence += 1;
        self.jump_charging = true;
        true
    }

    /// Advances by one quantum.
    pub fn advance(&mut self, sample: &ScriptedMoveSample) -> ScriptedMoveOutput {
        let mut output = ScriptedMoveOutput::default();
        if !self.is_active() {
            return output;
        }

        let offset = sample.offset_from_start.truncate();
        let step = if self.sampled {
            offset - self.last_offset
        } else {
            Vec2::ZERO
        };
        let turned = if self.sampled {
            signed_degrees(sample.heading_degrees - self.last_heading)
        } else {
            0.0
        };
        self.last_offset = offset;
        self.last_heading = sample.heading_degrees;
        self.sampled = true;

        if sample.in_portal_space {
            self.lose();
        } else if self.is_moving() {
            if sample.manual_movement_held {
                self.interrupt();
            } else {
                output.heading = self.measure(sample, step, turned);
            }
        }

        let mut jump = false;
        let mut released = None;
        if self.jump_charging {
            let release_at = *self
                .jump_release_at
                .get_or_insert(sample.time_seconds + self.jump_hold_seconds);
            if sample.time_seconds >= release_at {
                released = Some(self.jump_power);
                self.jump_charging = false;
                self.jump_release_at = None;
                if let Some(pace) = self.jump_leave_at {
                    self.begin(MoveRequest::new(MoveDirection::Forward, pace, 0.0));
                }
                self.jump_leave_at = None;
            } else {
                jump = true;
            }
        }

        if self.is_moving() || jump {
            let held = MovementInput {
                jump_extent: released,
                ..self.hold(jump)
            };
            self.held_run = held.run;
            output.input = Some(held);
            self.held_last_frame = true;
        } else if self.held_last_frame {
            output.input = Some(MovementInput {
                run: self.held_run,
                is_persistent_command: true,
                jump_extent: released,
                ..MovementInput::default()
            });
            self.held_last_frame = false;
        }
        if !self.is_active() {
            self.sampled = false;
        }
        output
    }

    fn measure(&mut self, sample: &ScriptedMoveSample, step: Vec2, turned: f32) -> Option<f32> {
        let step = if step.length() > LONGEST_STEP_METERS {
            Vec2::ZERO
        } else {
            step
        };
        let radians = sample.heading_degrees * (std::f32::consts::PI / 180.0);
        let facing = Vec2::new(radians.sin(), radians.cos());
        let ahead = step.dot(facing);
        let aside = step.dot(Vec2::new(facing.y, -facing.x));

        let mut heading = None;
        for channel in &mut self.channels {
            if !channel.is_moving() {
                continue;
            }
            let direction = channel.request.direction;
            let toward = match direction {
                MoveDirection::Forward => ahead,
                MoveDirection::Backward => -ahead,
                MoveDirection::StrafeRight => aside,
                MoveDirection::StrafeLeft => -aside,
                MoveDirection::TurnRight => turned,
                MoveDirection::TurnLeft => -turned,
            };
            if let Some(overshoot) = channel.advance(sample.time_seconds, toward) {
                heading = Some(normalized_degrees(
                    if direction == MoveDirection::TurnRight {
                        sample.heading_degrees - overshoot
                    } else {
                        sample.heading_degrees + overshoot
                    },
                ));
            }
        }
        heading
    }

    fn hold(&self, jump: bool) -> MovementInput {
        let [travel, strafe, turn] = &self.channels;
        let pace = if travel.is_moving() {
            travel.request.pace
        } else if strafe.is_moving() {
            strafe.request.pace
        } else if turn.is_moving() {
            turn.request.pace
        } else {
            MovePace::Run
        };
        MovementInput {
            forward: travel.holds(MoveDirection::Forward),
            backward: travel.holds(MoveDirection::Backward),
            strafe_left: strafe.holds(MoveDirection::StrafeLeft),
            strafe_right: strafe.holds(MoveDirection::StrafeRight),
            turn_left: turn.holds(MoveDirection::TurnLeft),
            turn_right: turn.holds(MoveDirection::TurnRight),
            run: pace == MovePace::Run,
            jump,
            is_persistent_command: true,
            ..MovementInput::default()
        }
    }

    fn end_all(&mut self, state: ScriptedMoveState) -> bool {
        let mut ended = false;
        for channel in &mut self.channels {
            ended |= channel.end(state);
        }
        ended
    }
}

/// An angle as the signed turn from -180 to 180 degrees.
#[must_use]
pub fn signed_degrees(degrees: f32) -> f32 {
    ((degrees % 360.0) + 540.0) % 360.0 - 180.0
}

/// An angle as a compass heading from 0 to 360 degrees.
#[must_use]
pub fn normalized_degrees(degrees: f32) -> f32 {
    ((degrees % 360.0) + 360.0) % 360.0
}
