//! The route driver against a simulated body that carries out scripted
//! moves the way the client does, at fixed speeds.
//!
//! Ported from OpenAC `tests/AcDream.Runtime.Tests/Gameplay/RuntimeRouteDriverTests.cs`
//! and `RuntimeRouteDriverTakeoffTests.cs` (one partial class there).

use std::cell::{Cell, RefCell};

use ac_nav::driver::{
    AT_ITS_TAKEOFF_RADIUS, LANDING_RADIUS, MOST_TAKEOFF_ADJUSTMENTS, NO_AIM_TAKEOFF_RADIUS,
    RouteDriveHooks, RouteDriveSample, RouteDriveState, RouteDriveStep, RouteDriver, RouteLeap,
    RouteLeapAim, RouteTurning, TAKEOFF_RADIUS, TURN_IN_PLACE_DEGREES, move_off_degrees,
};
use ac_nav::leaps::TAKEOFF_SLACK;
use ac_nav::motor::{
    MoveChannel, MoveChannelSnapshot, MoveDirection, MovePace, MoveRequest, ScriptedMoveSnapshot,
    ScriptedMoveState,
};
use glam::{Vec2, Vec3};

const FRAME: f32 = 1.0 / 30.0;

/// How fast the simulated body goes and turns.
const TURNING: RouteTurning = RouteTurning {
    run_speed: 4.0,
    run_turn_degrees_per_second: 90.0,
    walk_speed: 0.0,
    walk_turn_degrees_per_second: 0.0,
};

fn v(x: f32, y: f32, z: f32) -> Vec3 {
    Vec3::new(x, y, z)
}

fn flat(point: Vec3) -> Vec2 {
    point.truncate()
}

fn leap(leg_index: i32, power: f32, run: bool) -> RouteLeap {
    RouteLeap {
        leg_index,
        power,
        run,
    }
}

fn driver(legs: &[Vec3]) -> RouteDriver {
    RouteDriver::new(legs, &[], false).unwrap()
}

fn leaping_driver(legs: &[Vec3], leaps: &[RouteLeap]) -> RouteDriver {
    RouteDriver::new(legs, leaps, false).unwrap()
}

fn none() -> RouteDriveHooks<'static> {
    RouteDriveHooks::default()
}

fn in_range(value: f32, low: f32, high: f32) {
    assert!(
        value >= low && value <= high,
        "{value} is not within {low}..{high}"
    );
}

fn first_jump(steps: &[RouteDriveStep]) -> Option<usize> {
    steps.iter().position(|step| step.jump.is_some())
}

fn single_jump(steps: &[RouteDriveStep]) -> RouteDriveStep {
    let jumps: Vec<_> = steps.iter().filter(|step| step.jump.is_some()).collect();
    assert_eq!(jumps.len(), 1, "{} jumps", jumps.len());
    *jumps[0]
}

fn single(points: &[Vec2]) -> Vec2 {
    assert_eq!(points.len(), 1, "{points:?}");
    points[0]
}

fn drive(
    driver: &mut RouteDriver,
    body: &mut SimulatedBody,
    seconds: f32,
    hooks: &RouteDriveHooks<'_>,
    mut path: Option<&mut Vec<Vec2>>,
) -> Vec<RouteDriveStep> {
    let mut steps = Vec::new();
    let mut time = 0.0f32;
    while time < seconds && driver.state() == RouteDriveState::Driving {
        let step = driver.advance(&body.sample(), hooks);
        steps.push(step);
        body.apply(step);
        body.integrate(FRAME);
        if let Some(path) = path.as_deref_mut() {
            path.push(body.position);
        }
        time += FRAME;
    }
    steps
}

/// A body that carries out scripted moves the way the client does, at fixed
/// speeds. It stands on `floor`, and a jump it charges while standing still
/// leaves the ground at the pace of the move pressed as it leaves.
struct SimulatedBody {
    sequence: i64,
    travel: MoveChannelSnapshot,
    strafe: MoveChannelSnapshot,
    turn: MoveChannelSnapshot,
    turn_remaining: f32,
    charge_left: f32,
    charge_power: f32,
    charge_pace: Option<MovePace>,
    slide_left: f32,
    slide_total: f32,
    slide_speed: f32,
    velocity: Vec3,
    air_height: f32,
    leave_ground_left: f32,
    position: Vec2,
    /// The height of the ground under a point; level ground at zero when
    /// unset.
    floor: Option<Box<dyn Fn(Vec2) -> f32>>,
    cannot_jump: bool,
    airborne: bool,
    jumps: i32,
    heading: f32,
    stuck: bool,
    in_portal_space: bool,
    turning: Option<RouteTurning>,
    longest_travel_seconds: f32,
    travels_begun_while_charging: i32,
    /// How long the body slides on, slowing, after a travel stops.
    slide_seconds: f32,
    walk_stop_meters: f32,
    run_stop_meters: f32,
    /// Where the body stood as each jump began to charge.
    charged_at: Vec<Vec2>,
    /// How long after a jump releases the body leaves the ground.
    leave_ground_seconds: f32,
    /// How long the body slides on, slowing, after it comes down.
    landing_slide_seconds: f32,
    travels_begun_while_sliding: i32,
    jumps_charged_while_sliding: i32,
    landings_pressing_forward: i32,
}

const RUN_SPEED: f32 = 4.0;
const WALK_SPEED: f32 = 1.5;
const TURN_SPEED: f32 = 90.0;
const GRAVITY: f32 = 9.8;
const FULL_JUMP_HEIGHT: f32 = 4.2;

impl Default for SimulatedBody {
    fn default() -> Self {
        Self {
            sequence: 0,
            travel: MoveChannelSnapshot::default(),
            strafe: MoveChannelSnapshot::default(),
            turn: MoveChannelSnapshot::default(),
            turn_remaining: 0.0,
            charge_left: -1.0,
            charge_power: 0.0,
            charge_pace: None,
            slide_left: 0.0,
            slide_total: 0.0,
            slide_speed: 0.0,
            velocity: Vec3::ZERO,
            air_height: 0.0,
            leave_ground_left: 0.0,
            position: Vec2::ZERO,
            floor: None,
            cannot_jump: false,
            airborne: false,
            jumps: 0,
            heading: 0.0,
            stuck: false,
            in_portal_space: false,
            turning: None,
            longest_travel_seconds: 0.0,
            travels_begun_while_charging: 0,
            slide_seconds: 0.0,
            walk_stop_meters: 0.0,
            run_stop_meters: 0.0,
            charged_at: Vec::new(),
            leave_ground_seconds: 0.0,
            landing_slide_seconds: 0.0,
            travels_begun_while_sliding: 0,
            jumps_charged_while_sliding: 0,
            landings_pressing_forward: 0,
        }
    }
}

impl SimulatedBody {
    fn on(floor: impl Fn(Vec2) -> f32 + 'static) -> Self {
        Self {
            floor: Some(Box::new(floor)),
            ..Self::default()
        }
    }

    fn sliding(mut self, slide_seconds: f32, walk_stop: f32, run_stop: f32) -> Self {
        self.slide_seconds = slide_seconds;
        self.walk_stop_meters = walk_stop;
        self.run_stop_meters = run_stop;
        self
    }

    fn ground(&self, at: Vec2) -> f32 {
        self.floor.as_ref().map_or(0.0, |floor| floor(at))
    }

    fn height(&self) -> f32 {
        if self.airborne {
            self.air_height
        } else {
            self.ground(self.position)
        }
    }

    fn travelling(&self) -> bool {
        self.travel.state == ScriptedMoveState::Moving
    }

