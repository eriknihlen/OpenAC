//! The route driver: walks a body along the legs of a planned route with
//! scripted moves, one quantum at a time.
//!
//! Each quantum the caller samples the body ([`RouteDriveSample`]), calls
//! [`RouteDriver::advance`], and applies the returned [`RouteDriveStep`] to
//! its [`crate::motor::ScriptedMovement`]: moves to begin or stop, and a
//! jump to charge with the pace to leave the ground at.
//!
//! The driver turns in place onto a leg that points well away from its
//! heading, runs along the leg while steering back onto it with small exact
//! turns, and counts a leg's end as reached once the body is near it or
//! past it. Where it knows how fast the body runs and turns, it cuts a
//! corner without stopping, wherever the arc of a running turn passes. A
//! leap is taken as a standing long jump: run up to the takeoff, wait until
//! still, face the landing, charge, press forward as the charge releases,
//! let go once risen, and wait for the landing slide to stop.

use std::collections::HashMap;

use glam::{Vec2, Vec3};

use crate::leaps::TAKEOFF_SLACK;
use crate::motor::{
    MoveChannelSnapshot, MoveDirection, MovePace, MoveRequest, ScriptedMoveSnapshot,
    ScriptedMoveState, normalized_degrees, signed_degrees,
};

/// How a drive stands.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum RouteDriveState {
    /// Under way.
    #[default]
    Driving,
    /// The body reached the route's end.
    Arrived,
    /// The body stopped making progress along a leg; plan again from where
    /// it stands.
    Blocked,
    /// The player moved the character.
    Interrupted,
    /// The character entered portal space or left the world.
    Lost,
    /// A leap came down on its landing's level but away from where it was
    /// planned; plan on from where the body stands.
    LandedElsewhere,
}

/// How fast a body runs and turns at a run, metres and degrees a second,
/// which sets the arc it follows when it turns as it runs.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct RouteTurning {
    /// Running speed, metres per second.
    pub run_speed: f32,
    /// Turn rate at a run, degrees per second.
    pub run_turn_degrees_per_second: f32,
    /// Walking speed, metres per second.
    pub walk_speed: f32,
    /// Turn rate at a walk, degrees per second.
    pub walk_turn_degrees_per_second: f32,
}

/// The body as a route driver sees it on one quantum.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RouteDriveSample {
    /// Where the body is, world frame.
    pub position: Vec3,
    /// Its compass heading, degrees.
    pub heading_degrees: f32,
    /// The scripted moves under way.
    pub moves: ScriptedMoveSnapshot,
    /// Whether the character is in portal space.
    pub in_portal_space: bool,
    /// Whether the body is off the ground.
    pub airborne: bool,
    /// How fast it goes and turns, when known.
    pub turning: Option<RouteTurning>,
    /// Whether it stands still.
    pub still: bool,
    /// How far a walk carries it on as the walk ends, metres; zero when not
    /// known.
    pub walk_stop_meters: f32,
    /// How far a run carries it on as the run ends, metres; zero when not
    /// known.
    pub run_stop_meters: f32,
}

impl Default for RouteDriveSample {
    fn default() -> Self {
        Self {
            position: Vec3::ZERO,
            heading_degrees: 0.0,
            moves: ScriptedMoveSnapshot::default(),
            in_portal_space: false,
            airborne: false,
            turning: None,
            still: true,
            walk_stop_meters: 0.0,
            run_stop_meters: 0.0,
        }
    }
}

/// A leg of a route flown rather than walked: the leg that ends at
/// `legs[leg_index]` is a standing long jump from `legs[leg_index - 1]`,
/// charged to `power` and left at running or walking pace.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RouteLeap {
    /// The leg the leap ends.
    pub leg_index: i32,
    /// The charge, 0 to 1.
    pub power: f32,
    /// Whether it leaves at a run.
    pub run: bool,
}

/// A leap aimed again from where the body stands.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RouteLeapAim {
    /// The charge, 0 to 1.
    pub power: f32,
    /// Whether it leaves at a run.
    pub run: bool,
}

/// How a leap's takeoff was come up to, for narration.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LeapApproach {
    /// The leg walked or run to the takeoff.
    pub leg: i32,
    /// The pace.
    pub pace: MovePace,
    /// How many moves were begun on that leg.
    pub moves_begun: i32,
    /// How far short of the takeoff the last move was let go (NaN when the
    /// body was already still).
    pub let_go_meters: f32,
    /// How many turns faced the landing.
    pub turns: i32,
    /// Through how many degrees.
    pub turned_degrees: f32,
    /// How far from the takeoff the jump charged.
    pub takeoff_error: f32,
    /// The takeoff, measured flat.
    pub takeoff_at: Vec2,
    /// Where the body was as the last move on the way was begun.
    pub begun_at: Vec2,
    /// Where the body was as the last move was let go.
    pub let_go_at: Vec2,
    /// Where the body stood as it began turning to face the landing.
    pub turned_at: Vec2,
    /// Where the body stood as the jump charged.
    pub charged_at: Vec2,
    /// Where the body stood as the jump charged, with its height.
    pub charged_from: Vec3,
    /// The spot the leap was flown at, where that is not the planned
    /// landing.
    pub flown_at: Option<Vec3>,
    /// The power the route planned the leap at.
    pub planned_power: f32,
    /// Whether the route planned it at a run.
    pub planned_run: bool,
    /// The leap as aimed again from where the body charged.
    pub aimed: Option<RouteLeapAim>,
    /// How far the body faced off the line to its landing as it charged,
    /// degrees, clockwise positive.
    pub facing_error: f32,
    /// How far the body would have walked to the planned takeoff had it not
    /// leapt from where it stood.
    pub walk_skipped: f32,
    /// How many times the body went back to the takeoff.
    pub adjustments: i32,
    /// How many sidesteps the body took onto its takeoff.
    pub sidesteps: i32,
    /// Whether the leap was aimed at another spot on the landing's floor.
    pub aimed_onward: bool,
}

impl LeapApproach {
    fn new(leg: i32, pace: MovePace) -> Self {
        Self {
            leg,
            pace,
            moves_begun: 0,
            let_go_meters: f32::NAN,
            turns: 0,
            turned_degrees: 0.0,
            takeoff_error: f32::NAN,
            takeoff_at: Vec2::ZERO,
            begun_at: Vec2::ZERO,
            let_go_at: Vec2::ZERO,
            turned_at: Vec2::ZERO,
            charged_at: Vec2::ZERO,
            charged_from: Vec3::ZERO,
            flown_at: None,
            planned_power: 0.0,
            planned_run: false,
            aimed: None,
            facing_error: 0.0,
            walk_skipped: 0.0,
            adjustments: 0,
            sidesteps: 0,
            aimed_onward: false,
        }
    }
}

