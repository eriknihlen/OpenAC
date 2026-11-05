//! Leaps: standing long jumps between pieces of floor no walk joins.
//!
//! A *piece* of floor is a patch of clear standing points joined by steps
//! both ways (a platform, a ledge, the floor beyond a gap). Leaps aim at the
//! middle of each other piece in reach, solve every takeoff on the line back
//! toward the body's own piece for the jump that comes down there, and keep
//! the ones that still land when flown a little off. The flight, landing hop
//! and ground slide follow how the physics carries a body, so a leap is
//! planned to where the body comes to rest, not just where its arc lands.

use std::collections::{HashMap, HashSet};
use std::ops::Deref;
use std::sync::{Arc, Mutex};

use glam::{Vec2, Vec3};

use crate::grid::{DIRECTION_COUNT, NavGrid};
use crate::order::{compare_f32, fixed, intro_sort, signed_two_decimals};

/// Gravity, metres per second squared.
pub const GRAVITY: f32 = 9.8;

/// What a body can leap: how fast it walks and runs (metres per second), how
/// high a jump at full power lifts it, the deepest drop a route may take it
/// down (metres), and how the physics carries it through a jump and its
/// landing. A jump at a share of full power lifts the body that share as
/// high, but never less than [`NavLeapAbility::LOWEST_JUMP`].
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct NavLeapAbility {
    /// Walking speed, metres per second.
    pub walk_speed: f32,
    /// Running speed, metres per second.
    pub run_speed: f32,
    /// How high a full-power jump lifts the body, metres.
    pub full_jump_height: f32,
    /// The deepest drop a route may take, metres.
    pub maximum_drop: f32,
    /// How the physics carries the body.
    pub physics: NavLeapPhysics,
}

impl NavLeapAbility {
    /// The least height any jump lifts the body, metres.
    pub const LOWEST_JUMP: f32 = 0.35;

    /// How high a jump at `power`, from 0 to 1, lifts the body, metres.
    #[must_use]
    pub fn jump_height(&self, power: f32) -> f32 {
        Self::LOWEST_JUMP.max(self.full_jump_height * power.clamp(0.0, 1.0))
    }
}

/// How the physics carries a body through a jump, for a leap to be planned
/// to where the body comes to rest. The body moves in steps `step_seconds`
/// long, and gravity pulls on it only through a step that begins with it
/// off the ground. So a body leaving the ground rises through a whole step
/// before it starts to fall, and one coming down keeps its speed along the
/// ground to the end of the step it lands in. Landing throws it back up at
/// `elasticity` of the speed it fell at, and at [`Self::LEAST_BOUNCE`] or
/// faster it leaves the ground again in a short hop. Once it stays down,
/// friction takes `friction` of its speed away each second, step by step,
/// until a step begins slower than [`Self::STOPPED_SPEED`]. With no step
/// length a jump flies an unbroken arc and stops where it comes down.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct NavLeapPhysics {
    /// The physics step, seconds.
    pub step_seconds: f32,
    /// How much of its falling speed a landing throws the body back up at.
    pub elasticity: f32,
    /// How much of its speed along the ground friction takes each second.
    pub friction: f32,
}

impl NavLeapPhysics {
    /// Gravity, metres per second squared.
    pub const GRAVITY: f32 = GRAVITY;
    /// The least speed a landing throws a body back up at that lifts it off
    /// the ground again, metres per second.
    pub const LEAST_BOUNCE: f32 = 0.25;
    /// A body sliding slower than this stops, metres per second.
    pub const STOPPED_SPEED: f32 = 0.25;

    /// How far above its takeoff a body launched upward at `launch` is after
    /// `seconds` in the air.
    #[must_use]
    pub fn height(&self, launch: f32, seconds: f32) -> f32 {
        let falling = self.falling(seconds);
        launch * seconds - 0.5 * GRAVITY * falling * falling
    }

    /// How fast a body launched upward at `launch` is falling after
    /// `seconds` in the air; below zero while it still rises.
    #[must_use]
    pub fn fall_speed(&self, launch: f32, seconds: f32) -> f32 {
        GRAVITY * self.falling(seconds) - launch
    }

    /// The launch speed that puts a body `rise` above its takeoff after
    /// `seconds` in the air.
    #[must_use]
    pub fn launch_for(&self, rise: f32, seconds: f32) -> f32 {
        let falling = self.falling(seconds);
        (rise + 0.5 * GRAVITY * falling * falling) / seconds
    }

    /// How far along the ground a body moving at `speed` goes on off the
    /// ground from the point its arc comes down through, falling at
    /// `fall_speed`: through the rest of the step it lands in, and through
    /// any hop its landing throws it into.
    #[must_use]
    pub fn carry(&self, speed: f32, fall_speed: f32) -> f32 {
        let step = self.step_seconds;
        if !(step > 0.0) || !(speed > 0.0) {
            return 0.0;
        }
        let mut seconds = step / 2.0;
        let mut fall = fall_speed + GRAVITY * step / 2.0;
        let mut hop = self.elasticity * fall;
        while hop >= Self::LEAST_BOUNCE {
            let landing = (hop * hop + 2.0 * GRAVITY * hop * step).sqrt();
            seconds += step + (hop + landing) / GRAVITY + step / 2.0;
            fall = landing + GRAVITY * step / 2.0;
            hop = self.elasticity * fall;
        }
        speed * seconds
    }

    /// How far a body moving at `speed` slides along the ground once it
    /// stays down, step by step, until a step begins slower than
    /// [`Self::STOPPED_SPEED`]. Infinity on ground without friction.
    #[must_use]
    pub fn ground_slide(&self, speed: f32) -> f32 {
        let step = self.step_seconds;
        if !(step > 0.0) || !(speed > 0.0) {
            return 0.0;
        }
        let kept = (1.0 - self.friction.clamp(0.0, 1.0)).powf(step);
        if !(kept < 1.0) {
            return f32::INFINITY;
        }
        let mut slid = 0.0;
        let mut moving = speed;
        while moving >= Self::STOPPED_SPEED {
            slid += moving * kept * step;
            moving *= kept;
        }
        slid
    }

    /// How far along open ground a body moving at `speed` goes on from the
    /// point its arc comes down through, falling at `fall_speed`, to where it
    /// comes to rest: its [`Self::carry`] and then its
    /// [`Self::ground_slide`].
    #[must_use]
    pub fn slide(&self, speed: f32, fall_speed: f32) -> f32 {
        self.carry(speed, fall_speed) + self.ground_slide(speed)
    }

    /// How long a body has been falling after `seconds` in the air: not at
    /// all through its first step.
    fn falling(&self, seconds: f32) -> f32 {
        (seconds - self.step_seconds.max(0.0)).max(0.0)
    }
}

/// A standing long jump from one node to another: the body stands on
/// `from` facing `to`, charges a jump to `power`, and leaves the ground at
/// running or walking pace. `risk` counts what makes it less than easy:
/// aiming nearer the edge of the floor it lands on than that floor's middle,
/// coming down with little room when flown a little off, clearing the edge
/// it jumps up past by little, or leaving from near the edge of its own
/// floor. An easy leap has none.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NavLeap {
    /// The takeoff node.
    pub from: i32,
    /// The node the body comes to rest on.
    pub to: i32,
    /// The jump's charge, 0 to 1.
    pub power: f32,
    /// Whether it leaves at a run.
    pub run: bool,
    /// How risky it is.
    pub risk: f32,
}

/// A leap a route takes: the leg that ends at `legs[leg_index]` is flown
/// from `legs[leg_index - 1]` as a standing long jump charged to `power`, at
/// running or walking pace.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NavRouteLeap {
    /// The leg the leap ends.
    pub leg_index: i32,
    /// The jump's charge, 0 to 1.
    pub power: f32,
    /// Whether it leaves at a run.
    pub run: bool,
}

/// A leap aimed again from where a body really stands: charged to `power`
/// and left at running or walking pace, facing the landing.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NavLeapAim {
    /// The jump's charge, 0 to 1.
    pub power: f32,
    /// Whether it leaves at a run.
    pub run: bool,
}