    fn sample(&self) -> RouteDriveSample {
        RouteDriveSample {
            position: self.position.extend(self.height()),
            heading_degrees: self.heading,
            moves: ScriptedMoveSnapshot {
                travel: self.travel,
                strafe: self.strafe,
                turn: self.turn,
                jump_sequence: 0,
                jump_charging: self.charge_left >= 0.0,
            },
            in_portal_space: self.in_portal_space,
            airborne: self.airborne,
            turning: self.turning,
            still: self.slide_left <= 0.0
                && !self.airborne
                && self.strafe.state != ScriptedMoveState::Moving,
            walk_stop_meters: self.walk_stop_meters,
            run_stop_meters: self.run_stop_meters,
        }
    }

    fn end_travel_before_drive(&mut self, state: ScriptedMoveState) {
        self.sequence += 1;
        self.travel = MoveChannelSnapshot {
            sequence: self.sequence,
            state,
            request: MoveRequest::new(MoveDirection::Forward, MovePace::Run, 0.0),
            covered: 0.0,
            elapsed_seconds: 0.0,
        };
    }

    fn interrupt(&mut self) {
        for channel in [&mut self.travel, &mut self.strafe, &mut self.turn] {
            if channel.state == ScriptedMoveState::Moving {
                channel.state = ScriptedMoveState::Interrupted;
            }
        }
    }

    fn begun(&mut self, request: MoveRequest) -> MoveChannelSnapshot {
        self.sequence += 1;
        MoveChannelSnapshot {
            sequence: self.sequence,
            state: ScriptedMoveState::Moving,
            request,
            covered: 0.0,
            elapsed_seconds: 0.0,
        }
    }

    fn apply(&mut self, step: RouteDriveStep) {
        if step.stop_travel && self.travel.state == ScriptedMoveState::Moving {
            self.slide_left = self.slide_seconds;
            self.slide_total = self.slide_seconds;
            self.slide_speed = if self.travel.request.pace == MovePace::Run {
                RUN_SPEED
            } else {
                WALK_SPEED
            };
            self.travel.state = ScriptedMoveState::Stopped;
        }
        if step.stop_turn && self.turn.state == ScriptedMoveState::Moving {
            self.turn.state = ScriptedMoveState::Stopped;
        }
        if let Some(travel) = step.travel {
            if self.charge_left >= 0.0 {
                self.travels_begun_while_charging += 1;
            }
            if self.slide_left > 0.0 {
                self.travels_begun_while_sliding += 1;
            }
            // The client puts a move on the channel its direction belongs to,
            // so a sidestep runs on the strafe channel beside a walk.
            if travel.channel() == MoveChannel::Strafe {
                self.strafe = self.begun(travel);
            } else {
                self.travel = self.begun(travel);
            }
        }
        if let Some(turn) = step.turn {
            self.turn = self.begun(turn);
            self.turn_remaining = turn.amount;
        }
        if let Some(power) = step.jump
            && !self.cannot_jump
            && !self.airborne
        {
            if self.slide_left > 0.0 {
                self.jumps_charged_while_sliding += 1;
            }
            self.charged_at.push(self.position);
            self.charge_left = power;
            self.charge_power = power;
            self.charge_pace = step.jump_pace;
            self.jumps += 1;
        }
    }

    /// Takes the jump's speed along the ground from the pace pressed now.
    fn leave_ground(&mut self) {
        let pace = if self.travel.state != ScriptedMoveState::Moving {
            0.0
        } else if self.travel.request.pace == MovePace::Run {
            RUN_SPEED
        } else {
            WALK_SPEED
        };
        let facing = self.heading * std::f32::consts::PI / 180.0;
        self.velocity = Vec3::new(facing.sin() * pace, facing.cos() * pace, self.velocity.z);
    }

    fn integrate(&mut self, seconds: f32) {
        if self.turn.state == ScriptedMoveState::Moving {
            let step = self.turn_remaining.min(TURN_SPEED * seconds);
            self.turn_remaining -= step;
            let signed = if self.turn.request.direction == MoveDirection::TurnRight {
                step
            } else {
                -step
            };
            self.heading = ((self.heading + signed) % 360.0 + 360.0) % 360.0;
            if self.turn_remaining <= 0.0 {
                self.turn.state = ScriptedMoveState::Completed;
                self.turn.covered = self.turn.request.amount;
            }
        }
        if self.airborne {
            if self.leave_ground_left > 0.0 {
                self.leave_ground_left -= seconds;
                if self.leave_ground_left > 0.0 {
                    return;
                }
                self.leave_ground();
            }
            self.position += self.velocity.truncate() * seconds;
            self.velocity.z -= GRAVITY * seconds;
            self.air_height += self.velocity.z * seconds;
            let ground = self.ground(self.position);
            if self.velocity.z < 0.0 && self.air_height <= ground {
                self.airborne = false;
                if self.travel.state == ScriptedMoveState::Moving {
                    self.landings_pressing_forward += 1;
                }
                if self.landing_slide_seconds > 0.0 {
                    self.slide_left = self.landing_slide_seconds;
                    self.slide_total = self.landing_slide_seconds;
                    self.slide_speed = self.velocity.truncate().length();
                }
            }
            if self.travel.state == ScriptedMoveState::Moving {
                self.travel.elapsed_seconds += seconds;
            }
            return;
        }
        if self.charge_left >= 0.0 {
            if self.travel.state == ScriptedMoveState::Moving {
                self.travel.elapsed_seconds += seconds;
            }
            self.charge_left -= seconds;
            if self.charge_left > 0.0 {
                return;
            }
            self.charge_left = -1.0;
            if let Some(leave_at) = self.charge_pace {
                self.travel = self.begun(MoveRequest::new(MoveDirection::Forward, leave_at, 0.0));
            }
            let rise = (2.0 * GRAVITY * (FULL_JUMP_HEIGHT * self.charge_power).max(0.35)).sqrt();
            self.velocity = Vec3::new(0.0, 0.0, rise);
            self.air_height = self.height();
            self.airborne = true;
            self.leave_ground_left = self.leave_ground_seconds;
            if self.leave_ground_left <= 0.0 {
                self.leave_ground();
            }
            return;
        }
        if self.slide_left > 0.0 {
            let radians = self.heading * std::f32::consts::PI / 180.0;
            self.position += Vec2::new(radians.sin(), radians.cos())
                * self.slide_speed
                * (self.slide_left / self.slide_total)
                * seconds;
            self.slide_left -= seconds;
        }
        // A sidestep runs on its own channel, square to the body's heading.
        if self.strafe.state == ScriptedMoveState::Moving {
            let sideways = self.heading * std::f32::consts::PI / 180.0;
            let aside = if self.strafe.request.direction == MoveDirection::StrafeLeft {
                Vec2::new(-sideways.cos(), sideways.sin())
            } else {
                Vec2::new(sideways.cos(), -sideways.sin())
            };
            let step_speed = if self.strafe.request.pace == MovePace::Run {
                RUN_SPEED
            } else {
                WALK_SPEED
            };
            let mut stepped = step_speed * seconds;
            if self.strafe.request.amount > 0.0 {
                stepped = stepped.min(self.strafe.request.amount - self.strafe.covered);
            }
            if !self.stuck {
                self.position += aside * stepped;
            }
            self.strafe.covered += stepped;
            self.strafe.elapsed_seconds += seconds;
            if self.strafe.request.amount > 0.0 && self.strafe.covered >= self.strafe.request.amount
            {
                self.strafe.state = ScriptedMoveState::Completed;
            }
        }
        if self.travel.state != ScriptedMoveState::Moving {
            return;
        }
        let speed = if self.travel.request.pace == MovePace::Run {
            RUN_SPEED
        } else {
            WALK_SPEED
        };
        let radians = self.heading * std::f32::consts::PI / 180.0;
        // A move asked for a distance stops once it has covered it.
        let along = if self.travel.request.direction == MoveDirection::Backward {
            Vec2::new(-radians.sin(), -radians.cos())
        } else {
            Vec2::new(radians.sin(), radians.cos())
        };
        let mut moved = speed * seconds;
        if self.travel.request.amount > 0.0 {
            moved = moved.min(self.travel.request.amount - self.travel.covered);
        }
        if !self.stuck {
            self.position += along * moved;
        }
        self.travel.covered += moved;
        if self.travel.request.amount > 0.0 && self.travel.covered >= self.travel.request.amount {
            self.travel.state = ScriptedMoveState::Completed;
        }
        self.travel.elapsed_seconds += seconds;
        self.longest_travel_seconds = self.longest_travel_seconds.max(self.travel.elapsed_seconds);
        if self.stuck && self.travel.elapsed_seconds > 1.5 {
            self.travel.state = ScriptedMoveState::Blocked;
        }
    }
}