impl Default for LeapApproach {
    fn default() -> Self {
        Self {
            let_go_meters: 0.0,
            takeoff_error: 0.0,
            ..Self::new(0, MovePace::Walk)
        }
    }
}

/// The scripted moves a route driver wants begun or stopped on one quantum,
/// the power of a jump to charge, and the pace to leave the ground at as it
/// releases.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct RouteDriveStep {
    /// A move to begin on the travel or strafe channel.
    pub travel: Option<MoveRequest>,
    /// A turn to begin.
    pub turn: Option<MoveRequest>,
    /// Stop the travel channel.
    pub stop_travel: bool,
    /// Stop the turn channel.
    pub stop_turn: bool,
    /// A jump to charge, at this power.
    pub jump: Option<f32>,
    /// The pace to leave the ground at as the jump releases.
    pub jump_pace: Option<MovePace>,
}

impl RouteDriveStep {
    /// Whether the step asks for nothing.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.travel.is_none()
            && self.turn.is_none()
            && !self.stop_travel
            && !self.stop_turn
            && self.jump.is_none()
    }
}

/// Whether a body can pass along a path of points.
pub type CanCutAlong<'a> = dyn Fn(&[Vec3]) -> bool + 'a;
/// Whether the body walks rather than runs at a point.
pub type CarefulAt<'a> = dyn Fn(Vec2) -> bool + 'a;
/// A leap aimed again from where the body stands: given where it stands, the
/// landing, whether the route runs it and whether the body is at its planned
/// takeoff.
pub type AimLeapFrom<'a> = dyn Fn(Vec3, Vec3, bool, bool) -> Option<RouteLeapAim> + 'a;
/// A leap onto the landing's floor from where the body stands, with the spot
/// it comes to rest on.
pub type AimOnward<'a> = dyn Fn(Vec3, Vec3, bool) -> Option<(RouteLeapAim, Vec3)> + 'a;
/// Whether two points stand on the same piece of floor.
pub type SameFloor<'a> = dyn Fn(Vec3, Vec3) -> bool + 'a;

/// What the driver may ask of the world each quantum. Every hook is
/// optional; a drive given none walks its legs and flies its leaps exactly
/// as planned. The caller passes the same hooks every quantum of a drive.
#[derive(Clone, Copy, Default)]
pub struct RouteDriveHooks<'a> {
    /// Whether a body can pass along a path of points; with it the drive
    /// cuts the corners whose running arcs it lets through.
    pub can_cut_along: Option<&'a CanCutAlong<'a>>,
    /// Where the body walks rather than runs, as beside a trap, so it holds
    /// its line.
    pub careful_at: Option<&'a CarefulAt<'a>>,
    /// Aims a leap again from where the body stands; `None` when no leap
    /// from there is kept.
    pub aim_leap_from: Option<&'a AimLeapFrom<'a>>,
    /// A leap onto the floor a planned landing lies on, taken from where the
    /// body stands.
    pub aim_onward: Option<&'a AimOnward<'a>>,
    /// Whether two points stand on the same piece of floor; without it only
    /// their heights say.
    pub same_floor: Option<&'a SameFloor<'a>>,
}

impl std::fmt::Debug for RouteDriveHooks<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RouteDriveHooks")
            .field("can_cut_along", &self.can_cut_along.is_some())
            .field("careful_at", &self.careful_at.is_some())
            .field("aim_leap_from", &self.aim_leap_from.is_some())
            .field("aim_onward", &self.aim_onward.is_some())
            .field("same_floor", &self.same_floor.is_some())
            .finish()
    }
}

/// Why a drive cannot be built.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RouteDriveError {
    /// A route needs a start and at least one leg end.
    TooFewLegs,
    /// A leap must end at one of the route's leg ends.
    LeapOffRoute,
}

/// A leg's end counts as reached within this, metres.
pub const ARRIVAL_RADIUS: f32 = 0.5;
/// The shortest a forward move carries this client's body, however little
/// is asked of it: measured live, asking for 0.10 m and for 0.25 m both
/// moved 0.76 m. A spot nearer than this is stepped onto sideways.
pub const SHORTEST_WALK_METERS: f32 = 0.76;
/// Asking a sidestep for less than this moves nothing at all, metres.
pub const SHORTEST_SIDESTEP_METERS: f32 = 0.15;
/// How far a sidestep carries the body past what it was asked for, metres.
pub const SIDESTEP_CARRY_METERS: f32 = 0.05;
/// A heading error within this needs no steering, degrees.
pub const STEER_TOLERANCE_DEGREES: f32 = 3.0;
/// A heading error beyond this is turned in place, degrees.
pub const TURN_IN_PLACE_DEGREES: f32 = 30.0;
/// How far aside of its leg a body may drift by moving off while it still
/// has some of a turn in place to make, metres.
pub const MOVE_OFF_DRIFT_METERS: f32 = 0.15;
/// A corner turning more than this is never cut, degrees.
pub const SHARPEST_CUT_DEGREES: f32 = 120.0;
/// A cut starts this many seconds of travel before its arc does: half a
/// frame at 60 frames a second.
pub const CUT_EARLY_SECONDS: f32 = 1.0 / 120.0;
/// While cutting a corner, the body stops to turn in place only when it
/// faces farther than this from where it is headed, degrees.
pub const CUT_TURN_IN_PLACE_DEGREES: f32 = 90.0;
/// The body steers for a point this far along its leg past the point on the
/// leg nearest it, metres.
pub const LOOK_AHEAD_METERS: f32 = 2.5;
/// And this much farther for each metre it is off the leg.
pub const LOOK_AHEAD_PER_METER_ASIDE: f32 = 2.75;
/// The points of an arc a cut is checked along lie about this far apart.
const ARC_STEP_METERS: f32 = 0.25;
/// A run without an amount is renewed this often, well inside the motor's
/// thirty second limit, seconds.
pub const RENEW_TRAVEL_SECONDS: f32 = 20.0;
/// A leap starts once the body stands this near its takeoff, metres.
pub const TAKEOFF_RADIUS: f32 = 0.35;
/// Facing its landing to within this, degrees.
pub const LEAP_FACING_DEGREES: f32 = 2.0;
/// A leap that comes to rest farther than this from its landing landed
/// elsewhere, metres.
pub const LANDING_RADIUS: f32 = 1.5;
/// A leap that comes to rest farther above or below its landing than this
/// is on another level, metres.
pub const LANDING_HEIGHT: f32 = 1.0;
/// A jump still on the ground this long after it releases ends the drive
/// blocked, seconds.
pub const TAKEOFF_GRACE_SECONDS: f32 = 1.0;
/// A leap lets go of forward once the body has risen this far above where
/// it charged, metres.
pub const LET_GO_RISE_METERS: f32 = 0.05;
/// How much room past how far a run carries the body a run-up needs,
/// metres; a nearer takeoff is walked up to.
pub const SHORTEST_RUN_UP_METERS: f32 = 1.5;
/// How far above or below its takeoff a body with no floor to compare may
/// stand and still leap from where it stands, metres.
pub const LEAP_FROM_HERE_HEIGHT: f32 = 0.5;
/// How many times a body that came to rest off its takeoff goes back to it.
pub const MOST_TAKEOFF_ADJUSTMENTS: i32 = 3;
/// How near its takeoff a body with no aim kept stands before it flies the
/// planned leap, metres.
pub const NO_AIM_TAKEOFF_RADIUS: f32 = 0.1;
/// How near the planned takeoff a body stands to be at it, metres: the
/// slack the planner checks its own leaps through.
pub const AT_ITS_TAKEOFF_RADIUS: f32 = TAKEOFF_SLACK;
/// How far a body may drift standing still and still be where it last
/// stood, metres.
const STILL_METERS: f32 = 0.05;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
enum LeapPhase {
    #[default]
    None,
    Facing,
    Charging,
    Flying,
}