/// How far the body flies between the points of its arc that are tested: a
/// column's width.
const SAMPLE_STEP: f32 = 0.25;
const LEAST_AIM_REACH: f32 = 4.0;
/// How much farther than its spot an arc is followed before it counts as
/// landing nowhere.
const FLIGHT_OVERSHOOT: f32 = 4.0;
/// The shortest leap kept, measured flat; anything shorter is a step.
const SHORTEST_LEAP: f32 = 0.5;
/// A jump is aimed again for how far the body is carried on off the ground
/// after the arc last aimed comes down, at most [`AIM_PASSES`] times, until
/// that distance changes by no more than [`SLIDE_TOLERANCE`].
const AIM_PASSES: i32 = 3;
const SLIDE_TOLERANCE: f32 = 0.05;
/// How much more power than it was charged to a jump may leave with: a
/// charge releases on the first frame past its time.
const POWER_SLACK: f32 = 1.0 / 30.0;
/// How far to either side of its landing a body may face as it leaves.
const HEADING_SLACK_DEGREES: f32 = 2.0;
/// How far short of its takeoff, or past it, a body may stop before it
/// jumps, metres. Measured live over sixty leaps, a body charges a median
/// 0.05 m from its takeoff and nine in ten within 0.20 m, so a leap is kept
/// that still lands from three times the usual miss.
pub const TAKEOFF_SLACK: f32 = 0.15;
/// What each metre a landing may move costs a takeoff when takeoffs are
/// ranked. A landing that may move more than [`EASY_SHIFT_METERS`] is a
/// risk.
const SHIFT_COST: f32 = 4.0;
const EASY_SHIFT_METERS: f32 = 0.5;
/// A leap up onto higher floor must clear that floor's lip by at least
/// [`LEAST_LIP_CLEARANCE`] across the body's whole width. Clearing it by
/// less than [`EASY_LIP_CLEARANCE`] is a risk.
const LEAST_LIP_CLEARANCE: f32 = 0.15;
const EASY_LIP_CLEARANCE: f32 = 0.5;
/// A takeoff standing less than this far back from the edge it leaves is a
/// risk.
const EASY_TAKEOFF_BACK: f32 = 0.5;
const LEAST_MISS_METERS: f32 = 0.1;
const WALK_MISS_SHARE: f32 = 0.02;
const RUN_MISS_SHARE: f32 = 0.03;
const MOST_MISS_RISK: f32 = 8.0;
/// What a leap's power costs, metres, when takeoffs are ranked.
const POWER_COST: f32 = 2.0;
/// How far in from its piece's edge, in steps, a leap aims.
const CENTRE_ROOM_STEPS: i32 = 12;
const INSIDE_ROOM_STEPS: i32 = 4;
const NEAR_ROOM_STEPS: i32 = 2;
/// A leap flown a little off must still come down at least this many steps
/// from its piece's edge, or as far as a narrower piece allows; less than
/// [`EASY_LANDING_ROOM_STEPS`] is a risk.
const LEAST_LANDING_ROOM_STEPS: i32 = 2;
const EASY_LANDING_ROOM_STEPS: i32 = 4;
/// A takeoff nearer the edge of its floor than this many steps, or than that
/// floor's middle, is a risk.
const EASY_TAKEOFF_ROOM_STEPS: i32 = 4;
/// A spot counts as already aimed at when a kept leap no riskier, from
/// within [`SAME_TAKEOFF_METERS`] of a takeoff, comes down within
/// [`SAME_LANDING_METERS`] of it.
const SAME_LANDING_METERS: f32 = 0.75;
const SAME_TAKEOFF_METERS: f32 = 1.5;
/// How far past the heights a jump can reach the nearest spot of a piece
/// may lie and still set the line a leap is aimed along.
const SPOT_HEIGHT_SLACK: f32 = 1.0;
const SMALL_PIECE_NODES: i32 = 1024;
const SMALL_PIECE_SPOT_COLUMNS: i32 = 2;
const LARGE_PIECE_SPOT_COLUMNS: i32 = 4;
/// Spots are sorted into square tiles this many columns wide.
const TILE_COLUMNS: i32 = 16;
/// How many of the most promising takeoffs are flown, at each pace, for
/// each spot.
const TRIES_PER_PACE: i32 = 2;
const PACES: [bool; 2] = [false, true];
/// How far, measured flat, a body or its landing may stand from the node
/// [`NavLeapFinder::aim_from`] places it on.
const AIM_FROM_RADIUS: f32 = 0.5;
/// How far above or below that node it may stand.
const AIM_FROM_HEIGHT_TOLERANCE: f32 = 1.0;
/// How many edges a sliding body is followed along before it is taken to
/// stop at the next.
const MOST_EDGE_SLIDES: i32 = 3;
/// The least share of a slide that goes on along a ledge: cos 80 degrees,
/// so a body meeting one nearly head on stops.
const LEAST_EDGE_SLIDE_SHARE: f32 = 0.17;

/// About how far from its spot a leap comes to rest, measured live over
/// seventy landings: a leap flown farther misses by more, and one flown at a
/// run misses by more again.
fn miss_of(flat: f32, run: bool) -> f32 {
    LEAST_MISS_METERS + flat * if run { RUN_MISS_SHARE } else { WALK_MISS_SHARE }
}

/// How much of a risk a leap's miss is against the room its landing leaves:
/// the square of the one over the other, so leaps that chain are counted
/// fairly.
fn miss_risk(flat: f32, run: bool, room: f32) -> f32 {
    let share = miss_of(flat, run) / room.max(LEAST_MISS_METERS);
    MOST_MISS_RISK.min(share * share)
}

fn launch(height: f32) -> f32 {
    (2.0 * GRAVITY * height).sqrt()
}

fn flat(point: Vec3) -> Vec2 {
    point.truncate()
}

fn sign(value: f32) -> i32 {
    if value > 0.0 {
        1
    } else if value < 0.0 {
        -1
    } else {
        0
    }
}

fn turned(heading: Vec2, degrees: f32) -> Vec2 {
    let radians = degrees * (std::f32::consts::PI / 180.0);
    let (sin, cos) = (radians.sin(), radians.cos());
    Vec2::new(
        heading.x * cos - heading.y * sin,
        heading.x * sin + heading.y * cos,
    )
}

/// How far, measured flat, a body carries at a run on a full-power jump
/// before it is back down at the height it left from.
#[must_use]
pub fn aim_reach_of(ability: &NavLeapAbility) -> f32 {
    reach_down_to(ability, 0.0)
}

/// How far, measured flat, a body carries at a run on a full-power jump
/// before it is down `drop` metres below the height it left from.
fn reach_down_to(ability: &NavLeapAbility, drop: f32) -> f32 {
    let speed = ability.run_speed.max(ability.walk_speed);
    if !(speed > 0.0) {
        return LEAST_AIM_REACH;
    }
    let launch_speed = launch(ability.jump_height(1.0));
    let mut seconds = 0.0f32;
    loop {
        seconds += 0.01;
        if !(ability.physics.height(launch_speed, seconds) > -drop && seconds < 10.0) {
            break;
        }
    }
    LEAST_AIM_REACH.max(speed * seconds)
}

/// The leaps of one piece of floor, by takeoff node, in the order they were
/// found.
#[derive(Debug, Default)]
struct ByTakeoff {
    order: Vec<(i32, Arc<[NavLeap]>)>,
    index: HashMap<i32, usize>,
}

impl ByTakeoff {
    fn get(&self, node: i32) -> Option<&Arc<[NavLeap]>> {
        self.index.get(&node).map(|&i| &self.order[i].1)
    }
}

/// A takeoff solved for a spot.
#[derive(Debug, Clone, Copy)]
struct Candidate {
    node: i32,
    aim_at: Vec3,
    power: f32,
    run: bool,
    risk: f32,
    lip_risk: f32,
    cost: f32,
    settles: bool,
    land_at: Vec3,
    land_power: f32,
}

/// What a line from a spot crosses: the distance at which the spot's floor
/// stops being walkable, the distance at which the takeoff piece's floor
/// begins, and that piece's clear nodes along it with their distances from
/// the spot.
struct Line {
    face_at: f32,
    edge_at: f32,
    takeoffs: Vec<(i32, f32)>,
}

/// Where a finder's leaps may go: taking off within `takeoff_radius`
/// metres, across, of `takeoff_near`, and coming to rest within
/// `landing_within` metres, across, of `toward`. A search toward a goal
/// it cannot walk to looks for the leaps that cross what stopped its walk:
/// they take off near where the walk came nearest the goal and come down
/// nearer it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NavLeapLimits {
    /// Where takeoffs gather.
    pub takeoff_near: Vec3,
    /// How far from it a leap may take off, metres.
    pub takeoff_radius: f32,
    /// What landings come nearer.
    pub toward: Vec3,
    /// How near it a leap must come to rest, metres; infinite for anywhere.
    pub landing_within: f32,
}

/// Finds the leaps a body can take between pieces of floor no walk joins,
/// such as platforms, ledges, and the floor beyond a gap or below a drop.
/// See the module documentation.
#[derive(Debug)]
pub struct NavLeapFinder<G: Deref<Target = NavGrid>> {
    grid: G,
    ability: NavLeapAbility,
    aim_reach: f32,
    limits: Option<NavLeapLimits>,
    by_piece: Mutex<HashMap<i32, Arc<ByTakeoff>>>,
    standing: Mutex<HashMap<i32, Arc<[NavLeap]>>>,
    onward: Mutex<OnwardLeaps>,
}

impl<G: Deref<Target = NavGrid>> NavLeapFinder<G> {
    /// A finder over a grid (borrowed, or shared) for a body.
    #[must_use]
    pub fn new(grid: G, ability: NavLeapAbility) -> Self {
        Self {
            grid,
            ability,
            aim_reach: aim_reach_of(&ability),
            limits: None,
            by_piece: Mutex::new(HashMap::new()),
            standing: Mutex::new(HashMap::new()),
            onward: Mutex::new(HashMap::new()),
        }
    }

    /// A finder whose leaps keep within limits: see [`NavLeapLimits`].
    #[must_use]
    pub fn limited(grid: G, ability: NavLeapAbility, limits: NavLeapLimits) -> Self {
        Self {
            limits: Some(limits),
            ..Self::new(grid, ability)
        }
    }

    /// The grid the finder leaps over.
    #[must_use]
    pub fn grid(&self) -> &NavGrid {
        &self.grid
    }

    /// What the body can leap.
    #[must_use]
    pub fn ability(&self) -> NavLeapAbility {
        self.ability
    }

    fn surfaces(&self) -> &Surfaces {
        self.grid.surfaces()
    }

    fn by_piece(&self, piece: i32) -> Arc<ByTakeoff> {
        if let Some(found) = self.by_piece.lock().expect("a leap cache").get(&piece) {
            return Arc::clone(found);
        }
        let found = Arc::new(self.find_from(piece, &self.limit_bounds()));
        self.by_piece
            .lock()
            .expect("a leap cache")
            .insert(piece, Arc::clone(&found));
        found
    }

    /// The leaps a body standing on `node` can take, found once for each
    /// piece of floor. A piece's leaps are solved from the takeoffs each aim
    /// traced across it, and a body walks to one of those before it jumps.
    /// Floor with no room to walk on offers no such walk: wherever a body
    /// stands on it, it stands at the takeoff, so the whole piece's leaps are
    /// open to it, each taken from where the body really is.
    #[must_use]
    pub fn from(&self, node: i32) -> Arc<[NavLeap]> {
        let piece = self.surfaces().piece_of[node as usize];
        if piece < 0 {
            return Arc::from([]);
        }
        let by_takeoff = self.by_piece(piece);
        if let Some(leaps) = by_takeoff.get(node) {
            return Arc::clone(leaps);
        }
        if self.surfaces().deepest[piece as usize] != 0 {
            return Arc::from([]);
        }
        if let Some(found) = self.standing.lock().expect("a leap cache").get(&node) {
            return Arc::clone(found);
        }
        let leaps = standing_leaps(&by_takeoff, node);
        self.standing
            .lock()
            .expect("a leap cache")
            .insert(node, Arc::clone(&leaps));
        leaps
    }