fn is_walk(step: &RouteDriveStep) -> bool {
    step.travel.is_some_and(|t| t.pace == MovePace::Walk)
}

fn is_run(step: &RouteDriveStep) -> bool {
    step.travel.is_some_and(|t| t.pace == MovePace::Run)
}

fn stops_and_turns_right(step: &RouteDriveStep) -> bool {
    step.stop_travel
        && step
            .turn
            .is_some_and(|t| t.direction == MoveDirection::TurnRight)
}

fn is_strafe(step: &RouteDriveStep) -> bool {
    step.travel.is_some_and(|t| {
        matches!(
            t.direction,
            MoveDirection::StrafeLeft | MoveDirection::StrafeRight
        )
    })
}

#[test]
fn a_straight_route_is_run_to_its_end() {
    let mut body = SimulatedBody::default();
    let mut driver = driver(&[v(0.0, 0.0, 0.0), v(0.0, 20.0, 0.0)]);

    drive(&mut driver, &mut body, 10.0, &none(), None);

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    in_range(body.position.distance(Vec2::new(0.0, 20.0)), 0.0, 0.6);
    assert!(!body.travelling());
}

#[test]
fn a_drive_that_takes_over_a_move_under_way_goes_on_without_starting_it_again() {
    let mut body = SimulatedBody::default();
    drive(
        &mut driver(&[v(0.0, 0.0, 0.0), v(0.0, 30.0, 0.0)]),
        &mut body,
        1.0,
        &none(),
        None,
    );
    assert!(body.travelling());
    let legs = [body.position.extend(0.0), v(0.5, 30.0, 0.0)];

    let taken = RouteDriver::new(&legs, &[], true)
        .unwrap()
        .advance(&body.sample(), &none());
    let started = RouteDriver::new(&legs, &[], false)
        .unwrap()
        .advance(&body.sample(), &none());

    assert!(taken.travel.is_none());
    assert!(!taken.stop_travel);
    assert!(started.travel.is_some());
}

#[test]
fn a_leg_pointing_behind_is_faced_before_running() {
    let mut body = SimulatedBody {
        heading: 180.0,
        ..SimulatedBody::default()
    };
    let mut driver = driver(&[v(0.0, 0.0, 0.0), v(0.0, 10.0, 0.0)]);

    let first = driver.advance(&body.sample(), &none());

    assert!(first.travel.is_none());
    in_range(first.turn.unwrap().amount, 179.9995, 180.0005);
    body.apply(first);
    drive(&mut driver, &mut body, 10.0, &none(), None);
    assert_eq!(driver.state(), RouteDriveState::Arrived);
}

#[test]
fn a_corner_is_run_up_to_and_turned_in_place_without_walking() {
    let mut body = SimulatedBody::default();
    let mut driver = driver(&[v(0.0, 0.0, 0.0), v(0.0, 10.0, 0.0), v(10.0, 10.0, 0.0)]);

    let steps = drive(&mut driver, &mut body, 20.0, &none(), None);

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    assert!(!steps.iter().any(is_walk));
    assert!(steps.iter().any(stops_and_turns_right));
    in_range(body.position.distance(Vec2::new(10.0, 10.0)), 0.0, 0.6);
}

/// Beside a trap the body walks, and its turns are asked for at a walk too.
#[test]
fn beside_a_trap_the_body_walks_and_turns_at_a_walk() {
    let mut body = SimulatedBody::default();
    let mut driver = driver(&[v(0.0, 0.0, 0.0), v(0.0, 10.0, 0.0), v(10.0, 10.0, 0.0)]);
    let careful = |_: Vec2| true;
    let hooks = RouteDriveHooks {
        careful_at: Some(&careful),
        ..RouteDriveHooks::default()
    };

    let steps = drive(&mut driver, &mut body, 40.0, &hooks, None);

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    assert!(!steps.iter().any(is_run));
    assert!(steps.iter().any(is_walk));
    assert!(steps.iter().any(stops_and_turns_right));
    assert!(
        !steps
            .iter()
            .any(|step| step.turn.is_some_and(|t| t.pace == MovePace::Run))
    );
}

/// A body moves off from a turn in place only once the arc it then goes
/// round bows out from its leg by no more than a hand's breadth.
#[test]
fn a_body_moves_off_from_a_turn_only_when_its_arc_stays_in_its_lane() {
    let human = RouteTurning {
        run_speed: 11.0,
        run_turn_degrees_per_second: 129.0,
        walk_speed: 3.12,
        walk_turn_degrees_per_second: 86.0,
    };

    let run = move_off_degrees(Some(human), MovePace::Run);
    let walk = move_off_degrees(Some(human), MovePace::Walk);

    in_range(run, 10.0, 16.0);
    in_range(walk, 18.0, 26.0);
    assert!(run < walk);
    assert_eq!(move_off_degrees(None, MovePace::Run), TURN_IN_PLACE_DEGREES);
    // Speeds known for a run alone leave a walk at the old angle.
    let run_only = RouteTurning {
        run_speed: 11.0,
        run_turn_degrees_per_second: 129.0,
        ..RouteTurning::default()
    };
    assert_eq!(
        move_off_degrees(Some(run_only), MovePace::Walk),
        TURN_IN_PLACE_DEGREES
    );
}

#[test]
fn a_corner_with_room_is_run_around_along_an_arc_without_stopping() {
    let legs = [v(0.0, 0.0, 0.0), v(0.0, 10.0, 0.0), v(10.0, 10.0, 0.0)];
    let arcs: RefCell<Vec<Vec<Vec3>>> = RefCell::new(Vec::new());
    let cut = |arc: &[Vec3]| {
        arcs.borrow_mut().push(arc.to_vec());
        true
    };
    let hooks = RouteDriveHooks {
        can_cut_along: Some(&cut),
        ..RouteDriveHooks::default()
    };
    let mut body = SimulatedBody {
        turning: Some(TURNING),
        ..SimulatedBody::default()
    };
    let mut path = Vec::new();
    let mut driver = RouteDriver::new(&legs, &[], false).unwrap();

    let steps = drive(&mut driver, &mut body, 20.0, &hooks, Some(&mut path));
    let standing = drive(
        &mut RouteDriver::new(&legs, &[], false).unwrap(),
        &mut SimulatedBody::default(),
        20.0,
        &none(),
        None,
    );

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    assert!(!steps[..steps.len() - 1].iter().any(|step| step.stop_travel));
    assert!(!steps.iter().any(is_walk));
    let radius = 4.0 / (std::f32::consts::PI / 2.0);
    let arcs = arcs.borrow();
    assert_eq!(arcs.len(), 1);
    let arc = &arcs[0];
    assert_eq!(flat(arc[0]), Vec2::new(0.0, 10.0 - radius));
    in_range(
        flat(*arc.last().unwrap()).distance(Vec2::new(radius, 10.0)),
        0.0,
        0.01,
    );
    let nearest = path
        .iter()
        .map(|at| at.distance(Vec2::new(0.0, 10.0)))
        .fold(f32::INFINITY, f32::min);
    assert!(
        nearest > 0.8,
        "the body came within {nearest:.2} m of the corner"
    );
    in_range(body.position.distance(Vec2::new(10.0, 10.0)), 0.0, 0.6);
    assert!(
        steps.len() < standing.len(),
        "cutting the corner took {} frames, turning in place {}",
        steps.len(),
        standing.len()
    );
    assert_eq!(
        (driver.corners_run_around, driver.corners_turned_in_place),
        (1, 0)
    );
}