/// How a corner is cut: at what pace, how far before the corner the turn
/// starts, the point on the next leg where the cut ends, and the way that
/// leg runs. A cut with no pace is a corner taken standing.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
struct Cut {
    pace: Option<MovePace>,
    lead: f32,
    exit: Vec2,
    onward: Vec2,
}

/// Walks a body along the legs of a planned route. See the module
/// documentation.
#[derive(Debug, Clone)]
pub struct RouteDriver {
    legs: Vec<Vec3>,
    leaps: HashMap<i32, RouteLeap>,
    started: bool,
    travel_baseline: i64,
    turn_baseline: i64,
    strafe_baseline: i64,
    phase: LeapPhase,
    takeoff_height: f32,
    /// The highest the body has been since the leap under way charged.
    highest: f32,
    touched_down: bool,
    touchdown: Vec2,
    take_over_moves: bool,
    cut_planned_for: i32,
    cut: Cut,
    cutting: Cut,
    settling: Option<MovePace>,
    last_position: Option<Vec2>,
    /// Where the body last stood still and was aimed from at the leap
    /// ahead, so it is asked once a stop.
    aimed_from_here: Option<Vec2>,
    /// The spot a leap aimed onward flies at.
    fly_at: Option<Vec3>,
    /// The takeoff a body with no leap kept walks back to.
    walking_back: Option<Vec2>,
    aim_asked: bool,
    aim: Option<RouteLeapAim>,
    /// The spot a sidestep under way steps onto.
    sidestep_to: Option<Vec2>,
    /// What the strafe channel stood at as a sidestep was asked for.
    sidestep_asked: i64,
    state: RouteDriveState,
    leg_index: i32,
    approach: LeapApproach,
    /// How far from its planned landing, measured flat, the last leap came
    /// to rest.
    pub landing_error: f32,
    /// How far the body slid on after the last leap came down.
    pub landing_slide: f32,
    /// How far past the landing the last leap came to rest, along the line
    /// from where it charged; below zero for short of it.
    pub landing_long: f32,
    /// How far to the landing's side it came to rest.
    pub landing_aside: f32,
    /// How far the last leap flew from where it charged to where it came
    /// down.
    pub landing_flown: f32,
    /// How far its landing stood from where it charged.
    pub landing_planned: f32,
    /// How far the body slid off the line to its landing, degrees.
    pub landing_slide_degrees: f32,
    /// How far the last leap flew off the line to its landing, degrees.
    pub landing_flight_degrees: f32,
    /// How far above its planned landing the last leap came to rest.
    pub landing_rise: f32,
    /// Whether the last leap faced was charged and flown.
    pub leap_flew: bool,
    /// Where the body was as the last leap came down.
    pub landing_touchdown: Vec3,
    /// How far above where it charged the last leap rose at its highest.
    pub landing_peak: f32,
    /// How many corners the drive planned to run around.
    pub corners_run_around: i32,
    /// How many corners it turned in place at.
    pub corners_turned_in_place: i32,
}

impl RouteDriver {
    /// A drive along `legs`. With `take_over_moves`, moves already under way
    /// when it starts, such as those of a drive it replaces, count as its
    /// own, so a new plan goes on without stopping the character.
    ///
    /// # Errors
    ///
    /// When there are fewer than two legs, or a leap ends off the route.
    pub fn new(
        legs: &[Vec3],
        leaps: &[RouteLeap],
        take_over_moves: bool,
    ) -> Result<Self, RouteDriveError> {
        if legs.len() < 2 {
            return Err(RouteDriveError::TooFewLegs);
        }
        let mut by_leg = HashMap::new();
        for leap in leaps {
            if leap.leg_index < 1 || leap.leg_index as usize >= legs.len() {
                return Err(RouteDriveError::LeapOffRoute);
            }
            by_leg.insert(leap.leg_index, *leap);
        }
        Ok(Self {
            legs: legs.to_vec(),
            leaps: by_leg,
            started: false,
            travel_baseline: 0,
            turn_baseline: 0,
            strafe_baseline: 0,
            phase: LeapPhase::None,
            takeoff_height: 0.0,
            highest: 0.0,
            touched_down: false,
            touchdown: Vec2::ZERO,
            take_over_moves,
            cut_planned_for: 0,
            cut: Cut::default(),
            cutting: Cut::default(),
            settling: None,
            last_position: None,
            aimed_from_here: None,
            fly_at: None,
            walking_back: None,
            aim_asked: false,
            aim: None,
            sidestep_to: None,
            sidestep_asked: -1,
            state: RouteDriveState::Driving,
            leg_index: 1,
            approach: LeapApproach::default(),
            landing_error: 0.0,
            landing_slide: 0.0,
            landing_long: 0.0,
            landing_aside: 0.0,
            landing_flown: 0.0,
            landing_planned: 0.0,
            landing_slide_degrees: 0.0,
            landing_flight_degrees: 0.0,
            landing_rise: 0.0,
            leap_flew: false,
            landing_touchdown: Vec3::ZERO,
            landing_peak: 0.0,
            corners_run_around: 0,
            corners_turned_in_place: 0,
        })
    }

    /// How the drive stands.
    #[must_use]
    pub fn state(&self) -> RouteDriveState {
        self.state
    }

    /// The index in [`Self::legs`] of the leg end the body is heading for.
    #[must_use]
    pub fn leg_index(&self) -> i32 {
        self.leg_index
    }

    /// The route's leg ends.
    #[must_use]
    pub fn legs(&self) -> &[Vec3] {
        &self.legs
    }

    /// Whether the body is facing, charging or flying a leap.
    #[must_use]
    pub fn is_leaping(&self) -> bool {
        self.phase != LeapPhase::None
    }

    /// How the leap under way was come up to.
    #[must_use]
    pub fn approach(&self) -> LeapApproach {
        self.approach
    }