    /// Aims a leap again from exactly where a body stands at `landing`, the
    /// spot a route planned it to come to rest on, as the route's own leaps
    /// are aimed and kept. `None` when no jump from here is kept.
    #[must_use]
    pub fn aim_from(&self, standing: Vec3, landing: Vec3, prefer_run: bool) -> Option<NavLeapAim> {
        self.aim_from_explained(standing, landing, prefer_run, false)
            .ok()
    }

    /// As [`Self::aim_from`], where `at_its_takeoff` says the body stands
    /// within the slack of the takeoff the route planned the leap from. A
    /// landing smaller than the body keeps no room to spare, so the route's
    /// own leap onto one is kept without the check that it still lands when
    /// flown a little off. A leap aimed again from that same takeoff is kept
    /// the same way, and one from farther off is not. The error says why no
    /// leap is kept.
    ///
    /// # Errors
    ///
    /// When no jump from where the body stands is kept, with the reasons.
    pub fn aim_from_explained(
        &self,
        standing: Vec3,
        landing: Vec3,
        prefer_run: bool,
        at_its_takeoff: bool,
    ) -> Result<NavLeapAim, String> {
        let grid: &NavGrid = &self.grid;
        let from = grid.find_standing_node(standing, AIM_FROM_RADIUS, AIM_FROM_HEIGHT_TOLERANCE);
        let spot = grid.find_standing_node(landing, AIM_FROM_RADIUS, AIM_FROM_HEIGHT_TOLERANCE);
        if from < 0 {
            return Err("no floor the grid knows is under the body".to_owned());
        }
        if spot < 0 {
            return Err("no floor the grid knows is under the landing".to_owned());
        }
        let piece = self.surfaces().piece_of[from as usize];
        let target = self.surfaces().piece_of[spot as usize];
        if piece < 0 {
            return Err("the body stands on no piece of floor".to_owned());
        }
        if target < 0 {
            return Err("the landing is on no piece of floor".to_owned());
        }
        if piece == target {
            return Err("the landing is on the floor the body stands on".to_owned());
        }
        let at = grid.position(spot);
        let back = flat(standing) - flat(at);
        let distance = back.length();
        if distance < SHORTEST_LEAP {
            return Err("the landing is too near to leap to".to_owned());
        }
        let mut reasons: Vec<String> = Vec::with_capacity(3);
        let stands_above = standing.z - grid.position(from).z;
        if stands_above.abs() >= 0.05 {
            reasons.push(format!(
                "the body stands {} m off the grid's floor",
                signed_two_decimals(stands_above)
            ));
        }
        let line = self.trace(spot, back / distance, piece, standing.z);
        let rise = at.z - standing.z;
        let before_face = distance - line.face_at - grid.body().radius;
        let deepest = self.surfaces().deepest[target as usize];
        let least_room = 0.max(LEAST_LANDING_ROOM_STEPS.min(deepest - 1));
        let fussy = at_its_takeoff
            && grid.border_distance(spot) as f32 * grid.cell_size() < grid.body().radius;

        // Only at the pace the route planned it: a walking leap aimed again
        // as the lowest running jump was kept by the model and, live, ran off
        // the rock's edge.
        let run = prefer_run;
        let speed = if run {
            self.ability.run_speed
        } else {
            self.ability.walk_speed
        };
        let pace = if run { "running" } else { "walking" };
        let rest = self.aim_short(standing, at, grid.is_terrain(spot), speed, true);
        let land = self.aim_short(standing, at, grid.is_terrain(spot), speed, false);
        let clearance = |leaves: f32| {
            if rise <= 0.0 {
                f32::INFINITY
            } else if before_face > 0.0 {
                self.ability.physics.height(leaves, before_face / speed) - rise
            } else {
                f32::NEG_INFINITY
            }
        };
        let aims_to_rest =
            rest.is_some_and(|(_, _, leaves)| clearance(leaves) >= LEAST_LIP_CLEARANCE);
        let aims_to_land =
            land.is_some_and(|(_, _, leaves)| clearance(leaves) >= LEAST_LIP_CLEARANCE);
        let mut missed: Option<&'static str> = None;
        let flies = |aim_at: Vec3, power: f32, settles: bool, missed: &mut Option<&'static str>| {
            let (lands, lip, room) =
                self.fly_at_point(from, standing, aim_at, power, run, target, settles);
            if lands < 0 || room < least_room {
                *missed = Some(if lip < LEAST_LIP_CLEARANCE {
                    "touches a lip on the way"
                } else if lands < 0 {
                    "does not come to rest on the landing's floor"
                } else {
                    "comes to rest too near the landing floor's edge"
                });
                return false;
            }
            // A landing smaller than the body keeps no room to spare, and the
            // route's own leap is aimed from the least risky takeoff of many.
            // A leap aimed again from wherever the body stands has no such
            // choice, so it must still land when flown a little off.
            if self.worst_room_from_point(standing, aim_at, power, run, target, least_room, settles)
                >= least_room
                || fussy
            {
                return true;
            }
            *missed = Some("does not still land when flown a little off");
            false
        };
        if aims_to_rest {
            let (rest_at, rest_power, _) = rest.expect("aims to rest");
            if flies(rest_at, rest_power, true, &mut missed) {
                return Ok(NavLeapAim {
                    power: rest_power,
                    run,
                });
            }
        }
        if aims_to_land {
            let (land_at, land_power, _) = land.expect("aims to land");
            let distinct = !aims_to_rest
                || flat(rest.expect("aims to rest").0).distance(flat(land_at)) > SLIDE_TOLERANCE;
            if distinct && flies(land_at, land_power, false, &mut missed) {
                return Ok(NavLeapAim {
                    power: land_power,
                    run,
                });
            }
        }
        reasons.push(if rest.is_none() && land.is_none() {
            format!(
                "no jump {pace} reaches the landing, which needs {} of full power before any slide",
                fixed(self.needed_power(standing, at, speed), 2)
            )
        } else if !aims_to_rest && !aims_to_land {
            format!("no jump {pace} clears the lip of the landing's floor")
        } else {
            format!("the jump {pace} {}", missed.unwrap_or("misses"))
        });
        Err(reasons.join("; "))
    }

    /// The share of full power a jump at `speed` needs to come down through
    /// a point, ignoring what it goes on after; above one where none can.
    fn needed_power(&self, from: Vec3, to: Vec3, speed: f32) -> f32 {
        let distance = flat(from).distance(flat(to));
        if !(speed > 0.0) || distance < 1e-3 || !(self.ability.full_jump_height > 0.0) {
            return f32::NAN;
        }
        let launch_speed = self
            .ability
            .physics
            .launch_for(to.z - from.z, distance / speed);
        launch_speed * launch_speed / (2.0 * GRAVITY) / self.ability.full_jump_height
    }

    /// A leap from exactly where a body stands onto the floor a planned
    /// landing lies on, for a body that keeps no aim at the landing itself.
    /// Every spot its own floor leaps to on that floor is aimed at from where
    /// the body is, the planned landing's own neighbours first, and the first
    /// leap kept is taken with the spot it comes to rest on.
    #[must_use]
    pub fn aim_onward(
        &self,
        standing: Vec3,
        landing: Vec3,
        prefer_run: bool,
    ) -> Option<(NavLeapAim, Vec3)> {
        let grid: &NavGrid = &self.grid;
        let from = grid.find_standing_node(standing, AIM_FROM_RADIUS, AIM_FROM_HEIGHT_TOLERANCE);
        let planned = grid.find_standing_node(landing, AIM_FROM_RADIUS, AIM_FROM_HEIGHT_TOLERANCE);
        if from < 0 || planned < 0 {
            return None;
        }
        let piece = self.surfaces().piece_of[from as usize];
        let target = self.surfaces().piece_of[planned as usize];
        if piece < 0 || target < 0 || piece == target {
            return None;
        }
        let at = flat(landing);
        let cached = self
            .onward
            .lock()
            .expect("a leap cache")
            .get(&(from, target))
            .cloned();
        let onward = match cached {
            Some(found) => found,
            None => {
                // Only leaps onto the target that the body can make from
                // where it stands are aimed again below: those come down
                // within its reach (and a slide), and take off within that
                // of it. Its whole piece's leaps, which for open ground
                // aim at every rock and roof of the grid, are not needed.
                // A leap down carries farther than one back to its own height:
                // as far as the deepest drop allowed, then a slide as long
                // again as the level reach.
                let down = reach_down_to(&self.ability, self.ability.maximum_drop);
                let landing = down + self.aim_reach;
                let bounds = Bounds {
                    takeoff: Some((flat(standing), landing + down)),
                    landing: Some((flat(standing), landing)),
                    onto: Some(target),
                };
                let leaps = standing_leaps(&self.find_from(piece, &bounds), from);
                self.onward
                    .lock()
                    .expect("a leap cache")
                    .insert((from, target), Arc::clone(&leaps));
                leaps
            }
        };
        let mut choices: Vec<NavLeap> = onward
            .iter()
            .filter(|leap| {
                self.surfaces().piece_of[leap.to as usize] == target && leap.to != planned
            })
            .copied()
            .collect();
        choices.sort_by(|a, b| {
            compare_f32(
                flat(grid.position(a.to)).distance(at),
                flat(grid.position(b.to)).distance(at),
            )
        });
        for leap in choices {
            let candidate = grid.position(leap.to);
            if let Ok(aimed) = self.aim_from_explained(standing, candidate, prefer_run, false) {
                return Some((aimed, candidate));
            }
        }
        None
    }

    /// The leaps from anywhere on a piece of floor to the spots of every
    /// other piece in reach, by takeoff.
    fn find_from(&self, piece: i32, bounds: &Bounds) -> ByTakeoff {
        let surfaces = self.surfaces();
        let side = surfaces.tile_side;
        let reach = self.tiles_within(self.aim_reach);
        let tiles = (side * side) as usize;
        let mut holds = vec![false; tiles];
        let mut near = vec![false; tiles];
        for (tile, held) in holds.iter_mut().enumerate() {
            if !surfaces.tile_holds(tile, piece) || !self.takes_off_in(tile, bounds) {
                continue;
            }
            *held = true;
            let tile_x = tile as i32 % side;
            let tile_y = tile as i32 / side;
            for y in (tile_y - reach).max(0)..=(tile_y + reach).min(side - 1) {
                for x in (tile_x - reach).max(0)..=(tile_x + reach).min(side - 1) {
                    near[(y * side + x) as usize] = true;
                }
            }
        }

        let mut found = FoundLeaps::default();
        let mut taken = HashSet::new();
        let mut kept: HashMap<i32, Vec<(Vec3, Vec3, f32)>> = HashMap::new();
        let mut candidates = Vec::new();
        for (tile, &is_near) in near.iter().enumerate() {
            if !is_near {
                continue;
            }
            for index in surfaces.tile_start[tile]..surfaces.tile_start[tile + 1] {
                let spot = surfaces.spots[index as usize];
                if surfaces.piece_of[spot as usize] != piece && self.lands_within(spot, bounds) {
                    self.aim_at(
                        piece,
                        &holds,
                        spot,
                        i32::from(surfaces.spot_tiers[index as usize]),
                        &mut candidates,
                        &mut found,
                        &mut taken,
                        &mut kept,
                    );
                }
            }
        }

        let mut leaps = ByTakeoff::default();
        for (from, list) in found.order {
            leaps.index.insert(from, leaps.order.len());
            leaps.order.push((from, Arc::from(list)));
        }
        leaps
    }

    /// Aims leaps from a piece of floor at a spot on another, `tier` bands
    /// out from that piece's middle. Along the line from the spot back toward
    /// the piece's nearest spot, each of the piece's takeoffs is solved for
    /// the jump that comes down on the spot. At each pace the least risky are
    /// flown until one lands with room to spare.
    #[allow(clippy::too_many_arguments)]
    fn aim_at(
        &self,
        piece: i32,
        holds: &[bool],
        spot: i32,
        tier: i32,
        candidates: &mut Vec<Candidate>,
        found: &mut FoundLeaps,
        taken: &mut HashSet<(i32, i32, bool)>,
        kept: &mut HashMap<i32, Vec<(Vec3, Vec3, f32)>>,
    ) {
        let grid: &NavGrid = &self.grid;
        let landing = grid.position(spot);
        let nearest = self.nearest_spot(piece, holds, landing);
        if nearest < 0 {
            return;
        }
        let toward = grid.position(nearest);
        let back = flat(toward) - flat(landing);
        let length = back.length();
        if length < 1e-3 {
            return;
        }
        let line = self.trace(spot, back / length, piece, toward.z);
        if line.takeoffs.is_empty() {
            return;
        }

        candidates.clear();
        let run_out = grid.border_distance(spot) as f32 * grid.cell_size();
        let easy_takeoff_room =
            EASY_TAKEOFF_ROOM_STEPS.min(self.surfaces().deepest[piece as usize] - 1);
        for &(node, distance) in &line.takeoffs {
            let from = grid.position(node);
            let rise = landing.z - from.z;
            let stands_back = (distance - line.edge_at).max(0.0);
            let before_face = distance - line.face_at - grid.body().radius;
            let takeoff_risk = if stands_back < EASY_TAKEOFF_BACK {
                1.0
            } else {
                0.0
            } + if grid.border_distance(node) < easy_takeoff_room {
                1.0
            } else {
                0.0
            };
            for run in PACES {
                let speed = if run {
                    self.ability.run_speed
                } else {
                    self.ability.walk_speed
                };
                let clearance = |leaves: f32| {
                    if rise <= 0.0 {
                        f32::INFINITY
                    } else if before_face > 0.0 {
                        self.ability.physics.height(leaves, before_face / speed) - rise
                    } else {
                        f32::NEG_INFINITY
                    }
                };
                let rest = self.aim_short(from, landing, grid.is_terrain(spot), speed, true);
                let land = self.aim_short(from, landing, grid.is_terrain(spot), speed, false);
                let rest_clearance = rest.map_or(f32::NEG_INFINITY, |r| clearance(r.2));
                let land_clearance = land.map_or(f32::NEG_INFINITY, |l| clearance(l.2));
                let aims_to_rest = rest.is_some() && rest_clearance >= LEAST_LIP_CLEARANCE;
                let aims_to_land = land.is_some() && land_clearance >= LEAST_LIP_CLEARANCE;
                if !aims_to_rest && !aims_to_land {
                    continue;
                }
                let (land_at, land_power, land_launch) = land.unwrap_or((landing, 0.0, 0.0));
                let (aim_at, power, launch_speed) = if aims_to_rest {
                    rest.expect("aims to rest")
                } else {
                    (land_at, land_power, land_launch)
                };
                let clearance_kept = if aims_to_rest {
                    rest_clearance
                } else {
                    land_clearance
                };
                let twin = aims_to_rest
                    && aims_to_land
                    && flat(aim_at).distance(flat(land_at)) > SLIDE_TOLERANCE;
                let flat_distance = flat(from).distance(flat(aim_at));
                let shift = self.landing_shift(launch_speed, speed, rise).max(
                    flat_distance * (HEADING_SLACK_DEGREES * (std::f32::consts::PI / 180.0)).tan(),
                );
                let lip_risk = if clearance_kept < EASY_LIP_CLEARANCE {
                    1.0
                } else {
                    0.0
                };
                let risk = tier as f32
                    + takeoff_risk
                    + lip_risk
                    + if shift > EASY_SHIFT_METERS { 1.0 } else { 0.0 }
                    + miss_risk(flat_distance, run, run_out);
                let cost = flat_distance + rise.abs() + power * POWER_COST + shift * SHIFT_COST;
                candidates.push(Candidate {
                    node,
                    aim_at,
                    power,
                    run,
                    risk,
                    lip_risk,
                    cost,
                    settles: aims_to_rest,
                    land_at,
                    land_power: if twin { land_power } else { f32::NAN },
                });
            }
        }
        if candidates.is_empty() {
            return;
        }
        intro_sort(candidates, |a, b| {
            if a.risk != b.risk {
                compare_f32(a.risk, b.risk)
            } else {
                compare_f32(a.cost, b.cost)
            }
        });

        let target = self.surfaces().piece_of[spot as usize];
        let deepest = self.surfaces().deepest[target as usize];
        let least_room = 0.max(LEAST_LANDING_ROOM_STEPS.min(deepest - 1));
        let easy_room = 0.max(EASY_LANDING_ROOM_STEPS.min(deepest - 1));
        let kept_on_target = kept.entry(target).or_default();
        // Floor narrower than the body cannot hold a jump that leaves a
        // little off, so on a post or a sign a jump is kept for what it is -
        // one that has to be flown as aimed - and carries the risk of that,
        // rather than being ruled out.
        let fussy = run_out < grid.body().radius;
        let flies = |node: i32, aim_at: Vec3, charged: f32, running: bool, settles: bool| {
            let (lands, lip, mut room) =
                self.fly_at(node, aim_at, charged, running, target, settles);
            if lands < 0 || room < least_room {
                return (false, lands, lip, room, false);
            }
            let steady =
                self.worst_room(node, aim_at, charged, running, target, least_room, settles);
            let absorbs = steady >= least_room;
            if absorbs {
                room = room.min(steady);
            }
            (absorbs || fussy, lands, lip, room, absorbs)
        };

        for run in PACES {
            let mut tries = 0;
            for candidate in candidates.iter() {
                if candidate.run != run {
                    continue;
                }
                let tried = tries;
                tries += 1;
                if tried == TRIES_PER_PACE {
                    break;
                }
                let from = grid.position(candidate.node);
                if already_aimed(kept_on_target, from, landing, candidate.risk) {
                    break;
                }
                let mut power = candidate.power;
                let (mut flies_ok, mut lands, mut lip, mut room, mut absorbs) = flies(
                    candidate.node,
                    candidate.aim_at,
                    power,
                    run,
                    candidate.settles,
                );
                if !flies_ok && !candidate.land_power.is_nan() {
                    power = candidate.land_power;
                    (flies_ok, lands, lip, room, absorbs) =
                        flies(candidate.node, candidate.land_at, power, run, false);
                }
                if !flies_ok {
                    continue;
                }
                let risk = candidate.risk - candidate.lip_risk
                    + if lip < EASY_LIP_CLEARANCE { 1.0 } else { 0.0 }
                    + if room < easy_room { 1.0 } else { 0.0 }
                    + if absorbs { 0.0 } else { 1.0 };
                if taken.insert((candidate.node, lands, run)) {
                    found.push(
                        candidate.node,
                        NavLeap {
                            from: candidate.node,
                            to: lands,
                            power,
                            run,
                            risk,
                        },
                    );
                    kept_on_target.push((from, grid.position(lands), risk));
                }
                break;
            }
        }
    }

    /// Aims a jump from `from` at `speed` at `spot`: with `settles`, so the
    /// body comes to rest on the spot, its arc coming down short of it by all
    /// the body goes on after it lands; otherwise so the body comes down to
    /// stay on the spot, short of it by only what carries the body on off the
    /// ground, and slides on past it. Gives the point that arc is aimed to
    /// come down through, and the jump's power and launch speed; `None` when
    /// no jump comes down short enough.
    fn aim_short(
        &self,
        from: Vec3,
        spot: Vec3,
        on_terrain: bool,
        speed: f32,
        settles: bool,
    ) -> Option<(Vec3, f32, f32)> {
        let toward = flat(spot) - flat(from);
        let distance = toward.length();
        if distance < 1e-3 {
            return None;
        }
        let mut aimed = None;
        let mut short_by = self.short_by(speed, 0.0, settles);
        for _ in 0..AIM_PASSES {
            let flat_distance = distance - short_by;
            let point = (flat(spot) - toward * (short_by / distance)).extend(spot.z);
            if flat_distance < SHORTEST_LEAP {
                break;
            }
            let Some((charged, leaves)) = self.aim(from, point, speed) else {
                break;
            };
            aimed = Some((point, charged, leaves));
            let fall = if on_terrain {
                self.ability
                    .physics
                    .fall_speed(leaves, flat_distance / speed)
            } else {
                0.0
            };
            let goes_on = self.short_by(speed, fall, settles);
            if (goes_on - short_by).abs() <= SLIDE_TOLERANCE {
                break;
            }
            short_by = goes_on;
        }
        aimed
    }

    /// How far short of its spot a jump at `speed` that comes down falling
    /// at `fall` is aimed.
    fn short_by(&self, speed: f32, fall: f32, settles: bool) -> f32 {
        if settles {
            self.ability.physics.slide(speed, fall)
        } else {
            self.ability.physics.carry(speed, fall)
        }
    }

    /// The power and launch speed of the jump that, leaving `from` at
    /// `speed`, comes down through `to`. When even the lowest jump flies past
    /// it, that jump. `None` when no jump comes down through it.
    fn aim(&self, from: Vec3, to: Vec3, speed: f32) -> Option<(f32, f32)> {
        let distance = flat(from).distance(flat(to));
        if !(speed > 0.0) || distance < SHORTEST_LEAP {
            return None;
        }
        let time = distance / speed;
        let needed = self.ability.physics.launch_for(to.z - from.z, time);
        let lowest = launch(NavLeapAbility::LOWEST_JUMP);
        if needed > lowest.max(launch(self.ability.full_jump_height)) {
            return None;
        }
        if needed >= lowest && self.ability.physics.fall_speed(needed, time) <= 0.0 {
            return None;
        }
        let launch_speed = needed.max(lowest);
        let height = launch_speed * launch_speed / (2.0 * GRAVITY);
        let power = if self.ability.full_jump_height > NavLeapAbility::LOWEST_JUMP {
            (height / self.ability.full_jump_height).clamp(
                NavLeapAbility::LOWEST_JUMP / self.ability.full_jump_height,
                1.0,
            )
        } else {
            1.0
        };
        Some((power, launch_speed))
    }

    /// The nearest spot of a piece, measured flat, within the aim reach of a
    /// point and at a height a jump from it could reach the point from, or
    /// -1.
    fn nearest_spot(&self, piece: i32, holds: &[bool], point: Vec3) -> i32 {
        let surfaces = self.surfaces();
        let grid: &NavGrid = &self.grid;
        let side = surfaces.tile_side;
        let tile_size = TILE_COLUMNS as f32 * grid.cell_size();
        let centre_x = ((point.x - grid.origin_x()) / tile_size).floor() as i32;
        let centre_y = ((point.y - grid.origin_y()) / tile_size).floor() as i32;
        let reach = self.tiles_within(self.aim_reach);
        let mut nearest = -1;
        let mut best = self.aim_reach * self.aim_reach;
        for ring in 0..=reach {
            let ring_nearest = (ring - 1) as f32 * tile_size;
            if ring > 1 && ring_nearest * ring_nearest >= best {
                break;
            }
            for y in centre_y - ring..=centre_y + ring {
                if (y as u32) >= (side as u32) {
                    continue;
                }
                let edge_row = y == centre_y - ring || y == centre_y + ring;
                let step = if edge_row { 1 } else { 2 * ring.max(1) };
                let mut x = centre_x - ring;
                while x <= centre_x + ring {
                    let column = x;
                    x += step;
                    if (column as u32) >= (side as u32) {
                        continue;
                    }
                    let tile = (y * side + column) as usize;
                    if !holds[tile] {
                        continue;
                    }
                    for index in surfaces.tile_start[tile]..surfaces.tile_start[tile + 1] {
                        let spot = surfaces.spots[index as usize];
                        if surfaces.piece_of[spot as usize] != piece {
                            continue;
                        }
                        let at = grid.position(spot);
                        let rise = point.z - at.z;
                        if rise > self.ability.full_jump_height + SPOT_HEIGHT_SLACK
                            || rise < -(self.ability.maximum_drop + SPOT_HEIGHT_SLACK)
                        {
                            continue;
                        }
                        let squared = flat(at).distance_squared(flat(point));
                        if squared < best {
                            best = squared;
                            nearest = spot;
                        }
                    }
                }
            }
        }
        nearest
    }

    /// Follows a line flat from a spot, column by column, out to the aim
    /// reach: how far along it the spot's floor stops being walkable, how
    /// far along the piece's floor begins, and the piece's clear node in
    /// each column it crosses, with its distance from the spot.
    fn trace(&self, spot: i32, direction: Vec2, piece: i32, mut height: f32) -> Line {
        let grid: &NavGrid = &self.grid;
        let start = grid.position(spot);
        let (mut x, mut y) = grid.column_of(spot);
        let cell = grid.cell_size();
        let local_x = start.x - grid.origin_x();
        let local_y = start.y - grid.origin_y();
        let step_x = sign(direction.x);
        let step_y = sign(direction.y);
        let mut next_x = if step_x == 0 {
            f32::INFINITY
        } else {
            (if step_x > 0 {
                (x + 1) as f32 * cell - local_x
            } else {
                local_x - x as f32 * cell
            }) / direction.x.abs()
        };
        let mut next_y = if step_y == 0 {
            f32::INFINITY
        } else {
            (if step_y > 0 {
                (y + 1) as f32 * cell - local_y
            } else {
                local_y - y as f32 * cell
            }) / direction.y.abs()
        };
        let across_x = if step_x == 0 {
            f32::INFINITY
        } else {
            cell / direction.x.abs()
        };
        let across_y = if step_y == 0 {
            f32::INFINITY
        } else {
            cell / direction.y.abs()
        };

        let mut on_floor = spot;
        let mut face_at = f32::INFINITY;
        let mut edge_at = f32::INFINITY;
        let mut takeoffs = Vec::new();
        loop {
            let entered;
            let step;
            if next_x < next_y {
                x += step_x;
                entered = next_x;
                next_x += across_x;
                step = NavGrid::direction_of(step_x, 0);
            } else {
                y += step_y;
                entered = next_y;
                next_y += across_y;
                step = NavGrid::direction_of(0, step_y);
            }
            if entered > self.aim_reach
                || (x as u32) >= (grid.side() as u32)
                || (y as u32) >= (grid.side() as u32)
            {
                break;
            }
            if on_floor >= 0 {
                let next = grid.link(on_floor, step);
                if next < 0 || self.surfaces().piece_of[next as usize] == piece {
                    face_at = entered;
                    on_floor = -1;
                } else {
                    on_floor = next;
                }
            }
            let (first, count) = grid.nodes_in_column(x, y);
            let mut best = -1;
            for node in first..first + count {
                if self.surfaces().piece_of[node as usize] == piece
                    && (best < 0
                        || (grid.position(node).z - height).abs()
                            < (grid.position(best).z - height).abs())
                {
                    best = node;
                }
            }
            if best < 0 {
                continue;
            }
            let at = grid.position(best);
            edge_at = edge_at.min(entered);
            height = at.z;
            takeoffs.push((best, flat(start).distance(flat(at))));
        }
        Line {
            face_at,
            edge_at,
            takeoffs,
        }
    }

    /// Flies a jump from a node toward a point and returns the clear node of
    /// the target piece the body comes to rest on, or -1, with how far it
    /// cleared the lips it passed on the way and its room.
    fn fly_at(
        &self,
        from: i32,
        toward: Vec3,
        power: f32,
        run: bool,
        target: i32,
        settles: bool,
    ) -> (i32, f32, i32) {
        self.fly_at_point(
            from,
            self.grid.position(from),
            toward,
            power,
            run,
            target,
            settles,
        )
    }

    /// As [`Self::fly_at`], from a point on the node rather than the node's
    /// own.
    #[allow(clippy::too_many_arguments)]
    fn fly_at_point(
        &self,
        from: i32,
        start: Vec3,
        toward: Vec3,
        power: f32,
        run: bool,
        target: i32,
        settles: bool,
    ) -> (i32, f32, i32) {
        let line = flat(toward) - flat(start);
        let distance = line.length();
        if distance < 1e-3 {
            return (-1, f32::INFINITY, -1);
        }
        let heading = line / distance;
        let (touchdown, lip, fall) =
            self.fly(start, heading, power, run, distance + FLIGHT_OVERSHOOT);
        if touchdown < 0 || lip < LEAST_LIP_CLEARANCE {
            return (-1, lip, -1);
        }
        let (carry, slide) = self.goes_on(run, fall, touchdown);
        let (rest, room) = self.settle(
            touchdown,
            heading,
            carry,
            slide,
            self.speed_of(run),
            target,
            settles,
        );
        if rest >= 0 && self.lands(from, rest, start, target) {
            (rest, lip, room)
        } else {
            (-1, lip, room)
        }
    }

    /// The least room, in steps from the target piece's edge, a jump from a
    /// node toward a spot comes down with when it leaves a little off: turned
    /// a little either way, from a little short of the node or past it, or
    /// charged a little more or less, each followed through its landing
    /// slide. -1 as soon as one misses the piece, touches a lip on the way,
    /// slides off, or keeps less than `least`.
    #[allow(clippy::too_many_arguments)]
    fn worst_room(
        &self,
        from: i32,
        spot: Vec3,
        power: f32,
        run: bool,
        target: i32,
        least: i32,
        settles: bool,
    ) -> i32 {
        self.worst_room_from_point(
            self.grid.position(from),
            spot,
            power,
            run,
            target,
            least,
            settles,
        )
    }

    #[allow(clippy::too_many_arguments)]
    fn worst_room_from_point(
        &self,
        start: Vec3,
        spot: Vec3,
        power: f32,
        run: bool,
        target: i32,
        least: i32,
        settles: bool,
    ) -> i32 {
        let toward = flat(spot) - flat(start);
        let distance = toward.length();
        let heading = toward / distance;
        let longest = distance + FLIGHT_OVERSHOOT;
        let slack = (heading * TAKEOFF_SLACK).extend(0.0);
        let mut worst = i32::MAX;
        let turned_left = turned(heading, HEADING_SLACK_DEGREES);
        let turned_right = turned(heading, -HEADING_SLACK_DEGREES);
        let mut keeps = |flight: (i32, f32, f32), along: Vec2| {
            let (touchdown, lip, fall) = flight;
            let mut room = -1;
            if touchdown >= 0 && lip >= 0.0 {
                let (carry, slide) = self.goes_on(run, fall, touchdown);
                room = self
                    .settle(
                        touchdown,
                        along,
                        carry,
                        slide,
                        self.speed_of(run),
                        target,
                        settles,
                    )
                    .1;
            }
            worst = worst.min(room);
            worst >= least
        };
        let kept = keeps(
            self.fly(start, turned_left, power, run, longest),
            turned_left,
        ) && keeps(
            self.fly(start, turned_right, power, run, longest),
            turned_right,
        ) && keeps(
            self.fly(start - slack, heading, power, run, longest + TAKEOFF_SLACK),
            heading,
        ) && keeps(
            self.fly(start + slack, heading, power, run, longest),
            heading,
        ) && (power >= 1.0
            || keeps(
                self.fly(start, heading, (power + POWER_SLACK).min(1.0), run, longest),
                heading,
            ))
            && keeps(
                self.fly(start, heading, (power - POWER_SLACK).max(0.0), run, longest),
                heading,
            );
        if kept { worst } else { -1 }
    }

    /// About how far along the ground a jump's landing, `rise` above its
    /// takeoff, moves for a little more or less power: far for a jump that
    /// comes down near the top of its arc, little for one that comes down
    /// steeply.
    fn landing_shift(&self, launch_speed: f32, speed: f32, rise: f32) -> f32 {
        let fall = launch_speed * launch_speed - 2.0 * GRAVITY * rise;
        if !(fall > 0.0) {
            return f32::INFINITY;
        }
        let launch_per_power = GRAVITY
            * self
                .ability
                .full_jump_height
                .max(NavLeapAbility::LOWEST_JUMP)
            / launch_speed;
        let time_per_launch = (1.0 + launch_speed / fall.sqrt()) / GRAVITY;
        speed * time_per_launch * launch_per_power * POWER_SLACK
    }

    /// How far a body leaping at the pace goes on off the ground after its
    /// arc comes down falling at `fall`, and how far it then slides along
    /// the ground.
    fn goes_on(&self, run: bool, fall: f32, touchdown: i32) -> (f32, f32) {
        let speed = self.speed_of(run);
        // A body coming down on terrain is thrown back up a little and skims
        // on through the hop; one coming down on an object is not: live, over
        // a hundred landings on a jump puzzle's rocks slid on exactly as far
        // as the ground slide alone carries a body.
        let falling = if touchdown >= 0 && !self.grid.is_terrain(touchdown) {
            0.0
        } else {
            fall
        };
        (
            self.ability.physics.carry(speed, falling),
            self.ability.physics.ground_slide(speed),
        )
    }

    fn speed_of(&self, run: bool) -> f32 {
        if run {
            self.ability.run_speed
        } else {
            self.ability.walk_speed
        }
    }

    /// Follows a body on from the node it came down on along a heading, over
    /// floor linked step by step: off the ground for `carry`, stopping
    /// against a wall or a rise and going over a ledge, and then sliding
    /// along the ground for `slide`, held at a ledge as well as by a wall or
    /// a rise. Returns the node of the piece where it comes to rest (or -1)
    /// and its room in steps from the target piece's edge.
    #[allow(clippy::too_many_arguments)]
    fn settle(
        &self,
        touchdown: i32,
        mut heading: Vec2,
        mut carry: f32,
        slide: f32,
        speed: f32,
        target: i32,
        settles: bool,
    ) -> (i32, i32) {
        let grid: &NavGrid = &self.grid;
        let piece_of = &self.surfaces().piece_of;
        let mut room = self.room_on(touchdown, target);
        if room < 0 {
            return (-1, room);
        }
        let mut came_down = room;
        let mut goes_on = carry + slide;
        if !(goes_on > 0.0) {
            return (touchdown, room);
        }
        let mut at = touchdown;
        let mut standing = if piece_of[touchdown as usize] == target {
            touchdown
        } else {
            -1
        };
        let cell = grid.cell_size();
        // The square of the speed the body slides at, which a rise spends and
        // a drop gives back.
        let mut energy = speed * speed;
        let mut turned_count = 0;
        loop {
            let start = grid.position(at);
            let (x, y) = grid.column_of(at);
            let local_x = start.x - grid.origin_x();
            let local_y = start.y - grid.origin_y();
            let step_x = sign(heading.x);
            let step_y = sign(heading.y);
            let mut next_x = if step_x == 0 {
                f32::INFINITY
            } else {
                (if step_x > 0 {
                    (x + 1) as f32 * cell - local_x
                } else {
                    local_x - x as f32 * cell
                }) / heading.x.abs()
            };
            let mut next_y = if step_y == 0 {
                f32::INFINITY
            } else {
                (if step_y > 0 {
                    (y + 1) as f32 * cell - local_y
                } else {
                    local_y - y as f32 * cell
                }) / heading.y.abs()
            };
            let across_x = if step_x == 0 {
                f32::INFINITY
            } else {
                cell / heading.x.abs()
            };
            let across_y = if step_y == 0 {
                f32::INFINITY
            } else {
                cell / heading.y.abs()
            };
            let mut before = 0.0;
            loop {
                let entered;
                let direction;
                if next_x < next_y {
                    entered = next_x;
                    next_x += across_x;
                    direction = NavGrid::direction_of(step_x, 0);
                } else {
                    entered = next_y;
                    next_y += across_y;
                    direction = NavGrid::direction_of(0, step_y);
                }
                let aloft = entered <= carry;
                let next = if entered > goes_on {
                    -1
                } else {
                    grid.link(at, direction)
                };
                if entered > goes_on || (next < 0 && self.stops_against(at, direction)) {
                    if turned_count > 0 {
                        standing = self.beside(at, target, standing);
                    }
                    room = if standing < 0 {
                        -1
                    } else if settles {
                        if turned_count > 0 {
                            self.room_on(standing, target)
                        } else {
                            room
                        }
                    } else {
                        came_down
                    };
                    return (standing, room);
                }
                if next < 0 {
                    // Sliding along the ground into a ledge, a body is pushed
                    // along the edge rather than stopped, as the client's
                    // precipice slide carries it: what it goes on with is the
                    // part of its slide that lies along the edge.
                    if !aloft
                        && turned_count < MOST_EDGE_SLIDES
                        && let Some((edge, keeps)) = self.along_edge(at, heading)
                    {
                        goes_on = (goes_on - entered) * keeps;
                        carry = 0.0;
                        heading = edge;
                        break;
                    }
                    if turned_count > 0 && !aloft {
                        standing = self.beside(at, target, standing);
                    }
                    room = if aloft || standing < 0 { -1 } else { came_down };
                    return (if aloft { -1 } else { standing }, room);
                }
                let mut next_room = self.room_on(next, target);
                if next_room < 0 {
                    if aloft || piece_of[next as usize] >= 0 {
                        return (-1, -1);
                    }
                    next_room = 0;
                }
                if !aloft && energy > 0.0 {
                    // Sliding up a rise spends the body's speed and sliding
                    // down one gives it back, and a slide goes on as far as
                    // its speed carries it.
                    let across = (entered - before).max(0.0);
                    let rise = grid.position(next).z - grid.position(at).z;
                    let spent = energy - 2.0 * NavLeapPhysics::GRAVITY * rise;
                    let stops = if spent > 0.0 {
                        let mut left = (goes_on - entered) * (spent / energy).sqrt();
                        if rise > 0.0 {
                            left -= (across * across + rise * rise).sqrt() - across;
                        }
                        energy = spent;
                        goes_on = entered + left;
                        !(left > 0.0)
                    } else {
                        true
                    };
                    if stops {
                        if turned_count > 0 {
                            standing = self.beside(at, target, standing);
                        }
                        room = if standing < 0 {
                            -1
                        } else if settles {
                            room
                        } else {
                            came_down
                        };
                        return (standing, room);
                    }
                }
                before = entered;
                room = next_room;
                if aloft {
                    came_down = next_room;
                }
                at = next;
                if piece_of[at as usize] == target {
                    standing = at;
                }
            }
            // The walk above ends only by returning or by turning along an
            // edge, so here the body has turned and slides on while it has
            // any way left to go.
            if !(goes_on > 0.0) {
                standing = self.beside(at, target, standing);
                room = if standing < 0 {
                    -1
                } else if settles {
                    self.room_on(standing, target)
                } else {
                    came_down
                };
                return (standing, room);
            }
            turned_count += 1;
        }
    }

    /// Where a body sliding along the ground from a node, on its own piece
    /// of floor, comes to rest, as a leap's landing slide is followed; -1
    /// when it leaves the piece.
    #[must_use]
    pub fn slides_to(&self, node: i32, heading: Vec2, slide: f32) -> i32 {
        self.settle(
            node,
            heading.normalize(),
            0.0,
            slide,
            self.ability.run_speed,
            self.surfaces().piece_of[node as usize],
            true,
        )
        .0
    }

    /// The node of a piece a body at a node stands on: the node itself on
    /// the piece, else a neighbour on it, as a body pushed along a piece's
    /// edge stands beside it; else `otherwise`.
    fn beside(&self, node: i32, target: i32, otherwise: i32) -> i32 {
        if self.surfaces().piece_of[node as usize] == target {
            return node;
        }
        for direction in 0..DIRECTION_COUNT {
            let next = self.grid.link(node, direction);
            if next >= 0 && self.surfaces().piece_of[next as usize] == target {
                return next;
            }
        }
        otherwise
    }

    /// Flies a leap from a point toward another at a power and pace, as a
    /// route's leaps are flown, and gives where its arc comes down and where
    /// the body then comes to rest on the floor it came down on, for
    /// narration; `None` where it comes down nowhere.
    #[must_use]
    pub fn follow(&self, start: Vec3, toward: Vec3, power: f32, run: bool) -> Option<(Vec3, Vec3)> {
        let line = flat(toward) - flat(start);
        let distance = line.length();
        if distance < 1e-3 {
            return None;
        }
        let heading = line / distance;
        let (touchdown, _, fall) =
            self.fly(start, heading, power, run, distance + FLIGHT_OVERSHOOT);
        if touchdown < 0 {
            return None;
        }
        let came_down = self.grid.position(touchdown);
        let (carry, slide) = self.goes_on(run, fall, touchdown);
        let piece = self.surfaces().piece_of[touchdown as usize];
        let rest = if piece < 0 {
            -1
        } else {
            self.settle(
                touchdown,
                heading,
                carry,
                slide,
                self.speed_of(run),
                piece,
                true,
            )
            .0
        };
        let rests = if rest >= 0 {
            self.grid.position(rest)
        } else {
            came_down
        };
        Some((came_down, rests))
    }

    /// The way along a ledge a body sliding into it from a node goes on: the
    /// direction to a neighbouring node nearest its heading, and the share
    /// of its slide that lies that way. `None` when it meets the ledge head
    /// on, with no way on within 80 degrees of its heading.
    fn along_edge(&self, node: i32, heading: Vec2) -> Option<(Vec2, f32)> {
        let mut edge = Vec2::ZERO;
        let mut keeps = 0.0;
        for direction in 0..DIRECTION_COUNT {
            if self.grid.link(node, direction) < 0 {
                continue;
            }
            let (step_x, step_y) = NavGrid::step_of(direction);
            let way = Vec2::new(step_x as f32, step_y as f32).normalize();
            let share = way.dot(heading);
            if share > keeps {
                keeps = share;
                edge = way;
            }
        }
        (keeps > LEAST_EDGE_SLIDE_SHARE).then_some((edge, keeps))
    }

    /// Whether a body sliding from a node toward the neighbouring column is
    /// stopped there by a wall or a rise, rather than going over a ledge.
    fn stops_against(&self, node: i32, direction: i32) -> bool {
        let grid: &NavGrid = &self.grid;
        let here = grid.position(node);
        let (x, y) = grid.column_of(node);
        let (step_x, step_y) = NavGrid::step_of(direction);
        let (first, count) = grid.nodes_in_column(x + step_x, y + step_y);
        for other in first..first + count {
            let rise = grid.position(other).z - here.z;
            if rise > grid.body().step_up_height && rise < grid.body().height {
                return true;
            }
        }
        let beside = Vec3::new(
            here.x + step_x as f32 * grid.cell_size(),
            here.y + step_y as f32 * grid.cell_size(),
            here.z,
        );
        !grid.is_open_line(here, beside)
    }

    /// How far in from a piece's edge, in steps, a landing node lies: zero
    /// for an edge node beside the piece, -1 off it.
    fn room_on(&self, node: i32, target: i32) -> i32 {
        if node < 0 {
            return -1;
        }
        if self.surfaces().piece_of[node as usize] == target {
            return self.grid.border_distance(node);
        }
        if self.on_piece(node, target) { 0 } else { -1 }
    }

    /// Flies a standing long jump from a point along a heading and returns
    /// the node it comes down on (or -1 when the arc strikes something, falls
    /// deeper than a route may drop, or lands nowhere within `longest`,
    /// measured flat), how far the body's whole width cleared the lips of
    /// higher floor it passed before coming down (infinity for none), and
    /// how fast it was falling as its arc met the floor it comes down on. A
    /// body coming down onto a slope meets it with its side before its feet
    /// reach the floor under them, so floor within the body's radius below
    /// its feet counts as floor it comes down on.
    fn fly(
        &self,
        start: Vec3,
        heading: Vec2,
        power: f32,
        run: bool,
        longest: f32,
    ) -> (i32, f32, f32) {
        let grid: &NavGrid = &self.grid;
        let mut lip = f32::INFINITY;
        let speed = self.speed_of(run);
        if !(speed > 0.0) {
            return (-1, lip, 0.0);
        }
        let launch_speed = launch(self.ability.jump_height(power));
        let mut previous = start.z;
        let mut flown = SAMPLE_STEP;
        while flown <= longest {
            let time = flown / speed;
            let height = start.z + self.ability.physics.height(launch_speed, time);
            let feet = Vec3::new(
                start.x + heading.x * flown,
                start.y + heading.y * flown,
                height,
            );
            if !grid.contains(feet, 0.0) || height < start.z - self.ability.maximum_drop {
                return (-1, lip, 0.0);
            }
            if height < previous {
                let landing = grid.landing_under(feet, previous);
                if landing >= 0 {
                    let between = ((previous - grid.position(landing).z) / (previous - height))
                        .clamp(0.0, 1.0);
                    let fall = self
                        .ability
                        .physics
                        .fall_speed(launch_speed, time - (1.0 - between) * SAMPLE_STEP / speed);
                    return (landing, lip, fall);
                }
            }
            if !grid.is_open_air(feet) {
                let touching = if height < previous {
                    grid.landing_under(feet - Vec3::new(0.0, 0.0, grid.body().radius), previous)
                } else {
                    -1
                };
                let fall = if touching >= 0 {
                    self.ability.physics.fall_speed(launch_speed, time)
                } else {
                    0.0
                };
                return (touching, lip, fall);
            }
            lip = lip.min(self.lip_ahead(feet, heading));
            previous = height;
            flown += SAMPLE_STEP;
        }
        (-1, lip, 0.0)
    }

    /// How far above floor ahead of it a body in the air clears, where that
    /// floor stands more than a step higher than the floor under the body's
    /// centre: the lip of a higher floor it is passing over, measured across
    /// the body's whole width. Infinity when there is none, and below zero
    /// when such floor reaches above the body's feet.
    fn lip_ahead(&self, feet: Vec3, heading: Vec2) -> f32 {
        let grid: &NavGrid = &self.grid;
        let cell = grid.cell_size();
        let radius = grid.body().radius;
        let centre_x = ((feet.x - grid.origin_x()) / cell).floor() as i32;
        let centre_y = ((feet.y - grid.origin_y()) / cell).floor() as i32;
        let under = self.floor_below(centre_x, centre_y, feet.z) + grid.body().step_up_height;
        let top = feet.z + grid.body().height;
        let reach = (radius / cell).ceil() as i32;
        let mut lip = f32::INFINITY;
        for y in centre_y - reach..=centre_y + reach {
            for x in centre_x - reach..=centre_x + reach {
                let dx = grid.origin_x() + (x as f32 + 0.5) * cell - feet.x;
                let dy = grid.origin_y() + (y as f32 + 0.5) * cell - feet.y;
                if dx * dx + dy * dy > radius * radius || dx * heading.x + dy * heading.y <= 0.0 {
                    continue;
                }
                let (first, count) = grid.nodes_in_column(x, y);
                for node in first..first + count {
                    let floor = grid.position(node).z;
                    if floor > under && floor < top {
                        lip = lip.min(feet.z - floor);
                    }
                }
            }
        }
        lip
    }

    /// The top of the highest floor in a column at or below a height, or
    /// negative infinity.
    fn floor_below(&self, x: i32, y: i32, height: f32) -> f32 {
        let (first, count) = self.grid.nodes_in_column(x, y);
        let mut highest = f32::NEG_INFINITY;
        for node in first..first + count {
            let floor = self.grid.position(node).z;
            if floor <= height && floor > highest {
                highest = floor;
            }
        }
        highest
    }

    /// Whether a landing is one to keep: on the target piece, beyond a
    /// step's reach, and not ground the body could walk to.
    fn lands(&self, from: i32, landing: i32, start: Vec3, target: i32) -> bool {
        let there = self.grid.position(landing);
        landing != from
            && self.surfaces().piece_of[landing as usize] == target
            && flat(start).distance(flat(there)) >= SHORTEST_LEAP
            && self.grid.nodes_along(from, landing).is_empty()
    }

    /// Whether a node stands on a piece of floor: one of its clear nodes, or
    /// an edge node beside one.
    fn on_piece(&self, node: i32, target: i32) -> bool {
        if node < 0 {
            return false;
        }
        if self.surfaces().piece_of[node as usize] == target {
            return true;
        }
        for direction in 0..DIRECTION_COUNT {
            let next = self.grid.link(node, direction);
            if next >= 0 && self.surfaces().piece_of[next as usize] == target {
                return true;
            }
        }
        false
    }

    fn tiles_within(&self, meters: f32) -> i32 {
        (meters / (TILE_COLUMNS as f32 * self.grid.cell_size())).ceil() as i32
    }

    /// The bounds the finder's own limits set on the leaps it finds for a
    /// piece.
    fn limit_bounds(&self) -> Bounds {
        self.limits.map_or_else(Bounds::default, |limits| Bounds {
            takeoff: Some((flat(limits.takeoff_near), limits.takeoff_radius)),
            landing: Some((flat(limits.toward), limits.landing_within)),
            onto: None,
        })
    }

    /// Whether a leap may take off in a tile: any part of it lies within
    /// the bounds' takeoff radius, or they set none.
    fn takes_off_in(&self, tile: usize, bounds: &Bounds) -> bool {
        let Some((centre, radius)) = bounds.takeoff else {
            return true;
        };
        let side = self.surfaces().tile_side;
        let width = TILE_COLUMNS as f32 * self.grid.cell_size();
        let low = Vec2::new(
            self.grid.origin_x() + (tile as i32 % side) as f32 * width,
            self.grid.origin_y() + (tile as i32 / side) as f32 * width,
        );
        let nearest = centre.clamp(low, low + Vec2::splat(width));
        nearest.distance(centre) <= radius
    }

    /// Whether a leap may come down on a spot: on the bounds' piece, if
    /// they name one, and within their landing reach, if they set one.
    fn lands_within(&self, spot: i32, bounds: &Bounds) -> bool {
        bounds
            .onto
            .is_none_or(|piece| self.surfaces().piece_of[spot as usize] == piece)
            && bounds.landing.is_none_or(|(centre, radius)| {
                flat(self.grid.position(spot)).distance(centre) < radius
            })
    }
}

