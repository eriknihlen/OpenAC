//! The options that come with a movement request: what the mover may do
//! (walk, run, sidestep, ...), how close to get, how fast, and which state
//! the request should change.

use crate::motion_command::{RUN_FORWARD, WALK_BACKWARDS, WALK_FORWARD};
use crate::raw_motion_state::HoldKey;

/// Movement request options. Defaults are the game's (bitfield `0x1EE0F`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MovementParameters {
    /// May walk (bit `0x1`).
    pub can_walk: bool,
    /// May run (bit `0x2`).
    pub can_run: bool,
    /// May sidestep (bit `0x4`).
    pub can_sidestep: bool,
    /// May walk backwards (bit `0x8`).
    pub can_walk_backwards: bool,
    /// Always runs, however close (bit `0x10`).
    pub can_charge: bool,
    /// Fails instead of walking (bit `0x20`).
    pub fail_walk: bool,
    /// Turns to `desired_heading` on arrival (bit `0x40`).
    pub use_final_heading: bool,
    /// Sticks to the target on arrival (bit `0x80`).
    pub sticky: bool,
    /// Moves away from the target (bit `0x100`).
    pub move_away: bool,
    /// Moves towards the target (bit `0x200`).
    pub move_towards: bool,
    /// Measures distance between spheres, not centres (bit `0x400`).
    pub use_spheres: bool,
    /// Leaves the hold key unset (bit `0x800`).
    pub set_hold_key: bool,
    /// The request came from the object itself (bit `0x1000`).
    pub autonomous: bool,
    /// Mirror the motion into the raw state (bit `0x2000`).
    pub modify_raw_state: bool,
    /// Mirror the motion into the interpreted state (bit `0x4000`).
    pub modify_interpreted_state: bool,
    /// Cancel a move-to in progress (bit `0x8000`).
    pub cancel_move_to: bool,
    /// Stop completely first (bit `0x10000`).
    pub stop_completely: bool,
    /// No jumping while a link animation plays (bit `0x20000`).
    pub disable_jump_during_link: bool,
    /// Arrival distance, metres.
    pub distance_to_object: f32,
    /// Minimum distance, metres.
    pub min_distance: f32,
    /// Heading to end on, degrees.
    pub desired_heading: f32,
    /// Speed factor.
    pub speed: f32,
    /// Give up beyond this distance, metres.
    pub fail_distance: f32,
    /// Beyond this distance past arrival the mover runs, metres.
    pub walk_run_threshold: f32,
    /// Request context id.
    pub context_id: u32,
    /// Hold key applied when `set_hold_key` is clear.
    pub hold_key_to_apply: HoldKey,
    /// Action sequence stamp.
    pub action_stamp: u32,
}

impl Default for MovementParameters {
    fn default() -> Self {
        Self {
            can_walk: true,
            can_run: true,
            can_sidestep: true,
            can_walk_backwards: true,
            can_charge: false,
            fail_walk: false,
            use_final_heading: false,
            sticky: false,
            move_away: false,
            move_towards: true,
            use_spheres: true,
            set_hold_key: true,
            autonomous: false,
            modify_raw_state: true,
            modify_interpreted_state: true,
            cancel_move_to: true,
            stop_completely: true,
            disable_jump_during_link: false,
            distance_to_object: 0.6,
            min_distance: 0.0,
            desired_heading: 0.0,
            speed: 1.0,
            fail_distance: f32::MAX,
            walk_run_threshold: 15.0,
            context_id: 0,
            hold_key_to_apply: HoldKey::Invalid,
            action_stamp: 0,
        }
    }
}

impl MovementParameters {
    /// Parameters from a move-to message: flag bits and the six distances,
    /// speeds and heading in wire order.
    #[must_use]
    pub fn from_wire(
        bitfield: u32,
        distance_to_object: f32,
        min_distance: f32,
        fail_distance: f32,
        speed: f32,
        walk_run_threshold: f32,
        desired_heading: f32,
    ) -> Self {
        let mut p = Self::default();
        p.apply_bitfield(bitfield);
        p.distance_to_object = distance_to_object;
        p.min_distance = min_distance;
        p.fail_distance = fail_distance;
        p.speed = speed;
        p.walk_run_threshold = walk_run_threshold;
        p.desired_heading = desired_heading;
        p
    }