    /// Whether the leap last charged came down.
    #[must_use]
    pub fn leap_landed(&self) -> bool {
        self.touched_down
    }

    fn leg(&self, index: i32) -> Vec3 {
        self.legs[index as usize]
    }

    fn leg_count(&self) -> i32 {
        self.legs.len() as i32
    }

    fn measure_landing(&mut self, position: Vec2, height: f32, landing: Vec3) {
        let charged = self.approach.charged_at;
        let toward = flat(landing) - charged;
        let planned = toward.length();
        let along = if planned > 1e-3 {
            toward / planned
        } else {
            Vec2::Y
        };
        let off = position - flat(landing);
        self.landing_long = off.dot(along);
        self.landing_aside = off.x * along.y - off.y * along.x;
        self.landing_flown = self.touchdown.distance(charged);
        let slid = position - self.touchdown;
        self.landing_slide_degrees = if slid.length_squared() > 1e-4 && planned > 1e-3 {
            signed_degrees(compass_heading(slid) - compass_heading(toward))
        } else {
            0.0
        };
        let flight = self.touchdown - charged;
        self.landing_flight_degrees = if flight.length_squared() > 1e-6 && planned > 1e-3 {
            signed_degrees(compass_heading(flight) - compass_heading(toward))
        } else {
            0.0
        };
        self.landing_planned = planned;
        self.landing_rise = height - landing.z;
        self.landing_peak = self.highest - self.takeoff_height;
    }

    /// Advances by one quantum.
    pub fn advance(
        &mut self,
        sample: &RouteDriveSample,
        hooks: &RouteDriveHooks<'_>,
    ) -> RouteDriveStep {
        if self.state != RouteDriveState::Driving {
            return RouteDriveStep::default();
        }
        if !self.started {
            self.started = true;
            self.travel_baseline = self.baseline(&sample.moves.travel);
            self.turn_baseline = self.baseline(&sample.moves.turn);
            self.strafe_baseline = self.baseline(&sample.moves.strafe);
        }

        let travel = sample.moves.travel;
        let turn = sample.moves.turn;
        let strafe = sample.moves.strafe;
        let travel_is_ours = travel.sequence > self.travel_baseline;
        let turn_is_ours = turn.sequence > self.turn_baseline;
        let travelling = travel_is_ours && travel.state == ScriptedMoveState::Moving;
        let turning = turn_is_ours && turn.state == ScriptedMoveState::Moving;
        let forward = travelling && travel.request.direction == MoveDirection::Forward;
        let running = forward && travel.request.pace == MovePace::Run;
        let stop_meters = if !forward {
            0.0
        } else if running {
            sample.run_stop_meters
        } else {
            sample.walk_stop_meters
        };

        if sample.in_portal_space {
            return self.finish(RouteDriveState::Lost, travelling, turning);
        }
        if (travel_is_ours && travel.state == ScriptedMoveState::Interrupted)
            || (turn_is_ours && turn.state == ScriptedMoveState::Interrupted)
            || (strafe.sequence > self.strafe_baseline
                && strafe.state == ScriptedMoveState::Interrupted)
        {
            self.phase = LeapPhase::None;
            self.state = RouteDriveState::Interrupted;
            return RouteDriveStep::default();
        }
        if travel_is_ours && travel.state == ScriptedMoveState::Blocked {
            return self.finish(RouteDriveState::Blocked, travelling, turning);
        }

        let position = sample.position.truncate();
        let stepped = self
            .last_position
            .map_or(0.0, |before| position.distance(before));
        self.last_position = Some(position);
        if self.phase != LeapPhase::None {
            return self.advance_leap(sample, hooks, position, travel, travelling, turning);
        }

        if self.cutting.pace.is_some()
            && (position.distance(self.cutting.exit) <= ARRIVAL_RADIUS
                || (position - self.cutting.exit).dot(self.cutting.onward) >= 0.0)
        {
            self.settling = self.cutting.pace;
            self.cutting = Cut::default();
        }
        // A sidestep runs to its end before the spot it steps onto counts as
        // reached: a turn or a walk begun across one spoils it. It goes out on
        // a strafe channel of its own, so that is where it is watched.
        if let Some(stepping) = self.sidestep_to
            && let Some(onward) =
                self.still_stepping(sample, position, stepping, travelling, turning)
        {
            return onward;
        }
        if let Some(back) = self.walking_back {
            let reach = NO_AIM_TAKEOFF_RADIUS.max(if stop_meters > 0.0 {
                stop_meters + stepped / 2.0
            } else {
                0.0
            });
            if position.distance(back) > reach {
                // A gap a forward move overshoots is stepped across sideways,
                // as the step onto a takeoff is.
                if let Some(step_back) = self.sidestep(position, back, sample, travelling, turning)
                {
                    if step_back.travel.is_some() {
                        self.approach.sidesteps += 1;
                        self.approach.begun_at = position;
                    }
                    return step_back;
                }
                let off_back =
                    signed_degrees(compass_heading(back - position) - sample.heading_degrees);
                if off_back.abs() > TURN_IN_PLACE_DEGREES {
                    return RouteDriveStep {
                        turn: if turning {
                            None
                        } else {
                            Some(turn_by(off_back, MovePace::Walk))
                        },
                        stop_travel: travelling,
                        ..RouteDriveStep::default()
                    };
                }
                let walk_again = !travelling
                    || travel.request.direction != MoveDirection::Forward
                    || travel.request.pace != MovePace::Walk;
                return RouteDriveStep {
                    travel: walk_again
                        .then(|| MoveRequest::new(MoveDirection::Forward, MovePace::Walk, 0.0)),
                    turn: (!turning && off_back.abs() > STEER_TOLERANCE_DEGREES)
                        .then(|| turn_by(off_back, MovePace::Walk)),
                    ..RouteDriveStep::default()
                };
            }
            self.walking_back = None;
        }
        self.leap_from_here(sample, hooks, position, travelling);
        if self.cutting.pace.is_none() {
            while self.leg_index < self.leg_count() && !self.leaps.contains_key(&self.leg_index) {
                let reach = if stop_meters > 0.0 {
                    stop_meters + stepped / 2.0
                } else {
                    0.0
                };
                if self.reached(position, self.leg_index, reach)
                    || self.aims_from_here(sample, hooks, self.leg_index, travelling)
                {
                    self.leg_index += 1;
                    continue;
                }
                let cut = self.cut_for(self.leg_index, sample.turning, hooks);
                if cut.pace.is_some() && self.along_to(position, self.leg_index) <= cut.lead {
                    self.cutting = cut;
                    self.settling = None;
                    self.leg_index += 1;
                }
                break;
            }
        }
        if self.leg_index >= self.leg_count() {
            return self.finish(RouteDriveState::Arrived, travelling, turning);
        }
        // A body still sliding near a takeoff may come to rest where a leap
        // aimed from there is kept, which a walk begun now would spoil.
        if hooks.aim_leap_from.is_some()
            && self.leaps.contains_key(&(self.leg_index + 1))
            && !travelling
            && !sample.still
            && position.distance(flat(self.leg(self.leg_index))) <= SHORTEST_RUN_UP_METERS
        {
            return RouteDriveStep::default();
        }
        if self.leaps.contains_key(&self.leg_index) {
            self.phase = LeapPhase::Facing;
            self.aim_asked = false;
            self.leap_flew = false;
            let takeoff_at = flat(self.leg(self.leg_index - 1));
            if self.approach.leg != self.leg_index - 1 {
                self.approach = LeapApproach::new(self.leg_index - 1, MovePace::Walk);
            }
            self.approach.let_go_meters = if travelling {
                position.distance(takeoff_at)
            } else {
                f32::NAN
            };
            self.approach.takeoff_at = takeoff_at;
            self.approach.let_go_at = if travelling { position } else { Vec2::ZERO };
            return self.advance_leap(sample, hooks, position, travel, travelling, turning);
        }

        if self.leaps.contains_key(&(self.leg_index + 1))
            && let Some(across) = self.sidestep(
                position,
                flat(self.leg(self.leg_index)),
                sample,
                travelling,
                turning,
            )
        {
            if self.approach.leg != self.leg_index {
                self.approach = LeapApproach::new(self.leg_index, MovePace::Walk);
            }
            if across.travel.is_some() {
                self.approach.sidesteps += 1;
                self.approach.begun_at = position;
            }
            return across;
        }

        let aim = if self.cutting.pace.is_none() {
            self.aim_along(position, self.leg_index)
        } else {
            self.cutting.exit
        };
        let error = signed_degrees(compass_heading(aim - position) - sample.heading_degrees);
        if self.settling.is_some() && error.abs() <= TURN_IN_PLACE_DEGREES {
            self.settling = None;
        }
        let lenient = self.cutting.pace.is_some() || self.settling.is_some();
        // A turn is made at the pace the body travels at: a turn asked for at
        // a run while the body walks flips its hold to run as the walk ends.
        let pace = self.pace_for(
            hooks,
            position,
            sample.walk_stop_meters,
            sample.run_stop_meters,
            running,
        );
        let limit = if lenient {
            CUT_TURN_IN_PLACE_DEGREES
        } else {
            move_off_degrees(sample.turning, pace)
        };
        if error.abs() > limit {
            return RouteDriveStep {
                turn: if turning {
                    None
                } else {
                    Some(turn_by(error, pace))
                },
                stop_travel: travelling,
                ..RouteDriveStep::default()
            };
        }
        let renew = !travelling
            || travel.request.direction != MoveDirection::Forward
            || travel.request.pace != pace
            || travel.elapsed_seconds >= RENEW_TRAVEL_SECONDS;
        if self.leaps.contains_key(&(self.leg_index + 1)) {
            if self.approach.leg != self.leg_index {
                self.approach = LeapApproach::new(self.leg_index, pace);
            }
            if renew {
                self.approach.pace = pace;
                self.approach.moves_begun += 1;
                self.approach.begun_at = position;
            }
        }
        RouteDriveStep {
            travel: renew.then(|| MoveRequest::new(MoveDirection::Forward, pace, 0.0)),
            turn: (!turning && error.abs() > STEER_TOLERANCE_DEGREES).then(|| turn_by(error, pace)),
            ..RouteDriveStep::default()
        }
    }