/// The leaps aimed onward from a takeoff node onto a target piece, by the
/// two.
type OnwardLeaps = HashMap<(i32, i32), Arc<[NavLeap]>>;

/// Where the leaps found for one piece of floor may take off (within a
/// radius of a point, across), come down (likewise), and onto which piece:
/// anywhere, onto any, where unset.
#[derive(Debug, Clone, Copy, Default)]
struct Bounds {
    takeoff: Option<(Vec2, f32)>,
    landing: Option<(Vec2, f32)>,
    onto: Option<i32>,
}

/// The leaps found so far, by takeoff, in the order takeoffs were first
/// found.
#[derive(Default)]
struct FoundLeaps {
    order: Vec<(i32, Vec<NavLeap>)>,
    index: HashMap<i32, usize>,
}

impl FoundLeaps {
    fn push(&mut self, from: i32, leap: NavLeap) {
        let slot = *self.index.entry(from).or_insert_with(|| {
            self.order.push((from, Vec::new()));
            self.order.len() - 1
        });
        self.order[slot].1.push(leap);
    }
}

/// Every leap of a piece with no room to walk on, taken from where a body
/// stands on it.
fn standing_leaps(by_takeoff: &ByTakeoff, node: i32) -> Arc<[NavLeap]> {
    let mut standing = Vec::new();
    for (_, leaps) in &by_takeoff.order {
        for leap in leaps.iter() {
            if leap.to != node {
                standing.push(NavLeap {
                    from: node,
                    ..*leap
                });
            }
        }
    }
    Arc::from(standing)
}