    /// Parameters from a turn-to message: flag bits, speed and heading;
    /// distances keep their defaults.
    #[must_use]
    pub fn from_wire_turn_to(bitfield: u32, speed: f32, desired_heading: f32) -> Self {
        let mut p = Self::default();
        p.apply_bitfield(bitfield);
        p.speed = speed;
        p.desired_heading = desired_heading;
        p
    }

    fn apply_bitfield(&mut self, bits: u32) {
        let on = |mask: u32| bits & mask != 0;
        self.can_walk = on(0x1);
        self.can_run = on(0x2);
        self.can_sidestep = on(0x4);
        self.can_walk_backwards = on(0x8);
        self.can_charge = on(0x10);
        self.fail_walk = on(0x20);
        self.use_final_heading = on(0x40);
        self.sticky = on(0x80);
        self.move_away = on(0x100);
        self.move_towards = on(0x200);
        self.use_spheres = on(0x400);
        self.set_hold_key = on(0x800);
        self.autonomous = on(0x1000);
        self.modify_raw_state = on(0x2000);
        self.modify_interpreted_state = on(0x4000);
        self.cancel_move_to = on(0x8000);
        self.stop_completely = on(0x10000);
        self.disable_jump_during_link = on(0x20000);
    }

    /// The motion to make at distance `dist` (metres) from the target, the
    /// hold key (walk or run) and whether it moves away. 0 means "no
    /// motion needed".
    #[must_use]
    pub fn get_command(&self, dist: f32, _heading_diff: f32) -> (u32, HoldKey, bool) {
        let (motion, moving_away) = if self.move_towards && self.move_away {
            self.towards_and_away(dist)
        } else if self.move_away && !self.move_towards {
            // Away only: walk forward (turned around) inside the minimum.
            if dist < self.min_distance {
                (WALK_FORWARD, true)
            } else {
                (0, false)
            }
        } else if dist > self.distance_to_object {
            (WALK_FORWARD, false)
        } else {
            (0, false)
        };

        let hold_key = if self.can_charge {
            HoldKey::Run
        } else if !self.can_run
            || (self.can_walk && (dist - self.distance_to_object) <= self.walk_run_threshold)
        {
            // Cannot run, or close enough to walk.
            HoldKey::None
        } else {
            HoldKey::Run
        };
        (motion, hold_key, moving_away)
    }

    /// Towards-and-away: approach beyond the arrival distance, back off
    /// inside the minimum distance, otherwise hold.
    #[must_use]
    pub fn towards_and_away(&self, dist: f32) -> (u32, bool) {
        if dist > self.distance_to_object {
            (WALK_FORWARD, false)
        } else if dist - self.min_distance < crate::frame::F_EPSILON {
            (WALK_BACKWARDS, true)
        } else {
            (0, false)
        }
    }

    /// The heading offset (degrees) the mover faces relative to the
    /// target: 180 when walking forward away or backing up towards it.
    #[must_use]
    pub fn get_desired_heading(&self, command: u32, moving_away: bool) -> f32 {
        if command == RUN_FORWARD || command == WALK_FORWARD {
            if !moving_away {
                return 0.0;
            }
        } else if command != WALK_BACKWARDS || moving_away {
            return 0.0;
        }
        180.0
    }
}

#[cfg(test)]
mod tests {
    //! Golden cases from OpenAC `MovementParametersTests`,
    //! `MovementParametersFromWireTests`, `MovementParametersGetCommandTests`,
    //! `MovementParametersTowardsAndAwayTests` and
    //! `MovementParametersGetDesiredHeadingTests`.
    use super::*;
    use crate::motion_command::TURN_RIGHT;