    fn baseline(&self, channel: &MoveChannelSnapshot) -> i64 {
        if self.take_over_moves && channel.state == ScriptedMoveState::Moving {
            channel.sequence - 1
        } else {
            channel.sequence
        }
    }

    /// Ends the drive at once, stopping whatever it had begun. A drive
    /// that takes over the moves under way owns them from the start: ended
    /// before its first step, it still stops them (the drive it replaced
    /// let them go to it).
    pub fn cancel(&mut self) -> RouteDriveStep {
        if self.state != RouteDriveState::Driving {
            return RouteDriveStep::default();
        }
        self.phase = LeapPhase::None;
        self.sidestep_to = None;
        self.state = RouteDriveState::Interrupted;
        let owns_moves = self.started || self.take_over_moves;
        RouteDriveStep {
            stop_travel: owns_moves,
            stop_turn: owns_moves,
            ..RouteDriveStep::default()
        }
    }

    /// Takes the leap whose leg the body is on.
    fn advance_leap(
        &mut self,
        sample: &RouteDriveSample,
        hooks: &RouteDriveHooks<'_>,
        position: Vec2,
        travel: MoveChannelSnapshot,
        travelling: bool,
        turning: bool,
    ) -> RouteDriveStep {
        let leap = self.leaps[&self.leg_index];
        let mut landing = self.fly_at.unwrap_or(self.leg(self.leg_index));
        match self.phase {
            LeapPhase::Facing => {
                if travelling {
                    return RouteDriveStep {
                        stop_travel: true,
                        ..RouteDriveStep::default()
                    };
                }
                if turning || !sample.still {
                    return RouteDriveStep::default();
                }
                if !self.aim_asked {
                    self.aim_asked = true;
                    let takeoff = flat(self.leg(self.leg_index - 1));
                    self.aim = hooks.aim_leap_from.and_then(|aim| {
                        aim(
                            sample.position,
                            landing,
                            leap.run,
                            position.distance(takeoff) <= AT_ITS_TAKEOFF_RADIUS,
                        )
                    });

                    // Nothing says the landing the route planned is the only
                    // one this spot can reach: where no leap at it is kept, a
                    // leap from here onto the same floor is taken instead.
                    if self.aim.is_none()
                        && self.fly_at.is_none()
                        && let Some((aim, spot)) = hooks
                            .aim_onward
                            .and_then(|onward| onward(sample.position, landing, leap.run))
                    {
                        self.aim = Some(aim);
                        self.fly_at = Some(spot);
                        landing = spot;
                        self.approach.aimed_onward = true;
                    }
                    // No leap from where the body came to rest is kept, and it
                    // stands off its takeoff: it goes back to the takeoff
                    // rather than fly the planned leap from the wrong spot.
                    let off_takeoff = position.distance(takeoff);
                    if hooks.aim_leap_from.is_some()
                        && self.aim.is_none()
                        && off_takeoff > NO_AIM_TAKEOFF_RADIUS
                    {
                        // Going back is worth it only for a gap some move this
                        // client makes can close.
                        if self.approach.adjustments < MOST_TAKEOFF_ADJUSTMENTS
                            && off_takeoff > SHORTEST_SIDESTEP_METERS
                        {
                            self.approach.adjustments += 1;
                            self.phase = LeapPhase::None;
                            self.walking_back = Some(takeoff);
                            return RouteDriveStep::default();
                        }
                        // The body kept no leap from where it stands and cannot
                        // get back to its takeoff: the walk plans again from
                        // here rather than fly the planned leap from the wrong
                        // spot.
                        self.state = RouteDriveState::Blocked;
                        return RouteDriveStep {
                            stop_travel: travelling,
                            ..RouteDriveStep::default()
                        };
                    }
                }
                let error = signed_degrees(
                    compass_heading(flat(landing) - position) - sample.heading_degrees,
                );
                if error.abs() > LEAP_FACING_DEGREES {
                    if self.approach.turns == 0 {
                        self.approach.turned_at = position;
                    }
                    self.approach.turns += 1;
                    self.approach.turned_degrees += error.abs();
                    return RouteDriveStep {
                        turn: Some(turn_by(error, MovePace::Run)),
                        ..RouteDriveStep::default()
                    };
                }
                let aimed = self.aim;
                let flown = aimed.unwrap_or(RouteLeapAim {
                    power: leap.power,
                    run: leap.run,
                });
                self.approach.takeoff_error = position.distance(flat(self.leg(self.leg_index - 1)));
                self.approach.charged_at = position;
                self.approach.charged_from = sample.position;
                self.approach.flown_at = self.fly_at;
                self.approach.facing_error = -error;
                self.approach.planned_power = leap.power;
                self.approach.planned_run = leap.run;
                self.approach.aimed = aimed;
                self.takeoff_height = sample.position.z;
                self.highest = sample.position.z;
                self.leap_flew = true;
                self.touched_down = false;
                self.phase = LeapPhase::Charging;
                RouteDriveStep {
                    jump: Some(flown.power),
                    jump_pace: Some(if flown.run {
                        MovePace::Run
                    } else {
                        MovePace::Walk
                    }),
                    ..RouteDriveStep::default()
                }
            }
            LeapPhase::Charging => {
                if sample.airborne {
                    self.phase = LeapPhase::Flying;
                    return RouteDriveStep::default();
                }
                if sample.moves.jump_charging {
                    return RouteDriveStep::default();
                }
                if !travelling || travel.elapsed_seconds > TAKEOFF_GRACE_SECONDS {
                    self.finish(RouteDriveState::Blocked, travelling, turning)
                } else {
                    RouteDriveStep::default()
                }
            }
            LeapPhase::Flying | LeapPhase::None => {
                self.highest = self.highest.max(sample.position.z);
                if sample.airborne {
                    return RouteDriveStep {
                        stop_travel: travelling
                            && sample.position.z > self.takeoff_height + LET_GO_RISE_METERS,
                        ..RouteDriveStep::default()
                    };
                }
                if !self.touched_down {
                    self.touched_down = true;
                    self.touchdown = position;
                    self.landing_touchdown = sample.position;
                }
                if travelling {
                    return RouteDriveStep {
                        stop_travel: true,
                        ..RouteDriveStep::default()
                    };
                }
                if !sample.still {
                    return RouteDriveStep::default();
                }
                self.phase = LeapPhase::None;
                self.landing_slide = position.distance(self.touchdown);
                self.measure_landing(position, sample.position.z, landing);
                if (sample.position.z - landing.z).abs() > LANDING_HEIGHT {
                    return self.finish(RouteDriveState::Blocked, travelling, turning);
                }
                self.landing_error = position.distance(flat(landing));
                if self.landing_error > LANDING_RADIUS {
                    return self.finish(RouteDriveState::LandedElsewhere, travelling, turning);
                }
                // A leap aimed onward came down on the right floor but not on
                // the route's own landing, so the rest of the route is planned
                // again from where the body is.
                if self.fly_at.is_some() {
                    return self.finish(RouteDriveState::LandedElsewhere, travelling, turning);
                }
                self.leg_index += 1;
                RouteDriveStep::default()
            }
        }
    }