/// Whether a leap no riskier than `risk` is already kept from near a
/// takeoff to near a spot.
fn already_aimed(kept: &[(Vec3, Vec3, f32)], from: Vec3, spot: Vec3, risk: f32) -> bool {
    kept.iter().any(|&(kept_from, kept_to, kept_risk)| {
        kept_risk <= risk
            && flat(kept_to).distance(flat(spot)) <= SAME_LANDING_METERS
            && flat(kept_from).distance(flat(from)) <= SAME_TAKEOFF_METERS
    })
}

/// A grid's pieces of floor, the clear nodes walks join in both directions,
/// and the spots near each piece's edges that leaps aim at, sorted into
/// square tiles.
#[derive(Debug)]
pub(crate) struct Surfaces {
    /// The piece of floor each node belongs to, or -1 for a node that is not
    /// clear and not a perch.
    piece_of: Vec<i32>,
    /// How many nodes each piece holds.
    sizes: Vec<i32>,
    /// How many steps from its edge each piece's deepest node lies.
    deepest: Vec<i32>,
    tile_side: i32,
    /// Where each tile's spots begin in `spots`, with the count of all spots
    /// at the end.
    tile_start: Vec<i32>,
    spots: Vec<i32>,
    /// How many bands out from its piece's middle each spot lies: 0 in the
    /// middle, 1 and 2 nearer the edge.
    spot_tiers: Vec<u8>,
}