#[test]
fn a_corner_only_a_walks_tighter_arc_passes_is_turned_in_place_rather_than_walked() {
    let arcs = Cell::new(0);
    let cut = |arc: &[Vec3]| {
        arcs.set(arcs.get() + 1);
        flat(arc[0]).distance(Vec2::new(0.0, 10.0)) <= 1.5
    };
    let hooks = RouteDriveHooks {
        can_cut_along: Some(&cut),
        ..RouteDriveHooks::default()
    };
    let mut body = SimulatedBody {
        turning: Some(TURNING),
        ..SimulatedBody::default()
    };
    let mut driver = driver(&[v(0.0, 0.0, 0.0), v(0.0, 10.0, 0.0), v(10.0, 10.0, 0.0)]);

    let steps = drive(&mut driver, &mut body, 20.0, &hooks, None);

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    assert_eq!(arcs.get(), 1);
    assert_eq!(
        (driver.corners_run_around, driver.corners_turned_in_place),
        (0, 1)
    );
    assert!(!steps.iter().any(is_walk));
    assert!(steps.iter().any(stops_and_turns_right));
    in_range(body.position.distance(Vec2::new(10.0, 10.0)), 0.0, 0.6);
}

#[test]
fn a_corner_no_arc_passes_is_turned_in_place_without_walking_up_to_it() {
    let cut = |_: &[Vec3]| false;
    let hooks = RouteDriveHooks {
        can_cut_along: Some(&cut),
        ..RouteDriveHooks::default()
    };
    let mut body = SimulatedBody {
        turning: Some(TURNING),
        ..SimulatedBody::default()
    };
    let mut driver = driver(&[v(0.0, 0.0, 0.0), v(0.0, 10.0, 0.0), v(10.0, 10.0, 0.0)]);

    let steps = drive(&mut driver, &mut body, 20.0, &hooks, None);

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    assert_eq!(
        (driver.corners_run_around, driver.corners_turned_in_place),
        (0, 1)
    );
    assert!(!steps.iter().any(is_walk));
    assert!(steps.iter().any(stops_and_turns_right));
}

#[test]
fn a_hairpin_is_turned_in_place_without_asking_whether_an_arc_passes() {
    let asked = Cell::new(false);
    let cut = |_: &[Vec3]| {
        asked.set(true);
        true
    };
    let hooks = RouteDriveHooks {
        can_cut_along: Some(&cut),
        ..RouteDriveHooks::default()
    };
    let mut body = SimulatedBody {
        turning: Some(TURNING),
        ..SimulatedBody::default()
    };
    let mut driver = driver(&[v(0.0, 0.0, 0.0), v(0.0, 10.0, 0.0), v(1.0, 0.0, 0.0)]);

    let steps = drive(&mut driver, &mut body, 30.0, &hooks, None);

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    assert!(!asked.get());
    assert!(
        steps
            .iter()
            .any(|step| step.stop_travel && step.turn.is_some())
    );
}

#[test]
fn small_drift_is_corrected_without_stopping() {
    let body = SimulatedBody {
        heading: 10.0,
        ..SimulatedBody::default()
    };
    let mut driver = driver(&[v(0.0, 0.0, 0.0), v(0.0, 20.0, 0.0)]);

    let first = driver.advance(&body.sample(), &none());

    assert_eq!(
        first.travel,
        Some(MoveRequest::new(MoveDirection::Forward, MovePace::Run, 0.0))
    );
    let turn = first.turn.unwrap();
    assert_eq!(turn.direction, MoveDirection::TurnLeft);
    in_range(turn.amount, 9.9995, 10.0005);
    assert!(!first.stop_travel);
}

#[test]
fn a_leg_the_body_cannot_move_along_ends_the_drive_as_blocked() {
    let mut body = SimulatedBody {
        stuck: true,
        ..SimulatedBody::default()
    };
    let mut driver = driver(&[v(0.0, 0.0, 0.0), v(0.0, 20.0, 0.0)]);

    let steps = drive(&mut driver, &mut body, 5.0, &none(), None);

    assert_eq!(driver.state(), RouteDriveState::Blocked);
    assert!(steps.last().unwrap().stop_travel || !body.travelling());
}

#[test]
fn the_player_taking_over_ends_the_drive() {
    let mut body = SimulatedBody::default();
    let mut driver = driver(&[v(0.0, 0.0, 0.0), v(0.0, 20.0, 0.0)]);
    drive(&mut driver, &mut body, 1.0, &none(), None);

    body.interrupt();
    let step = driver.advance(&body.sample(), &none());

    assert_eq!(driver.state(), RouteDriveState::Interrupted);
    assert!(step.is_empty());
}

#[test]
fn portal_space_loses_the_drive() {
    let mut body = SimulatedBody::default();
    let mut driver = driver(&[v(0.0, 0.0, 0.0), v(0.0, 20.0, 0.0)]);
    drive(&mut driver, &mut body, 1.0, &none(), None);

    body.in_portal_space = true;
    let step = driver.advance(&body.sample(), &none());

    assert_eq!(driver.state(), RouteDriveState::Lost);
    assert!(step.stop_travel);
}

#[test]
fn a_long_leg_renews_its_run_before_the_clients_limit() {
    let mut body = SimulatedBody::default();
    let mut driver = driver(&[v(0.0, 0.0, 0.0), v(0.0, 200.0, 0.0)]);

    let steps = drive(&mut driver, &mut body, 60.0, &none(), None);

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    assert!(steps.iter().filter(|step| step.travel.is_some()).count() >= 3);
    assert!(body.longest_travel_seconds < 30.0);
}

#[test]
fn a_move_ended_before_the_drive_began_is_not_its_own() {
    let mut body = SimulatedBody::default();
    body.end_travel_before_drive(ScriptedMoveState::Blocked);
    let mut driver = driver(&[v(0.0, 0.0, 0.0), v(0.0, 20.0, 0.0)]);

    let first = driver.advance(&body.sample(), &none());

    assert_eq!(driver.state(), RouteDriveState::Driving);
    assert!(first.travel.is_some());
}

#[test]
fn cancel_stops_what_the_drive_began() {
    let mut body = SimulatedBody::default();
    let mut driver = driver(&[v(0.0, 0.0, 0.0), v(0.0, 20.0, 0.0)]);
    drive(&mut driver, &mut body, 1.0, &none(), None);

    let step = driver.cancel();

    assert!(step.stop_travel && step.stop_turn);
    assert_eq!(driver.state(), RouteDriveState::Interrupted);
    assert!(driver.advance(&body.sample(), &none()).is_empty());
}