    fn finish(
        &mut self,
        state: RouteDriveState,
        travelling: bool,
        turning: bool,
    ) -> RouteDriveStep {
        self.phase = LeapPhase::None;
        self.fly_at = None;
        self.cutting = Cut::default();
        self.settling = None;
        self.sidestep_to = None;
        self.state = state;
        RouteDriveStep {
            stop_travel: travelling,
            stop_turn: turning,
            ..RouteDriveStep::default()
        }
    }

    /// A body standing still anywhere on the floor it walks across to the
    /// next leap's takeoff first tries that leap from where it stands: a leap
    /// kept from here by the same checks as the route's own is taken from
    /// here, rather than after a walk to the spot the route planned it from.
    /// Asked once a stop.
    fn leap_from_here(
        &mut self,
        sample: &RouteDriveSample,
        hooks: &RouteDriveHooks<'_>,
        position: Vec2,
        travelling: bool,
    ) {
        let Some(aim_leap_from) = hooks.aim_leap_from else {
            return;
        };
        if travelling || !sample.still || self.cutting.pace.is_some() {
            return;
        }
        let mut leap = self.leg_index;
        while leap < self.leg_count() && !self.leaps.contains_key(&leap) {
            leap += 1;
        }
        if leap >= self.leg_count() || self.leg_index >= leap {
            return;
        }
        if let Some(asked) = self.aimed_from_here
            && asked.distance(position) < STILL_METERS
        {
            return;
        }
        self.aimed_from_here = Some(position);
        let takeoff = self.leg(leap - 1);
        let on_its_floor = match hooks.same_floor {
            Some(same_floor) => same_floor(sample.position, takeoff),
            None => (sample.position.z - takeoff.z).abs() <= LEAP_FROM_HERE_HEIGHT,
        };
        if !on_its_floor
            || aim_leap_from(
                sample.position,
                self.leg(leap),
                self.leaps[&leap].run,
                position.distance(flat(takeoff)) <= AT_ITS_TAKEOFF_RADIUS,
            )
            .is_none()
        {
            return;
        }
        let mut skipped = 0.0;
        let mut from = position;
        for index in self.leg_index..leap {
            skipped += from.distance(flat(self.leg(index)));
            from = flat(self.leg(index));
        }
        self.leg_index = leap;
        self.approach = LeapApproach {
            walk_skipped: skipped,
            ..LeapApproach::new(leap - 1, MovePace::Walk)
        };
    }

    /// Whether a body standing still within [`SHORTEST_RUN_UP_METERS`] of a
    /// leg's takeoff is at its takeoff already, because a leap aimed from
    /// exactly where it stands is kept by the same checks as the route's own.
    fn aims_from_here(
        &self,
        sample: &RouteDriveSample,
        hooks: &RouteDriveHooks<'_>,
        index: i32,
        travelling: bool,
    ) -> bool {
        let Some(aim_leap_from) = hooks.aim_leap_from else {
            return false;
        };
        if travelling || !sample.still {
            return false;
        }
        let Some(leap) = self.leaps.get(&(index + 1)) else {
            return false;
        };
        let position = sample.position.truncate();
        // On the takeoff's own floor, as a leap taken from anywhere on it is.
        let on_its_floor = match hooks.same_floor {
            Some(same_floor) => same_floor(sample.position, self.leg(index)),
            None => (sample.position.z - self.leg(index).z).abs() <= LEAP_FROM_HERE_HEIGHT,
        };
        on_its_floor
            && position.distance(flat(self.leg(index))) <= SHORTEST_RUN_UP_METERS
            && aim_leap_from(
                sample.position,
                self.leg(index + 1),
                leap.run,
                position.distance(flat(self.leg(index))) <= AT_ITS_TAKEOFF_RADIUS,
            )
            .is_some()
    }