impl Surfaces {
    pub(crate) fn new(grid: &NavGrid) -> Self {
        let count = grid.node_count();
        let mut parent: Vec<i32> = (0..count).collect();
        for node in 0..count {
            if !grid.is_clear(node) {
                continue;
            }
            for direction in 0..DIRECTION_COUNT {
                let next = grid.link(node, direction);
                if next <= node || !grid.is_clear(next) {
                    continue;
                }
                let (step_x, step_y) = NavGrid::step_of(direction);
                if grid.link(next, NavGrid::direction_of(-step_x, -step_y)) == node {
                    let a = root(&mut parent, next);
                    let b = root(&mut parent, node);
                    parent[a as usize] = b;
                }
            }
        }

        // A perch is floor a body stands on with no room to walk clear of its
        // edges, such as the top of a post, and it touches no clear floor.
        // The band along the edge of ordinary floor touches clear floor, so
        // it stays no piece of its own.
        let mut perch = vec![false; count as usize];
        for node in 0..count {
            if grid.is_clear(node) || !grid.is_standable(node) {
                continue;
            }
            let mut beside_clear = false;
            let mut direction = 0;
            while direction < DIRECTION_COUNT && !beside_clear {
                let next = grid.link(node, direction);
                beside_clear = next >= 0 && grid.is_clear(next);
                direction += 1;
            }
            perch[node as usize] = !beside_clear;
        }
        for node in 0..count {
            if !perch[node as usize] {
                continue;
            }
            for direction in 0..DIRECTION_COUNT {
                let next = grid.link(node, direction);
                if next <= node || !perch[next as usize] {
                    continue;
                }
                let (step_x, step_y) = NavGrid::step_of(direction);
                if grid.link(next, NavGrid::direction_of(-step_x, -step_y)) == node {
                    let a = root(&mut parent, next);
                    let b = root(&mut parent, node);
                    parent[a as usize] = b;
                }
            }
        }

        let mut piece_of = vec![0; count as usize];
        let mut piece_of_root = vec![-1; count as usize];
        let mut sizes: Vec<i32> = Vec::new();
        let mut deepest: Vec<i32> = Vec::new();
        for node in 0..count {
            if !grid.is_clear(node) && !perch[node as usize] {
                piece_of[node as usize] = -1;
                continue;
            }
            let node_root = root(&mut parent, node);
            if piece_of_root[node_root as usize] < 0 {
                piece_of_root[node_root as usize] = sizes.len() as i32;
                sizes.push(0);
                deepest.push(0);
            }
            let piece = piece_of_root[node_root as usize];
            piece_of[node as usize] = piece;
            sizes[piece as usize] += 1;
            deepest[piece as usize] = deepest[piece as usize].max(grid.border_distance(node));
        }

        let tile_side = (grid.side() + TILE_COLUMNS - 1) / TILE_COLUMNS;
        let mut surfaces = Self {
            piece_of,
            sizes,
            deepest,
            tile_side,
            tile_start: vec![0; (tile_side * tile_side + 1) as usize],
            spots: Vec::new(),
            spot_tiers: Vec::new(),
        };
        for node in 0..count {
            if surfaces.tier_of(grid, node) >= 0 {
                surfaces.tile_start[tile_of(grid, node) + 1] += 1;
            }
        }
        for tile in 0..(tile_side * tile_side) as usize {
            surfaces.tile_start[tile + 1] += surfaces.tile_start[tile];
        }
        let total = *surfaces.tile_start.last().expect("one past the tiles") as usize;
        surfaces.spots = vec![0; total];
        surfaces.spot_tiers = vec![0; total];
        let mut filled: Vec<i32> = surfaces.tile_start[..surfaces.tile_start.len() - 1].to_vec();
        for node in 0..count {
            let tier = surfaces.tier_of(grid, node);
            if tier < 0 {
                continue;
            }
            let tile = tile_of(grid, node);
            let index = filled[tile] as usize;
            filled[tile] += 1;
            surfaces.spots[index] = node;
            surfaces.spot_tiers[index] = tier as u8;
        }
        surfaces
    }