#[test]
fn a_drive_that_takes_over_moves_stops_them_when_cancelled_before_its_first_step() {
    let mut body = SimulatedBody::default();
    drive(
        &mut driver(&[v(0.0, 0.0, 0.0), v(0.0, 30.0, 0.0)]),
        &mut body,
        1.0,
        &none(),
        None,
    );
    assert!(body.travelling());
    let legs = [body.position.extend(0.0), v(0.5, 30.0, 0.0)];

    let taking_over = RouteDriver::new(&legs, &[], true).unwrap().cancel();
    let fresh = RouteDriver::new(&legs, &[], false).unwrap().cancel();

    assert!(taking_over.stop_travel && taking_over.stop_turn);
    assert!(fresh.is_empty(), "a drive that owns nothing stops nothing");
}

fn deck(edge: f32, height: f32) -> impl Fn(Vec2) -> f32 {
    move |at: Vec2| if at.y < edge { height } else { 0.0 }
}

#[test]
fn a_leap_is_faced_charged_and_flown_from_its_takeoff_to_its_landing() {
    let mut body = SimulatedBody::on(deck(5.5, 3.0));
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 3.0),
            v(0.0, 5.0, 3.0),
            v(0.0, 6.7, 0.0),
            v(0.0, 10.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    let steps = drive(&mut driver, &mut body, 30.0, &none(), None);

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    assert_eq!(body.jumps, 1);
    let jump = single_jump(&steps);
    in_range(jump.jump.unwrap(), 0.0995, 0.1005);
    assert_eq!(jump.jump_pace, Some(MovePace::Walk));
    assert!(jump.travel.is_none());
    assert_eq!(body.travels_begun_while_charging, 0);
    in_range(body.position.distance(Vec2::new(0.0, 10.0)), 0.0, 0.6);
    in_range(body.height(), -0.0005, 0.0005);
}

#[test]
fn a_leap_is_run_up_to_and_charged_only_once_the_body_stands_still() {
    let mut body = SimulatedBody {
        slide_seconds: 0.15,
        ..SimulatedBody::on(deck(5.5, 3.0))
    };
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 3.0),
            v(0.0, 4.9, 3.0),
            v(0.0, 6.7, 0.0),
            v(0.0, 10.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    let steps = drive(&mut driver, &mut body, 30.0, &none(), None);

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    assert_eq!(body.jumps, 1);
    assert_eq!(body.jumps_charged_while_sliding, 0);
    let jumped = first_jump(&steps).unwrap();
    assert!(!steps[..jumped].iter().any(is_walk));
}

#[test]
fn a_leap_is_run_up_to_and_let_go_of_short_of_its_takeoff_so_the_body_comes_to_rest_on_it() {
    let mut body = SimulatedBody::on(deck(5.5, 3.0)).sliding(1.0, 0.75, 2.0);
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 3.0),
            v(0.0, 4.9, 3.0),
            v(0.0, 6.7, 0.0),
            v(0.0, 10.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    let steps = drive(&mut driver, &mut body, 30.0, &none(), None);

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    let off_takeoff = single(&body.charged_at).distance(Vec2::new(0.0, 4.9));
    assert!(
        off_takeoff <= 0.15,
        "the leap charged {off_takeoff:.2} m from its takeoff"
    );
    let jumped = first_jump(&steps).unwrap();
    assert!(!steps[..jumped].iter().any(is_walk));
}

#[test]
fn a_leap_too_near_to_run_up_to_is_walked_up_to_and_let_go_of_short_of_its_takeoff() {
    let mut body = SimulatedBody::on(deck(1.6, 3.0)).sliding(1.0, 0.75, 2.0);
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 3.0),
            v(0.0, 1.2, 3.0),
            v(0.0, 3.0, 0.0),
            v(0.0, 6.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    let steps = drive(&mut driver, &mut body, 30.0, &none(), None);

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    let off_takeoff = single(&body.charged_at).distance(Vec2::new(0.0, 1.2));
    assert!(
        off_takeoff <= 0.15,
        "the leap charged {off_takeoff:.2} m from its takeoff"
    );
    let jumped = first_jump(&steps).unwrap();
    assert!(!steps[..jumped].iter().any(is_run));
}

/// A takeoff farther than the shortest run-up but nearer than a run carries
/// the body on is walked up to too.
fn a_leap_nearer_than_a_run_carries_the_body_is_walked_up_to(takeoff: f32) {
    let mut body = SimulatedBody::on(deck(takeoff + 0.4, 3.0)).sliding(1.0, 0.75, 2.0);
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 3.0),
            v(0.0, takeoff, 3.0),
            v(0.0, takeoff + 1.8, 0.0),
            v(0.0, takeoff + 5.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    let steps = drive(&mut driver, &mut body, 30.0, &none(), None);

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    let off_takeoff = single(&body.charged_at).distance(Vec2::new(0.0, takeoff));
    assert!(
        off_takeoff <= 0.15,
        "the leap charged {off_takeoff:.2} m from its takeoff"
    );
    let jumped = first_jump(&steps).unwrap();
    assert!(!steps[..jumped].iter().any(is_run));
}

#[test]
fn a_leap_1_6_m_on_is_walked_up_to_without_a_stutter_step() {
    a_leap_nearer_than_a_run_carries_the_body_is_walked_up_to(1.6);
}

#[test]
fn a_leap_1_9_m_on_is_walked_up_to_without_a_stutter_step() {
    a_leap_nearer_than_a_run_carries_the_body_is_walked_up_to(1.9);
}