    /// Whether the body has reached a leg's end: near it, or past it. The
    /// takeoff of a leap counts only once the body stands within
    /// [`TAKEOFF_RADIUS`] of it, or `stop_meters` for a body running or
    /// walking up to it.
    fn reached(&self, position: Vec2, index: i32, stop_meters: f32) -> bool {
        let end = flat(self.leg(index));
        // A takeoff is a spot, not a line.
        if self.leaps.contains_key(&(index + 1)) {
            return position.distance(end) <= TAKEOFF_RADIUS.max(stop_meters);
        }
        let along = end - flat(self.leg(index - 1));
        let past = along.length_squared() > 1e-6 && (position - end).dot(along) >= 0.0;
        past || position.distance(end) <= ARRIVAL_RADIUS
    }

    /// Whether the route turns more than [`TURN_IN_PLACE_DEGREES`] at a leg's
    /// end between two walked legs.
    fn turns_sharply_at(&self, corner: i32) -> bool {
        if corner + 1 >= self.leg_count()
            || self.leaps.contains_key(&corner)
            || self.leaps.contains_key(&(corner + 1))
        {
            return false;
        }
        let current = flat(self.leg(corner)) - flat(self.leg(corner - 1));
        let next = flat(self.leg(corner + 1)) - flat(self.leg(corner));
        signed_degrees(compass_heading(next) - compass_heading(current)).abs()
            > TURN_IN_PLACE_DEGREES
    }

    /// Whether a sidestep is still to run, and what to send while it is
    /// (`Some` while it is, with the step).
    fn still_stepping(
        &mut self,
        sample: &RouteDriveSample,
        position: Vec2,
        spot: Vec2,
        travelling: bool,
        turning: bool,
    ) -> Option<RouteDriveStep> {
        let strafe = sample.moves.strafe;
        if strafe.sequence > self.sidestep_asked {
            if strafe.state == ScriptedMoveState::Moving {
                return Some(RouteDriveStep::default());
            }
            self.sidestep_to = None;
            return None;
        }
        if let Some(again) = self.sidestep(position, spot, sample, travelling, turning) {
            return Some(again);
        }
        self.sidestep_to = None;
        None
    }

    /// A step onto a spot the body stands too near to walk to: it turns
    /// until the spot lies square to its side and steps across. `None` when
    /// the spot is farther than a walk's own shortest carry, or so near that
    /// a sidestep would not move at all.
    fn sidestep(
        &mut self,
        position: Vec2,
        spot: Vec2,
        sample: &RouteDriveSample,
        travelling: bool,
        turning: bool,
    ) -> Option<RouteDriveStep> {
        let gap = position.distance(spot);
        if !(SHORTEST_SIDESTEP_METERS..=SHORTEST_WALK_METERS).contains(&gap) || !sample.still {
            return None;
        }
        let bearing = compass_heading(spot - position);
        let to_the_right = signed_degrees(bearing - 90.0 - sample.heading_degrees);
        let to_the_left = signed_degrees(bearing + 90.0 - sample.heading_degrees);
        let right = to_the_right.abs() <= to_the_left.abs();
        let error = if right { to_the_right } else { to_the_left };
        if error.abs() > STEER_TOLERANCE_DEGREES {
            return Some(RouteDriveStep {
                turn: if turning {
                    None
                } else {
                    Some(turn_by(error, MovePace::Walk))
                },
                stop_travel: travelling,
                ..RouteDriveStep::default()
            });
        }
        if travelling || turning {
            return Some(RouteDriveStep::default());
        }
        self.sidestep_to = Some(spot);
        self.sidestep_asked = sample.moves.strafe.sequence;
        Some(RouteDriveStep {
            travel: Some(MoveRequest::new(
                if right {
                    MoveDirection::StrafeRight
                } else {
                    MoveDirection::StrafeLeft
                },
                MovePace::Walk,
                SHORTEST_SIDESTEP_METERS.max(gap - SIDESTEP_CARRY_METERS),
            )),
            ..RouteDriveStep::default()
        })
    }

    /// The pace to go at: a walk where the world says to go carefully; a
    /// cut's own while cutting and until the body faces the leg ahead; a walk
    /// up to a takeoff nearer than a run carries the body on with
    /// [`SHORTEST_RUN_UP_METERS`] to spare; and otherwise a run.
    fn pace_for(
        &self,
        hooks: &RouteDriveHooks<'_>,
        position: Vec2,
        walk_stop_meters: f32,
        run_stop_meters: f32,
        running: bool,
    ) -> MovePace {
        if hooks.careful_at.is_some_and(|careful| careful(position)) {
            return MovePace::Walk;
        }
        if let Some(pace) = self.cutting.pace.or(self.settling) {
            return pace;
        }
        if walk_stop_meters > 0.0
            && !running
            && self.leaps.contains_key(&(self.leg_index + 1))
            && position.distance(flat(self.leg(self.leg_index)))
                < run_stop_meters.max(0.0) + SHORTEST_RUN_UP_METERS
        {
            MovePace::Walk
        } else {
            MovePace::Run
        }
    }

    /// The point the body steers for on the leg to a leg end.
    fn aim_along(&self, position: Vec2, index: i32) -> Vec2 {
        let start = flat(self.leg(index - 1));
        let end = flat(self.leg(index));
        let along = end - start;
        let length = along.length();
        if length < 1e-3 {
            return end;
        }
        let direction = along / length;
        let offset = position - start;
        let aside = (offset.x * direction.y - offset.y * direction.x).abs();
        let reach = offset.dot(direction).max(0.0)
            + LOOK_AHEAD_METERS.max(aside * LOOK_AHEAD_PER_METER_ASIDE);
        if reach >= length {
            end
        } else {
            start + direction * reach
        }
    }

    /// How far the body is from a leg's end, measured along the leg.
    fn along_to(&self, position: Vec2, index: i32) -> f32 {
        let end = flat(self.leg(index));
        let along = end - flat(self.leg(index - 1));
        let length = along.length();
        if length > 1e-3 {
            (end - position).dot(along / length)
        } else {
            position.distance(end)
        }
    }