    /// The piece of floor each node belongs to: clear floor a body walks
    /// across belongs to one piece, a perch too small to walk on is a piece
    /// of its own, and a node that is neither is -1.
    pub(crate) fn piece_of(&self) -> &[i32] {
        &self.piece_of
    }

    fn tile_holds(&self, tile: usize, piece: i32) -> bool {
        (self.tile_start[tile]..self.tile_start[tile + 1])
            .any(|index| self.piece_of[self.spots[index as usize] as usize] == piece)
    }

    /// How many bands out from its piece's middle a node lies as a spot, or
    /// -1 for a node that is not one: 0 at the middle, 12 deep on a deeper
    /// piece; 1 at 4 deep; 2 at 2 deep.
    fn tier_of(&self, grid: &NavGrid, node: i32) -> i32 {
        let piece = self.piece_of[node as usize];
        if piece < 0 {
            return -1;
        }
        let deepest = self.deepest[piece as usize];
        let room = grid.border_distance(node);
        let middle = deepest.min(CENTRE_ROOM_STEPS);
        let tier = if room >= middle - 1 && room <= middle + 2 {
            0
        } else if deepest >= INSIDE_ROOM_STEPS + 5
            && (INSIDE_ROOM_STEPS..=INSIDE_ROOM_STEPS + 3).contains(&room)
        {
            1
        } else if deepest >= NEAR_ROOM_STEPS + 3
            && (NEAR_ROOM_STEPS..=NEAR_ROOM_STEPS + 1).contains(&room)
        {
            2
        } else {
            return -1;
        };
        let columns = if deepest <= INSIDE_ROOM_STEPS {
            1
        } else if self.sizes[piece as usize] <= SMALL_PIECE_NODES {
            SMALL_PIECE_SPOT_COLUMNS
        } else {
            LARGE_PIECE_SPOT_COLUMNS
        };
        let (x, y) = grid.column_of(node);
        if x % columns == 0 && y % columns == 0 {
            tier
        } else {
            -1
        }
    }
}

fn tile_of(grid: &NavGrid, node: i32) -> usize {
    let (x, y) = grid.column_of(node);
    ((y / TILE_COLUMNS) * ((grid.side() + TILE_COLUMNS - 1) / TILE_COLUMNS) + x / TILE_COLUMNS)
        as usize
}

fn root(parent: &mut [i32], mut node: i32) -> i32 {
    while parent[node as usize] != node {
        parent[node as usize] = parent[parent[node as usize] as usize];
        node = parent[node as usize];
    }
    node
}