/// A body already past the line through its takeoff, but off to its side, is
/// not at the takeoff: it walks back to it before it charges.
#[test]
fn a_body_past_its_takeoff_but_off_to_its_side_walks_to_it_before_charging() {
    let mut body = SimulatedBody::on(deck(0.2, 3.0)).sliding(1.0, 0.75, 2.0);
    let mut driver = leaping_driver(
        &[
            v(-0.9, -3.0, 3.0),
            v(-0.9, -0.5, 3.0),
            v(-0.9, 1.5, 0.0),
            v(-0.9, 4.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    drive(&mut driver, &mut body, 30.0, &none(), None);

    let off_takeoff = single(&body.charged_at).distance(Vec2::new(-0.9, -0.5));
    assert!(
        off_takeoff <= TAKEOFF_RADIUS,
        "the leap charged {off_takeoff:.2} m from its takeoff"
    );
    assert_eq!(driver.state(), RouteDriveState::Arrived);
}

/// A leap is aimed again from exactly where the body stands as it charges,
/// and flown at the power and pace that aim gives.
#[test]
fn a_leap_is_flown_as_aimed_again_from_where_the_body_charges() {
    let mut body = SimulatedBody::on(deck(5.5, 3.0));
    let asked: RefCell<Vec<(Vec3, Vec3, bool)>> = RefCell::new(Vec::new());
    let aim = |standing: Vec3, landing: Vec3, run: bool, _: bool| {
        asked.borrow_mut().push((standing, landing, run));
        Some(RouteLeapAim {
            power: 0.3,
            run: true,
        })
    };
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 3.0),
            v(0.0, 5.0, 3.0),
            v(0.0, 6.7, 0.0),
            v(0.0, 10.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    let steps = drive(&mut driver, &mut body, 30.0, &hooks, None);

    let jump = single_jump(&steps);
    in_range(jump.jump.unwrap(), 0.2995, 0.3005);
    assert_eq!(jump.jump_pace, Some(MovePace::Run));
    let (standing, landing, planned) = *asked.borrow().last().unwrap();
    assert_eq!(flat(standing), single(&body.charged_at));
    assert_eq!(landing, v(0.0, 6.7, 0.0));
    assert!(!planned);
    assert_eq!(
        driver.approach().aimed,
        Some(RouteLeapAim {
            power: 0.3,
            run: true
        })
    );
    in_range(driver.approach().planned_power, 0.0995, 0.1005);
}

/// A body standing a little off its takeoff, where a leap aimed from there
/// is kept, jumps from there.
#[test]
fn a_body_standing_near_its_takeoff_where_a_leap_is_kept_jumps_without_walking() {
    let mut body = SimulatedBody::on(deck(0.2, 3.0)).sliding(1.0, 0.75, 2.0);
    let aim = |_: Vec3, _: Vec3, run: bool, _: bool| Some(RouteLeapAim { power: 0.12, run });
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(
        &[
            v(0.0, -3.0, 3.0),
            v(0.0, -0.6, 3.0),
            v(0.0, 1.5, 0.0),
            v(0.0, 4.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    let steps = drive(&mut driver, &mut body, 30.0, &hooks, None);

    assert_eq!(single(&body.charged_at), Vec2::ZERO);
    let jumped = first_jump(&steps).unwrap();
    assert!(!steps[..jumped].iter().any(|step| step.travel.is_some()));
    in_range(steps[jumped].jump.unwrap(), 0.1195, 0.1205);
}

/// Where no leap from where the body stands is kept, it walks to the
/// takeoff and flies the leap the route planned.
#[test]
fn a_body_standing_where_no_leap_is_kept_walks_to_its_takeoff_before_jumping() {
    let mut body = SimulatedBody::on(deck(0.2, 3.0)).sliding(1.0, 0.75, 2.0);
    // As a real aim is: kept from the takeoff and a little way off it.
    let aim = |standing: Vec3, _: Vec3, run: bool, _: bool| {
        (flat(standing).distance(Vec2::new(-0.9, -0.5)) <= TAKEOFF_RADIUS)
            .then_some(RouteLeapAim { power: 0.1, run })
    };
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(
        &[
            v(-0.9, -3.0, 3.0),
            v(-0.9, -0.5, 3.0),
            v(-0.9, 1.5, 0.0),
            v(-0.9, 4.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    let steps = drive(&mut driver, &mut body, 30.0, &hooks, None);

    let off_takeoff = single(&body.charged_at).distance(Vec2::new(-0.9, -0.5));
    assert!(
        off_takeoff <= TAKEOFF_RADIUS,
        "the leap charged {off_takeoff:.2} m from its takeoff"
    );
    in_range(single_jump(&steps).jump.unwrap(), 0.0995, 0.1005);
}

fn stutter_route() -> [Vec3; 5] {
    [
        v(0.0, 0.0, 3.0),
        v(0.0, 3.8, 3.0),
        v(0.0, 4.6, 3.0),
        v(0.0, 6.7, 0.0),
        v(0.0, 10.0, 0.0),
    ]
}

/// A body that came to rest off its takeoff, where no leap aimed from there
/// is kept, goes back to the takeoff before it jumps.
#[test]
fn a_body_that_stuttered_past_its_takeoff_where_no_leap_is_kept_goes_back_before_jumping() {
    // The walk slides on 1.4 m, well past what the driver is told it carries.
    let mut body = SimulatedBody::on(deck(5.5, 3.0)).sliding(1.9, 0.41, 0.8);
    let aim = |standing: Vec3, _: Vec3, run: bool, _: bool| {
        (flat(standing).distance(Vec2::new(0.0, 4.6)) <= TAKEOFF_RADIUS)
            .then_some(RouteLeapAim { power: 0.1, run })
    };
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(&stutter_route(), &[leap(3, 0.1, false)]);

    drive(&mut driver, &mut body, 60.0, &hooks, None);

    assert!(
        driver.approach().adjustments >= 1,
        "the body never went back to its takeoff"
    );
    for charged in &body.charged_at {
        let off_takeoff = charged.distance(Vec2::new(0.0, 4.6));
        assert!(
            off_takeoff <= TAKEOFF_RADIUS,
            "the leap charged {off_takeoff:.2} m from its takeoff after {} adjustments",
            driver.approach().adjustments
        );
    }
    assert!(
        !body.charged_at.is_empty() || driver.state() == RouteDriveState::Blocked,
        "the body neither jumped from its takeoff nor asked for the walk to plan again"
    );
}

/// Inside its takeoff radius but a step off its takeoff, a body where no
/// leap aimed from there is kept still goes back before it jumps.
#[test]
fn a_body_a_step_off_its_takeoff_where_no_leap_is_kept_goes_back_even_inside_the_takeoff_radius() {
    let aim = |_: Vec3, _: Vec3, _: bool, _: bool| None;
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(&stutter_route(), &[leap(3, 0.1, false)]);
    let near = v(0.25, 4.6, 3.0);

    // Arrived at the takeoff leg standing 0.25 m to its side, still.
    let mut steps = Vec::new();
    for _ in 0..5 {
        steps.push(driver.advance(
            &RouteDriveSample {
                position: near,
                still: true,
                ..RouteDriveSample::default()
            },
            &hooks,
        ));
    }

    assert!(!steps.iter().any(|step| step.jump.is_some()));
    assert_eq!(driver.approach().adjustments, 1);
    assert!(
        steps
            .iter()
            .any(|step| step.turn.is_some() || step.travel.is_some())
    );
}

/// A body that keeps no leap from where it stands and cannot get back to its
/// takeoff plans again rather than fly the planned leap from the wrong spot.
#[test]
fn a_body_that_cannot_get_back_to_its_takeoff_plans_again_rather_than_fly_from_where_it_stands() {
    let mut body = SimulatedBody::on(deck(5.5, 3.0)).sliding(1.9, 0.41, 0.8);
    let aim = |_: Vec3, _: Vec3, _: bool, _: bool| None;
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(&stutter_route(), &[leap(3, 0.1, false)]);

    drive(&mut driver, &mut body, 60.0, &hooks, None);

    assert_eq!(driver.state(), RouteDriveState::Blocked);
    assert_eq!(driver.approach().adjustments, MOST_TAKEOFF_ADJUSTMENTS);
    assert!(body.charged_at.is_empty());
}

/// A body standing still on the floor it would walk across to a leap's
/// takeoff tries the leap from where it stands first.
#[test]
fn a_leap_kept_from_where_the_body_stands_is_taken_without_walking_to_the_takeoff() {
    let mut body = SimulatedBody::on(deck(5.5, 3.0));
    let aim = |_: Vec3, _: Vec3, run: bool, _: bool| Some(RouteLeapAim { power: 0.4, run });
    let same = |_: Vec3, _: Vec3| true;
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        same_floor: Some(&same),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 3.0),
            v(1.0, 3.0, 3.0),
            v(0.0, 5.0, 3.0),
            v(0.0, 8.0, 0.0),
            v(0.0, 10.0, 0.0),
        ],
        &[leap(3, 0.1, false)],
    );

    let steps = drive(&mut driver, &mut body, 30.0, &hooks, None);

    let jumped = first_jump(&steps).expect("a jump");
    assert!(!steps[..jumped].iter().any(|step| step.travel.is_some()));
    assert_eq!(single(&body.charged_at), Vec2::ZERO);
    in_range(driver.approach().walk_skipped, 5.2, 5.5);
}

/// A body standing on other floor than the takeoff walks the route.
#[test]
fn a_leap_is_not_tried_from_floor_other_than_its_takeoffs() {
    let mut body = SimulatedBody::on(deck(5.5, 3.0));
    let aim = |_: Vec3, _: Vec3, run: bool, _: bool| Some(RouteLeapAim { power: 0.4, run });
    let same = |_: Vec3, _: Vec3| false;
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        same_floor: Some(&same),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 3.0),
            v(0.0, 5.0, 3.0),
            v(0.0, 8.0, 0.0),
            v(0.0, 10.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    let steps = drive(&mut driver, &mut body, 30.0, &hooks, None);

    let jumped = first_jump(&steps).expect("the leap was never taken");
    assert!(steps[..jumped].iter().any(|step| step.travel.is_some()));
}

/// Where a leap comes down on the next leap's takeoff, a body sent back to
/// that takeoff walks to it and takes the next leap; it never steps back
/// onto the leap it already flew.
#[test]
fn a_body_sent_back_to_a_takeoff_the_leap_before_came_down_on_never_flies_that_leap_again() {
    let mut body = SimulatedBody::default().sliding(1.0, 0.41, 0.8);
    let takeoffs = [Vec2::new(0.0, 3.0), Vec2::new(0.0, 4.7)];
    let aim = |standing: Vec3, _: Vec3, run: bool, _: bool| {
        takeoffs
            .iter()
            .any(|takeoff| flat(standing).distance(*takeoff) <= 0.05)
            .then_some(RouteLeapAim { power: 0.1, run })
    };
    let same = |_: Vec3, _: Vec3| true;
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        same_floor: Some(&same),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 0.0),
            v(0.0, 3.0, 0.0),
            v(0.0, 4.7, 0.0),
            v(0.0, 6.4, 0.0),
            v(0.0, 9.0, 0.0),
        ],
        &[leap(2, 0.1, false), leap(3, 0.1, false)],
    );
    let mut highest = 0;

    let mut time = 0.0f32;
    while time < 60.0 && driver.state() == RouteDriveState::Driving {
        let step = driver.advance(&body.sample(), &hooks);
        assert!(
            driver.leg_index() >= highest,
            "the drive went back from leg {highest} to leg {}",
            driver.leg_index()
        );
        highest = driver.leg_index();
        body.apply(step);
        body.integrate(FRAME);
        time += FRAME;
    }

    // It either takes both leaps or asks for the walk to plan again; what it
    // never does is step back onto the leap it already flew.
    assert!(
        matches!(
            driver.state(),
            RouteDriveState::Arrived | RouteDriveState::Blocked
        ),
        "the drive ended {:?}",
        driver.state()
    );
    let expected = if driver.state() == RouteDriveState::Arrived {
        2
    } else {
        0
    };
    assert_eq!(
        body.jumps,
        expected,
        "the drive ended {:?} after {} jumps",
        driver.state(),
        body.jumps
    );
}

/// A leap says how its takeoff was come up to, so narration can show a
/// stutter step.
#[test]
fn a_leap_says_how_its_takeoff_was_come_up_to() {
    let mut body = SimulatedBody::on(deck(5.5, 3.0)).sliding(1.0, 0.75, 2.0);
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 3.0),
            v(0.0, 4.9, 3.0),
            v(1.0, 6.7, 0.0),
            v(1.0, 10.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    let steps = drive(&mut driver, &mut body, 30.0, &none(), None);
    assert!(first_jump(&steps).is_some());

    let approach = driver.approach();
    assert_eq!(approach.leg, 1);
    assert_eq!(approach.pace, MovePace::Run);
    assert_eq!(approach.moves_begun, 1);
    in_range(approach.let_go_meters, 1.5, 2.6);
    assert!(
        approach.turns >= 1,
        "the leap bends from the run up, so it turns to face its landing"
    );
    in_range(approach.turned_degrees, 25.0, 35.0);
    in_range(approach.takeoff_error, 0.0, 0.15);
}

#[test]
fn a_leap_that_lands_away_from_its_landing_on_the_same_level_asks_for_a_new_plan() {
    let mut body = SimulatedBody::on(deck(5.5, 3.0));
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 3.0),
            v(0.0, 5.0, 3.0),
            v(0.0, 12.0, 0.0),
            v(0.0, 15.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    drive(&mut driver, &mut body, 30.0, &none(), None);

    assert_eq!(driver.state(), RouteDriveState::LandedElsewhere);
    assert_eq!(body.jumps, 1);
    assert!(driver.landing_error > LANDING_RADIUS);
}

#[test]
fn a_leap_that_lands_on_another_level_ends_the_drive_blocked() {
    let mut body = SimulatedBody::on(deck(5.5, 3.0));
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 3.0),
            v(0.0, 5.0, 3.0),
            v(0.0, 6.7, 3.0),
            v(0.0, 10.0, 3.0),
        ],
        &[leap(2, 0.1, false)],
    );

    drive(&mut driver, &mut body, 30.0, &none(), None);

    assert_eq!(driver.state(), RouteDriveState::Blocked);
    assert_eq!(body.jumps, 1);
}