    /// How the corner at a leg's end is taken, worked out once, the first
    /// time the body heads for it.
    fn cut_for(
        &mut self,
        corner: i32,
        turning: Option<RouteTurning>,
        hooks: &RouteDriveHooks<'_>,
    ) -> Cut {
        if self.cut_planned_for != corner {
            self.cut_planned_for = corner;
            self.cut = self.plan_cut(corner, turning, hooks);
            if self.cut.pace == Some(MovePace::Run) {
                self.corners_run_around += 1;
            } else if self.turns_sharply_at(corner) {
                self.corners_turned_in_place += 1;
            }
        }
        self.cut
    }

    /// A cut around the corner at a leg's end, at a run, where the arc of a
    /// running turn passes; none where it does not.
    fn plan_cut(
        &self,
        corner: i32,
        turning: Option<RouteTurning>,
        hooks: &RouteDriveHooks<'_>,
    ) -> Cut {
        let (Some(can_cut_along), Some(speeds)) = (hooks.can_cut_along, turning) else {
            return Cut::default();
        };
        if corner + 1 >= self.leg_count()
            || self.leaps.contains_key(&corner)
            || self.leaps.contains_key(&(corner + 1))
        {
            return Cut::default();
        }
        let before = flat(self.leg(corner - 1));
        let at = flat(self.leg(corner));
        let after = flat(self.leg(corner + 1));
        let inbound = before.distance(at);
        let outbound = at.distance(after);
        if inbound < 1e-3 || outbound < 1e-3 {
            return Cut::default();
        }
        let turn = signed_degrees(compass_heading(after - at) - compass_heading(at - before));
        if turn.abs() <= STEER_TOLERANCE_DEGREES || turn.abs() > SHARPEST_CUT_DEGREES {
            return Cut::default();
        }
        let room = (if corner == 1 { inbound } else { inbound * 0.5 }).min(outbound * 0.5);
        self.try_cut(
            can_cut_along,
            corner,
            turn,
            room,
            MovePace::Run,
            speeds.run_speed,
            speeds.run_turn_degrees_per_second,
        )
    }

    #[allow(clippy::too_many_arguments)]
    fn try_cut(
        &self,
        can_cut_along: &dyn Fn(&[Vec3]) -> bool,
        corner: i32,
        turn: f32,
        room: f32,
        pace: MovePace,
        speed: f32,
        degrees_per_second: f32,
    ) -> Cut {
        if speed <= 0.0 || degrees_per_second <= 0.0 {
            return Cut::default();
        }
        let radius = speed / (degrees_per_second * (std::f32::consts::PI / 180.0));
        let tangent = radius * (turn.abs() * 0.5 * (std::f32::consts::PI / 180.0)).tan();
        if tangent > room {
            return Cut::default();
        }
        let arc = self.arc(corner, turn, radius, tangent);
        if !can_cut_along(&arc) {
            return Cut::default();
        }
        let onward = (flat(self.leg(corner + 1)) - flat(self.leg(corner))).normalize();
        Cut {
            pace: Some(pace),
            lead: tangent + speed * CUT_EARLY_SECONDS,
            exit: flat(*arc.last().expect("an arc has points")),
            onward,
        }
    }

    /// The arc a body turning at a steady rate, around a circle of `radius`,
    /// follows around a corner turning `turn` degrees, right for more than
    /// zero: from `tangent` before the corner on the leg in to that far along
    /// the leg out, as points about [`ARC_STEP_METERS`] apart at the legs'
    /// heights.
    fn arc(&self, corner: i32, turn: f32, radius: f32, tangent: f32) -> Vec<Vec3> {
        let before = self.leg(corner - 1);
        let at = self.leg(corner);
        let after = self.leg(corner + 1);
        let start = lerp(at, before, tangent / flat(before).distance(flat(at)));
        let end = lerp(at, after, tangent / flat(at).distance(flat(after)));
        let inbound = (flat(at) - flat(before)).normalize();
        let inside = if turn > 0.0 {
            Vec2::new(inbound.y, -inbound.x)
        } else {
            Vec2::new(-inbound.y, inbound.x)
        };
        let centre = flat(start) + inside * radius;
        let spoke = flat(start) - centre;
        let sweep = -turn * (std::f32::consts::PI / 180.0);
        let steps = ((radius * sweep.abs() / ARC_STEP_METERS).ceil() as i32).max(1);
        let mut points = Vec::with_capacity(steps as usize + 1);
        for step in 0..=steps {
            let share = step as f32 / steps as f32;
            let cos = (sweep * share).cos();
            let sin = (sweep * share).sin();
            let point =
                centre + Vec2::new(spoke.x * cos - spoke.y * sin, spoke.x * sin + spoke.y * cos);
            points.push(point.extend(start.z + (end.z - start.z) * share));
        }
        points
    }
}

/// The most of a turn in place a body may have left when it moves off at a
/// pace, so that the arc it then goes round bows out from its leg by no more
/// than [`MOVE_OFF_DRIFT_METERS`]: never more than
/// [`TURN_IN_PLACE_DEGREES`], and that much where the body's speed and turn
/// rate at the pace are not known.
#[must_use]
pub fn move_off_degrees(turning: Option<RouteTurning>, pace: MovePace) -> f32 {
    let Some(speeds) = turning else {
        return TURN_IN_PLACE_DEGREES;
    };
    let (speed, degrees_per_second) = if pace == MovePace::Run {
        (speeds.run_speed, speeds.run_turn_degrees_per_second)
    } else {
        (speeds.walk_speed, speeds.walk_turn_degrees_per_second)
    };
    if !(speed > 0.0) || !(degrees_per_second > 0.0) {
        return TURN_IN_PLACE_DEGREES;
    }
    let radius = speed / (degrees_per_second * (std::f32::consts::PI / 180.0));
    if radius <= MOVE_OFF_DRIFT_METERS {
        return TURN_IN_PLACE_DEGREES;
    }
    let degrees = (1.0 - MOVE_OFF_DRIFT_METERS / radius).acos() * (180.0 / std::f32::consts::PI);
    degrees.clamp(STEER_TOLERANCE_DEGREES, TURN_IN_PLACE_DEGREES)
}

/// A turn through an angle at a pace: the body's hold for turning is the
/// same as for travelling.
fn turn_by(degrees: f32, pace: MovePace) -> MoveRequest {
    MoveRequest::new(
        if degrees > 0.0 {
            MoveDirection::TurnRight
        } else {
            MoveDirection::TurnLeft
        },
        pace,
        degrees.abs(),
    )
}

fn flat(point: Vec3) -> Vec2 {
    point.truncate()
}

fn lerp(a: Vec3, b: Vec3, t: f32) -> Vec3 {
    a * (1.0 - t) + b * t
}

/// The compass heading of a direction, degrees clockwise from north (+y).
#[must_use]
pub fn compass_heading(direction: Vec2) -> f32 {
    normalized_degrees(direction.x.atan2(direction.y) * (180.0 / std::f32::consts::PI))
}