    fn to_bitfield(p: &MovementParameters) -> u32 {
        [
            p.can_walk,
            p.can_run,
            p.can_sidestep,
            p.can_walk_backwards,
            p.can_charge,
            p.fail_walk,
            p.use_final_heading,
            p.sticky,
            p.move_away,
            p.move_towards,
            p.use_spheres,
            p.set_hold_key,
            p.autonomous,
            p.modify_raw_state,
            p.modify_interpreted_state,
            p.cancel_move_to,
            p.stop_completely,
            p.disable_jump_during_link,
        ]
        .iter()
        .enumerate()
        .fold(0, |acc, (i, &on)| if on { acc | (1 << i) } else { acc })
    }

    #[test]
    fn defaults_match_the_retail_constructor() {
        let p = MovementParameters::default();
        assert_eq!(to_bitfield(&p), 0x1EE0F);
        assert_eq!(p.min_distance, 0.0);
        assert_eq!(p.distance_to_object, 0.6);
        assert_eq!(p.fail_distance, f32::MAX);
        assert_eq!(p.desired_heading, 0.0);
        assert_eq!(p.speed, 1.0);
        assert_eq!(p.walk_run_threshold, 15.0);
        assert_eq!(p.context_id, 0);
        assert_eq!(p.hold_key_to_apply, HoldKey::Invalid);
        assert_eq!(p.action_stamp, 0);
    }