#[test]
fn a_leap_that_never_leaves_the_ground_ends_the_drive_blocked() {
    let mut body = SimulatedBody {
        cannot_jump: true,
        ..SimulatedBody::on(deck(5.5, 3.0))
    };
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 3.0),
            v(0.0, 5.0, 3.0),
            v(0.0, 6.7, 0.0),
            v(0.0, 10.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    drive(&mut driver, &mut body, 30.0, &none(), None);

    assert_eq!(driver.state(), RouteDriveState::Blocked);
    assert!(!driver.is_leaping());
}

#[test]
fn a_leap_lets_go_of_forward_once_the_body_has_risen_off_the_ground_so_it_stops_where_it_lands() {
    let mut body = SimulatedBody {
        leave_ground_seconds: 0.1,
        ..SimulatedBody::on(deck(5.5, 3.0))
    };
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 3.0),
            v(0.0, 4.9, 3.0),
            v(0.0, 6.7, 0.0),
            v(0.0, 10.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    drive(&mut driver, &mut body, 30.0, &none(), None);

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    assert_eq!(body.jumps, 1);
    assert_eq!(body.landings_pressing_forward, 0);
    in_range(body.position.distance(Vec2::new(0.0, 10.0)), 0.0, 0.6);
}

#[test]
fn a_leap_goes_on_only_once_the_body_has_slid_to_a_stop_after_it_lands() {
    let mut body = SimulatedBody {
        landing_slide_seconds: 0.3,
        ..SimulatedBody::on(deck(5.5, 3.0))
    };
    let mut driver = leaping_driver(
        &[
            v(0.0, 0.0, 3.0),
            v(0.0, 4.9, 3.0),
            v(0.0, 6.9, 0.0),
            v(0.0, 10.0, 0.0),
        ],
        &[leap(2, 0.1, false)],
    );

    drive(&mut driver, &mut body, 30.0, &none(), None);

    assert_eq!(driver.state(), RouteDriveState::Arrived);
    assert_eq!(body.jumps, 1);
    assert_eq!(body.travels_begun_while_sliding, 0);
    in_range(driver.landing_slide, 0.1, 0.4);
}

// From RuntimeRouteDriverTakeoffTests.cs.

/// A sidestep onto a takeoff runs on a strafe channel of its own, so the
/// leg it steps onto counts as reached only once the step ends.
#[test]
fn a_sidestep_onto_a_takeoff_runs_to_its_end_before_anything_else_is_asked() {
    let takeoff = Vec2::new(0.0, 0.5);
    let mut body = SimulatedBody::default();
    let aim = |standing: Vec3, _: Vec3, run: bool, _: bool| {
        (flat(standing).distance(takeoff) <= 0.2).then_some(RouteLeapAim { power: 0.2, run })
    };
    let same = |_: Vec3, _: Vec3| true;
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        same_floor: Some(&same),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(
        &[v(0.0, 0.0, 0.0), v(0.0, 0.5, 0.0), v(0.0, 4.0, 0.0)],
        &[leap(2, 0.2, false)],
    );

    let steps = drive(&mut driver, &mut body, 20.0, &hooks, None);

    let stepped = steps
        .iter()
        .position(is_strafe)
        .expect("the takeoff a walk cannot reach was never stepped onto");
    let jumped = first_jump(&steps).expect("a jump");
    assert!(
        jumped > stepped,
        "the leap never followed the step onto its takeoff"
    );
    // Nothing travels over the step: only the turn that faces the landing
    // comes between it and the jump.
    assert!(
        !steps[stepped + 1..jumped]
            .iter()
            .any(|step| step.travel.is_some())
    );
    in_range(single(&body.charged_at).distance(takeoff), 0.0, 0.1);
}