    #[test]
    fn from_wire_sets_every_flag_from_its_bit() {
        let p = MovementParameters::from_wire(0x3FFFF, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0);
        assert_eq!(to_bitfield(&p), 0x3FFFF);
        let p = MovementParameters::from_wire(0, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0);
        assert_eq!(to_bitfield(&p), 0);
        for bit in 0..18 {
            let mask = 1 << bit;
            let p = MovementParameters::from_wire(mask, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
            assert_eq!(to_bitfield(&p), mask);
        }
    }

    #[test]
    fn from_wire_copies_the_scalars_in_wire_order() {
        let p = MovementParameters::from_wire(0, 1.5, 2.5, 3.5, 4.5, 5.5, 6.5);
        assert_eq!(
            (
                p.distance_to_object,
                p.min_distance,
                p.fail_distance,
                p.speed,
                p.walk_run_threshold,
                p.desired_heading
            ),
            (1.5, 2.5, 3.5, 4.5, 5.5, 6.5)
        );
    }

    #[test]
    fn turn_to_form_keeps_the_distance_defaults() {
        let p = MovementParameters::from_wire_turn_to(0x2, 2.0, 90.0);
        assert!(p.can_run);
        assert_eq!((p.speed, p.desired_heading), (2.0, 90.0));
        assert_eq!(p.distance_to_object, 0.6);
        assert_eq!(p.min_distance, 0.0);
        assert_eq!(p.fail_distance, f32::MAX);
        assert_eq!(p.walk_run_threshold, 15.0);
    }

    #[test]
    fn get_command_motion_selection() {
        let plain = MovementParameters::default();
        assert_eq!(plain.get_command(5.0, 0.0).0, WALK_FORWARD);
        assert_eq!(plain.get_command(0.6, 0.0).0, 0);
        assert_eq!(plain.get_command(0.3, 0.0).0, 0);

        let away = MovementParameters {
            move_towards: false,
            move_away: true,
            min_distance: 5.0,
            ..MovementParameters::default()
        };
        let (m, _, a) = away.get_command(2.0, 0.0);
        assert_eq!((m, a), (WALK_FORWARD, true));
        let (m, _, a) = away.get_command(5.0, 0.0);
        assert_eq!((m, a), (0, false));

        let both = MovementParameters {
            move_away: true,
            min_distance: 0.2,
            ..MovementParameters::default()
        };
        let (m, _, a) = both.get_command(5.0, 0.0);
        assert_eq!((m, a), (WALK_FORWARD, false));
        let (m, _, a) = both.get_command(0.2, 0.0);
        assert_eq!((m, a), (WALK_BACKWARDS, true));
        assert_eq!(both.get_command(0.4, 0.0).0, 0);

        let neither = MovementParameters {
            move_towards: false,
            move_away: false,
            ..MovementParameters::default()
        };
        let (m, _, a) = neither.get_command(5.0, 0.0);
        assert_eq!((m, a), (WALK_FORWARD, false));
    }

    #[test]
    fn get_command_hold_key_cascade() {
        let p = |can_run, can_walk, can_charge, threshold| MovementParameters {
            can_run,
            can_walk,
            can_charge,
            walk_run_threshold: threshold,
            ..MovementParameters::default()
        };
        assert_eq!(
            p(false, true, true, 15.0).get_command(0.6, 0.0).1,
            HoldKey::Run
        );
        assert_eq!(
            p(false, true, false, 15.0).get_command(1000.0, 0.0).1,
            HoldKey::None
        );
        assert_eq!(
            p(true, false, false, 15.0).get_command(0.6, 0.0).1,
            HoldKey::Run
        );
        assert_eq!(
            p(true, true, false, 15.0).get_command(10.6, 0.0).1,
            HoldKey::None
        );
        assert_eq!(
            p(true, true, false, 15.0).get_command(15.7, 0.0).1,
            HoldKey::Run
        );
        assert_eq!(
            p(true, true, false, 15.0).get_command(15.6, 0.0).1,
            HoldKey::None
        );
        assert_eq!(
            p(true, true, false, 15.0).get_command(15.600_001, 0.0).1,
            HoldKey::Run
        );
        assert_eq!(
            p(true, true, true, 1000.0).get_command(0.6, 0.0).1,
            HoldKey::Run
        );

        for (can_run, can_walk, can_charge, far, expected) in [
            (true, true, false, false, HoldKey::None),
            (true, true, false, true, HoldKey::Run),
            (true, false, false, false, HoldKey::Run),
            (true, false, false, true, HoldKey::Run),
            (false, true, false, false, HoldKey::None),
            (false, true, false, true, HoldKey::None),
            (false, false, false, false, HoldKey::None),
            (false, false, true, false, HoldKey::Run),
        ] {
            let dist = if far { 20.0 } else { 5.0 };
            assert_eq!(
                p(can_run, can_walk, can_charge, 15.0)
                    .get_command(dist, 0.0)
                    .1,
                expected
            );
        }
    }

    #[test]
    fn towards_and_away_bands() {
        let p = MovementParameters {
            distance_to_object: 0.6,
            min_distance: 0.2,
            ..MovementParameters::default()
        };
        assert_eq!(p.towards_and_away(5.0), (WALK_FORWARD, false));
        assert_eq!(p.towards_and_away(0.6).0, 0);
        assert_eq!(p.towards_and_away(0.2), (WALK_BACKWARDS, true));
        assert_eq!(p.towards_and_away(0.2 + 0.0001), (WALK_BACKWARDS, true));
        assert_eq!(p.towards_and_away(0.4), (0, false));
        assert_eq!(p.towards_and_away(0.2003).0, 0);
    }

    #[test]
    fn desired_heading_quadrants() {
        let p = MovementParameters::default();
        assert_eq!(p.get_desired_heading(RUN_FORWARD, false), 0.0);
        assert_eq!(p.get_desired_heading(RUN_FORWARD, true), 180.0);
        assert_eq!(p.get_desired_heading(WALK_FORWARD, false), 0.0);
        assert_eq!(p.get_desired_heading(WALK_FORWARD, true), 180.0);
        assert_eq!(p.get_desired_heading(WALK_BACKWARDS, false), 180.0);
        assert_eq!(p.get_desired_heading(WALK_BACKWARDS, true), 0.0);
        assert_eq!(p.get_desired_heading(TURN_RIGHT, false), 0.0);
        assert_eq!(p.get_desired_heading(0, true), 0.0);
    }
}