/// A body that came to rest past its takeoff steps back onto it sideways
/// when the gap is one a forward move overshoots.
#[test]
fn a_walk_back_to_a_takeoff_steps_sideways_for_a_gap_a_walk_would_overshoot() {
    let takeoff = Vec2::new(0.0, 3.0);
    let mut body = SimulatedBody::default().sliding(1.0, 0.41, 0.8);
    let aim = |standing: Vec3, _: Vec3, run: bool, _: bool| {
        (flat(standing).distance(takeoff) <= 0.05).then_some(RouteLeapAim { power: 0.1, run })
    };
    let same = |_: Vec3, _: Vec3| true;
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        same_floor: Some(&same),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(
        &[v(0.0, 0.0, 0.0), v(0.0, 3.0, 0.0), v(0.0, 6.0, 0.0)],
        &[leap(2, 0.1, false)],
    );

    let steps = drive(&mut driver, &mut body, 60.0, &hooks, None);

    assert!(steps.iter().any(is_strafe));
    assert!(
        driver.approach().sidesteps > 0,
        "the walk back took no sidestep"
    );
}

/// A leap whose aim is refused from where the body stands is never flown
/// from off its takeoff.
#[test]
fn a_leap_whose_aim_is_refused_is_never_flown_from_off_its_takeoff() {
    let takeoff = Vec2::new(0.0, 3.0);
    let mut body = SimulatedBody::default().sliding(1.0, 0.41, 0.8);
    let aim = |_: Vec3, _: Vec3, _: bool, _: bool| None;
    let same = |_: Vec3, _: Vec3| true;
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        same_floor: Some(&same),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(
        &[v(0.0, 0.0, 0.0), v(0.0, 3.0, 0.0), v(0.0, 6.0, 0.0)],
        &[leap(2, 0.1, false)],
    );

    drive(&mut driver, &mut body, 60.0, &hooks, None);

    assert_ne!(driver.state(), RouteDriveState::Driving);
    for at in &body.charged_at {
        in_range(at.distance(takeoff), 0.0, NO_AIM_TAKEOFF_RADIUS);
    }
}

/// A leap counts as aimed from the takeoff the route planned it from only
/// inside the slack the planner checks its own leaps through.
#[test]
fn a_leap_is_aimed_as_from_its_takeoff_only_inside_the_planners_own_slack() {
    let takeoff = Vec2::new(0.0, 3.0);
    let asked: RefCell<Vec<(f32, bool)>> = RefCell::new(Vec::new());
    let mut body = SimulatedBody::default().sliding(1.0, 0.41, 0.8);
    let aim = |standing: Vec3, _: Vec3, run: bool, at_its_takeoff: bool| {
        asked
            .borrow_mut()
            .push((flat(standing).distance(takeoff), at_its_takeoff));
        Some(RouteLeapAim { power: 0.1, run })
    };
    let same = |_: Vec3, _: Vec3| true;
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        same_floor: Some(&same),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(
        &[v(0.0, 0.0, 0.0), v(0.0, 3.0, 0.0), v(0.0, 6.0, 0.0)],
        &[leap(2, 0.1, false)],
    );

    drive(&mut driver, &mut body, 60.0, &hooks, None);

    let asked = asked.borrow();
    assert!(!asked.is_empty());
    assert_eq!(AT_ITS_TAKEOFF_RADIUS, TAKEOFF_SLACK);
    for (off, at_its_takeoff) in asked.iter() {
        assert_eq!(*off <= TAKEOFF_SLACK, *at_its_takeoff, "{off}");
    }
}

/// A body standing near a takeoff but on another floor walks to the
/// takeoff rather than taking the leap from where it stands.
#[test]
fn a_leap_is_not_aimed_from_near_its_takeoff_on_another_floor() {
    let mut body = SimulatedBody::on(|at: Vec2| if at.y < 0.6 { 0.0 } else { 3.0 });
    let aim = |_: Vec3, _: Vec3, run: bool, _: bool| Some(RouteLeapAim { power: 0.2, run });
    let same = |standing: Vec3, spot: Vec3| (standing.z - spot.z).abs() < 0.5;
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        same_floor: Some(&same),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(
        &[v(0.0, 0.0, 0.0), v(0.0, 1.2, 3.0), v(0.0, 5.0, 3.0)],
        &[leap(2, 0.2, false)],
    );

    let steps = drive(&mut driver, &mut body, 20.0, &hooks, None);

    let jumped = first_jump(&steps).expect("the leap was never taken");
    assert!(steps[..jumped].iter().any(|step| step.travel.is_some()));
}

/// A jump that never leaves the ground came down nowhere, so there is no
/// landing to measure.
#[test]
fn a_jump_that_never_leaves_the_ground_came_down_nowhere() {
    let mut body = SimulatedBody {
        cannot_jump: true,
        ..SimulatedBody::default()
    };
    let aim = |_: Vec3, _: Vec3, run: bool, _: bool| Some(RouteLeapAim { power: 0.2, run });
    let same = |_: Vec3, _: Vec3| true;
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        same_floor: Some(&same),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(
        &[v(0.0, 0.0, 0.0), v(0.0, 3.0, 0.0), v(0.0, 6.0, 0.0)],
        &[leap(2, 0.2, false)],
    );

    drive(&mut driver, &mut body, 30.0, &hooks, None);

    assert_eq!(driver.state(), RouteDriveState::Blocked);
    assert!(driver.leap_flew, "the leap never charged");
    assert!(
        !driver.leap_landed(),
        "a jump still on the ground was said to have come down"
    );
}

/// A leap aimed at another spot of its landing's floor says where it was
/// flown from and what it was flown at.
#[test]
fn a_leap_aimed_onward_says_where_it_was_flown_from_and_what_at() {
    let onward = v(0.0, 5.5, 0.0);
    let mut body = SimulatedBody::on(deck(3.5, 2.0));
    let aim = |_: Vec3, _: Vec3, _: bool, _: bool| None;
    let aim_onward = |_: Vec3, _: Vec3, run: bool| Some((RouteLeapAim { power: 0.3, run }, onward));
    let same = |_: Vec3, _: Vec3| true;
    let hooks = RouteDriveHooks {
        aim_leap_from: Some(&aim),
        aim_onward: Some(&aim_onward),
        same_floor: Some(&same),
        ..RouteDriveHooks::default()
    };
    let mut driver = leaping_driver(
        &[v(0.0, 0.0, 2.0), v(0.0, 3.0, 2.0), v(0.0, 6.0, 0.0)],
        &[leap(2, 0.2, false)],
    );

    drive(&mut driver, &mut body, 30.0, &hooks, None);

    assert_eq!(driver.approach().flown_at, Some(onward));
    assert!(
        driver.approach().aimed_onward,
        "the leap was not said to have been aimed onward"
    );
    assert_eq!(
        single(&body.charged_at),
        flat(driver.approach().charged_from)
    );
    in_range(driver.approach().charged_from.z, 1.9995, 2.0005);
}
