//! Walks: plans routes to objects, places, the tops of objects and players
//! over grids built from the physics world, and walks the local player along
//! them with scripted moves, one quantum at a time.
//!
//! A grid covers a square region around the character and its goal and is
//! built off the update thread (or inline, as the host chooses). Inside a
//! sealed dungeon a grid covers every cell of the dungeon and no terrain. A
//! goal too far for one region is walked to in stages. A walk that stops
//! making progress tries a different way off what it met at each stop, then
//! plans again, a few times before it gives up and names what stood beside
//! that spot. A closed door on the way is opened (appraised first when the
//! client never has), a locked one is walked around. Creatures and players
//! are passed around where there is room and through where going around
//! would scrape walls. A walk waits while something else needs the
//! character. A follow keeps with a player until stopped, through portals.
//!
//! The host owns the physics world, the body, the objects and the doors; it
//! hands them to [`WalkController::tick`] each quantum as a [`WalkWorld`].

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;

use ac_physics::PhysicsEngine;
use glam::{Vec2, Vec3};

use crate::driver::{
    ARRIVAL_RADIUS, LeapApproach, RouteDriveHooks, RouteDriveSample, RouteDriveState,
    RouteDriveStep, RouteDriver, RouteLeap, RouteLeapAim, RouteTurning,
};
use crate::geometry::{self, LANDBLOCK_SIZE, NavBody, NavGeometry, NavSurfaces};
use crate::grid::{DEFAULT_CELL_SIZE, NavGrid, TARGET_HEIGHT};
use crate::leaps::{NavLeapAbility, NavLeapFinder, NavLeapPhysics};
use crate::motor::{
    MoveChannel, MoveDirection, MovePace, MoveRequest, ScriptedMoveSnapshot, signed_degrees,
};
use crate::order::{at_most_one_decimal, fixed, signed_two_decimals};
use crate::route::{
    self, GOAL_HEIGHT_TOLERANCE, NavAvoidance, NavRoute, NavRouteOutcome, NavRouteRequest,
    START_HEIGHT_TOLERANCE, START_RADIUS,
};

mod preview;
mod reach;
pub use preview::{NavPlan, PathPreview, PlanPoint, PlanStatus, PreviewTarget};
pub use reach::{CellReach, NavReach, ReachSearch, cells_reached};

/// Where a walk or route request stands.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum WalkState {
    /// No request yet.
    #[default]
    None,
    /// Building a grid, or searching one for a route.
    Planning,
    /// Following the route.
    Walking,
    /// Stopped where the character stands while something else needs the
    /// character; plans on once nothing has for a moment.
    Waiting,
    /// A route was asked for without walking it, and found.
    Planned,
    /// Within reach with a line of sight.
    Arrived,
    /// No route joins the character to the goal.
    NoRoute,
    /// The character stopped making progress, even after planning again.
    Blocked,
    /// A stop, or a newer request, ended it.
    Stopped,
    /// The player moved the character.
    Interrupted,
    /// The character entered portal space or left the world.
    Lost,
    /// The walk ended at the nearest spot it could reach, but no spot it
    /// could reach can see the goal.
    ArrivedWithoutSight,
}

/// Where the most recent walk or route request stands. `sequence` grows by
/// one for every request. `remaining_meters` is the route left while the
/// walk goes on; once it has ended, the straight-line distance from the
/// character to the goal then, or NaN when unknown. On a walk that ended
/// blocked, `blocked_by_object_id` names the object beside the spot where it
/// last stopped making progress.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct WalkReport {
    /// The request's sequence.
    pub sequence: i64,
    /// How it stands.
    pub state: WalkState,
    /// The object it goes to, or 0.
    pub object_id: u32,
    /// The route left, metres.
    pub remaining_meters: f32,
    /// How often it planned again.
    pub replans: i32,
    /// Why, in words.
    pub reason: String,
    /// What blocked it.
    pub blocked_by_object_id: u32,
}

/// The local player's body on one quantum, as a walk sees it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WalkBodySample {
    /// Where it stands, world frame.
    pub position: Vec3,
    /// Its compass heading, degrees.
    pub heading_degrees: f32,
    /// The shape grids are built for.
    pub body: NavBody,
    /// The scripted moves under way.
    pub moves: ScriptedMoveSnapshot,
    /// Whether the character is in portal space.
    pub in_portal_space: bool,
    /// The cell it stands in, or 0.
    pub cell_id: u32,
    /// What it can leap, when known.
    pub leaps: Option<NavLeapAbility>,
    /// Whether it is off the ground.
    pub airborne: bool,
    /// How fast it goes and turns, when known.
    pub turning: Option<RouteTurning>,
    /// How far a walk carries it on as the walk ends, or 0.
    pub walk_stop_meters: f32,
    /// How far a run carries it on as the run ends, or 0.
    pub run_stop_meters: f32,
    /// How many quanta of the player's own movement input the character has
    /// seen; a walk ends once it grows.
    pub player_movement_input_frames: i64,
}

/// A server object standing where a walk stopped making progress.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct NavBlocker {
    /// The object.
    pub object_id: u32,
    /// Its name.
    pub name: String,
    /// Whether it is a closed door.
    pub is_closed_door: bool,
    /// Where it stands.
    pub position: Vec3,
    /// The radius it fills.
    pub radius: f32,
    /// Whether it moves and moves aside when bumped.
    pub moves: bool,
    /// Whether it is hostile, which stays to fight.
    pub hostile: bool,
}

/// A door a walk can open.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct NavDoor {
    /// The door.
    pub object_id: u32,
    /// Its name.
    pub name: String,
    /// Where it stands, when known.
    pub position: Vec3,
    /// The radius it fills, when known (else 0).
    pub radius: f32,
}

/// The doors a walk meets, as the client sees and opens them.
pub trait NavDoors {
    /// The closed door nearest `from` whose position lies ahead of it,
    /// within `corridor` of the straight line from it to `to`.
    fn try_find_closed_door(&self, from: Vec3, to: Vec3, corridor: f32) -> Option<NavDoor>;
    /// Whether a door stands open.
    fn is_open(&self, door_id: u32) -> bool;
    /// Asks the client to use a door, walking into its use range first as a
    /// player's click would.
    fn use_door(&mut self, door_id: u32);
    /// How near a door's middle a character must stand for the client to use
    /// it where it stands; zero when the client does not say.
    fn use_reach(&self, _door_id: u32) -> f32 {
        0.0
    }
    /// Whether the client's appraisal says a door is locked; `None` when it
    /// has not appraised it.
    fn is_locked(&self, _door_id: u32) -> Option<bool> {
        None
    }
    /// Asks the server to appraise a door; false when the client would not
    /// ask.
    fn appraise(&mut self, _door_id: u32) -> bool {
        false
    }
}

/// The local player's body, as a walk samples and moves it.
pub trait WalkBody {
    /// `None` while there is no local player in the world.
    fn try_sample(&self) -> Option<WalkBodySample>;
    /// Begins a scripted move.
    fn begin_move(&mut self, request: MoveRequest) -> bool;
    /// Stops the scripted move on a channel.
    fn stop_move(&mut self, channel: MoveChannel) -> bool;
    /// Charges a jump for `power` of a full charge, then releases it.
    fn begin_jump(&mut self, _power: f32, _leave_at: Option<MovePace>) -> bool {
        false
    }
}

/// Where objects stand in the physics world, and what stands in a walk's
/// way.
pub trait NavGoalSource {
    /// Where an object stands.
    fn try_locate(&self, object_id: u32) -> Option<Vec3>;
    /// The server object whose edge comes nearest `position` within
    /// `radius`.
    fn try_find_blocker(&self, _position: Vec3, _radius: f32) -> Option<NavBlocker> {
        None
    }
    /// The objects the server placed within `radius` of `around` that
    /// collide and stand still, as points and radii; doors, corpses,
    /// players, moving creatures, the goal object and the objects in
    /// `in_grid` left out.
    fn find_obstacles(
        &self,
        _around: Vec3,
        _radius: f32,
        _goal_object_id: u32,
        _in_grid: Option<&HashSet<u32>>,
    ) -> Vec<NavAvoidance> {
        Vec::new()
    }
    /// Whether an object the server placed stands where it is and a body
    /// meets it, so a grid takes its collision as floors and walls.
    fn stands_still(&self, _entity_id: u32) -> bool {
        false
    }
    /// The players, and creatures that move, within `radius` of `around`.
    fn find_crowd(&self, _around: Vec3, _radius: f32, _goal_object_id: u32) -> Vec<NavAvoidance> {
        Vec::new()
    }
    /// The portals within `radius` of `around`, other than the goal object
    /// and a portal standing at `goal`.
    fn find_portals(
        &self,
        _around: Vec3,
        _radius: f32,
        _goal_object_id: u32,
        _goal: Vec3,
    ) -> Vec<NavAvoidance> {
        Vec::new()
    }
    /// Where a place stands in the physics world: a point in the
    /// landblock-local frame of `cell_id`, or for a cell of zero a point
    /// measured from the corner of the first landblock.
    fn try_locate_place(&self, _cell_id: u32, _local: Vec3) -> Option<Vec3> {
        None
    }
    /// The collision of an object, for a walk that ends standing on it.
    fn try_get_surfaces(&self, _object_id: u32) -> Option<NavSurfaces> {
        None
    }
    /// Whether an object is a player, the only kind a walk follows.
    fn is_player(&self, _object_id: u32) -> bool {
        false
    }
    /// The portal nearest `near` within `radius`.
    fn try_find_portal(&self, _near: Vec3, _radius: f32) -> Option<(u32, Vec3)> {
        None
    }
    /// A physics-world point measured from the corner of the first
    /// landblock (a path preview's points are reported so).
    fn try_global_of(&self, _world: Vec3) -> Option<Vec3> {
        None
    }
}

/// What a walk reads and moves each quantum, lent by the host.
pub struct WalkWorld<'a> {
    /// The physics world grids are captured from.
    pub physics: &'a PhysicsEngine,
    /// The local player's body.
    pub body: &'a mut dyn WalkBody,
    /// Objects and what stands in the way.
    pub goals: &'a dyn NavGoalSource,
    /// Doors, when the host can open them.
    pub doors: Option<&'a mut dyn NavDoors>,
    /// What needs the character now, such as a plugin fighting a monster;
    /// a walk waits while it names something.
    pub paused_by: Option<&'a dyn Fn() -> Option<String>>,
    /// Whether a cell lies in a sealed dungeon.
    pub is_sealed_dungeon: Option<&'a dyn Fn(u32) -> bool>,
}

impl std::fmt::Debug for WalkWorld<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WalkWorld")
            .field("doors", &self.doors.is_some())
            .field("paused_by", &self.paused_by.is_some())
            .field("is_sealed_dungeon", &self.is_sealed_dungeon.is_some())
            .finish_non_exhaustive()
    }
}

/// How grids are built and routes searched.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PlanMode {
    /// On a worker thread, taken up on a later quantum.
    #[default]
    Threads,
    /// On the calling thread, at once, taken up on the next quantum.
    Inline,
}

/// A grid build or route search under way.
enum Job<T> {
    Thread(JoinHandle<T>),
    Ready(T),
}

impl<T: Send + 'static> Job<T> {
    fn start(mode: PlanMode, work: impl FnOnce() -> T + Send + 'static) -> Self {
        match mode {
            PlanMode::Threads => Self::Thread(std::thread::spawn(work)),
            PlanMode::Inline => Self::Ready(work()),
        }
    }

    fn is_finished(&self) -> bool {
        match self {
            Self::Thread(handle) => handle.is_finished(),
            Self::Ready(_) => true,
        }
    }

    fn take(self) -> Result<T, String> {
        match self {
            Self::Thread(handle) => handle.join().map_err(|_| "the work panicked".to_owned()),
            Self::Ready(value) => Ok(value),
        }
    }
}

/// What a grid build ends with: the grid, or why there is none.
type BuiltGrid = Result<Arc<NavGrid>, String>;

/// A grid build that more than one search can wait on: a walk takes it up
/// on the session thread once it has finished, while a preview or a reach
/// search waits for it on its own worker.
#[derive(Clone)]
struct SharedBuild(Arc<(Mutex<Option<BuiltGrid>>, std::sync::Condvar)>);

impl SharedBuild {
    fn start(
        mode: PlanMode,
        work: impl FnOnce() -> Result<NavGrid, String> + Send + 'static,
    ) -> Self {
        let build = Self(Arc::new((Mutex::new(None), std::sync::Condvar::new())));
        let done = build.clone();
        let run = move || {
            let built = std::panic::catch_unwind(std::panic::AssertUnwindSafe(work))
                .unwrap_or_else(|_| Err("the work panicked".to_owned()))
                .map(Arc::new);
            let (slot, ready) = &*done.0;
            *slot
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner) = Some(built);
            ready.notify_all();
        };
        match mode {
            PlanMode::Threads => {
                std::thread::spawn(run);
            }
            PlanMode::Inline => run(),
        }
        build
    }

    fn is_finished(&self) -> bool {
        self.0
            .0
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .is_some()
    }

    /// The grid, waiting for the build when it is still running.
    fn wait(&self) -> BuiltGrid {
        let (slot, ready) = &*self.0;
        let mut built = slot
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        loop {
            if let Some(built) = built.as_ref() {
                return built.clone();
            }
            built = ready
                .wait(built)
                .unwrap_or_else(std::sync::PoisonError::into_inner);
        }
    }
}

/// A loaded landblock and where: its id and the bits of its world offset.
type PlacedLandblock = (u32, u32, u32);

/// The grid build under way, and what it is being built over.
struct GridBuilding {
    build: SharedBuild,
    dungeon: u32,
    why: String,
    body: NavBody,
    origin_x: f32,
    origin_y: f32,
    size: f32,
    landblock_ids: Vec<u32>,
    object_ids: Arc<HashSet<u32>>,
    /// Whether a walk asked for it or has waited on it, so that a failed
    /// build ends the walk; a build only a preview or the grid view waits on
    /// ends nothing.
    walk_waits: bool,
}

impl GridBuilding {
    /// Whether the grid will cover `point` as [`NavGrid::contains`] says.
    fn covers(&self, point: Vec3, margin: f32) -> bool {
        point.x >= self.origin_x + margin
            && point.y >= self.origin_y + margin
            && point.x <= self.origin_x + self.size - margin
            && point.y <= self.origin_y + self.size - margin
    }
}

/// The arrival distance a walk ends within by default, metres.
pub const DEFAULT_ARRIVAL_METERS: f32 = 2.5;
/// How often a walk plans again before it gives up blocked.
pub const MAXIMUM_REPLANS: i32 = 6;
/// Room kept around the character and the goal inside a planning region.
pub const REGION_MARGIN: f32 = 24.0;
/// The smallest planning region, metres.
pub const MINIMUM_REGION: f32 = 96.0;
/// The largest planning region, metres.
pub const MAXIMUM_REGION: f32 = 320.0;
/// The side of the grid kept around the character while the grid is shown.
pub const VIEW_REGION: f32 = 128.0;
/// A stage must bring the character at least this much nearer.
pub const MINIMUM_STAGE_PROGRESS: f32 = 16.0;
/// How long a stage that ends at the edge of the loaded ground waits for
/// the landblocks beyond it to load, seconds, as just after a portal.
pub const GROUND_WAIT_SECONDS: f64 = 10.0;
/// The waiting stage's word.
const WAITING_FOR_GROUND: &str = "waiting for the ground toward the goal to load";
/// A walk has at most this many stages.
pub const MAXIMUM_STAGES: i32 = 12;
/// The largest region a grid covering a whole sealed dungeon may have.
pub const MAXIMUM_DUNGEON_REGION: f32 = 2048.0;
/// A walk that stops making progress keeps its next plans out of a spot
/// this far ahead of the character and this wide.
pub const BLOCKED_SPOT_AHEAD: f32 = 0.75;
/// The width of that spot, metres.
pub const BLOCKED_SPOT_RADIUS: f32 = 0.6;
/// How far a way off what the body met moves it, metres.
pub const RECOVERY_METERS: f32 = 1.0;
/// How many of a walk's stops keep its later plans out of the spot ahead.
pub const AVOIDED_STOPS: i32 = 2;
/// How long a way off is waited for before planning again, seconds.
pub const RECOVERY_SECONDS: f64 = 1.5;
/// The charge of a hop forward.
pub const HOP_POWER: f32 = 0.35;
/// The roomiest spot nearby lies within this.
pub const ROOMIER_SPOT_REACH: f32 = 2.5;
/// At least this far away.
pub const ROOMIER_SPOT_LEAST: f32 = 0.3;
/// With walls at least this much farther than where the body stands.
pub const ROOMIER_SPOT_MARGIN: f32 = 0.15;
/// How far around a stuck spot the walk looks for what blocked it.
pub const BLOCKER_SEARCH_RADIUS: f32 = 1.5;
/// A route keeps out of placed objects when it ends no more than this much
/// farther from the goal than a route through them.
pub const OBSTACLE_DETOUR_REACH: f32 = 1.0;
/// The farthest out an object's side is taken, metres.
pub const MAXIMUM_GOAL_RADIUS: f32 = 8.0;
/// A follow keeps within this far behind the player by default.
pub const DEFAULT_FOLLOW_METERS: f32 = 3.0;
/// A follow holding behind the player goes on once they are this much
/// farther away.
pub const FOLLOW_SLACK_METERS: f32 = 1.5;
/// A follow plans again once the player has moved this far.
pub const FOLLOW_REPLAN_METERS: f32 = 2.0;
/// At most this often, seconds.
pub const FOLLOW_REPLAN_SECONDS: f64 = 0.5;
/// A follow with no route tries again after this, seconds.
pub const FOLLOW_RETRY_SECONDS: f64 = 1.0;
/// A holding follow goes on once the player is this much higher or lower.
pub const FOLLOW_CLIMB_METERS: f32 = 1.5;
/// A player with no floor within this of their feet is in the air.
pub const FOLLOW_FLOOR_REACH: f32 = 0.6;
/// A follow waits up to this long for a player in the air to land.
pub const FOLLOW_ALOFT_SECONDS: f64 = 2.0;
/// A follower stranded on a top steps this far off it toward the player.
pub const STEP_OFF_METERS: f32 = 1.5;
/// At most this many times running.
pub const MAXIMUM_STEP_OFFS: i32 = 3;
/// A piece of floor with fewer standing points than this is a small top.
pub const SMALL_TOP_NODES: i32 = 64;
/// A player who vanishes within this far of a portal went through it.
pub const FOLLOW_PORTAL_REACH: f32 = 6.0;
/// The follower walks to within this of the portal.
pub const PORTAL_ARRIVAL_METERS: f32 = 0.5;
/// And uses it this often, seconds.
pub const PORTAL_USE_SECONDS: f64 = 2.0;
/// A player seen to move more than this far within
/// [`FOLLOW_TELEPORT_SECONDS`] was moved by a portal or a recall.
pub const FOLLOW_TELEPORT_METERS: f32 = 20.0;
/// See [`FOLLOW_TELEPORT_METERS`].
pub const FOLLOW_TELEPORT_SECONDS: f64 = 1.0;
/// A player moved farther than this, with no portal beside them, is out of
/// reach and the follow sleeps.
pub const FOLLOW_TELEPORT_REACH: f32 = 100.0;
/// How far above or below a leg an object's footprint still counts as in
/// its way.
const NAVIGATION_OBSTACLE_HEIGHT: f32 = 2.0;
/// The deepest drop a walk plans, metres: falls of up to 12 m do no damage.
pub const SAFE_DROP_METERS: f32 = 12.0;
/// A grid is used while the character and the goal are this far inside it.
const COVER_MARGIN: f32 = 8.0;
const MAXIMUM_BUILDS_PER_PLAN: i32 = 2;
const VIEW_RETRY_TICKS: i64 = 120;
/// How far from the goal an object still counts as what it stands on.
const GOAL_OBJECT_REACH: f32 = 4.0;
const FACE_TOLERANCE_DEGREES: f32 = 10.0;
/// While walking, a closed door within this far ahead along the leg...
pub const DOOR_LOOK_AHEAD_METERS: f32 = 3.0;
/// ...and this near the line to it is opened before the character reaches
/// it.
pub const DOOR_CORRIDOR_METERS: f32 = 1.2;
/// Room to spare as the walk drives up to a door's edge.
pub const DOOR_USE_MARGIN: f32 = 0.1;
/// A door is used only this many quanta after the walk stops the character.
pub const DOOR_USE_SETTLE_TICKS: i64 = 3;
/// A used door is waited on this long to open, seconds.
pub const DOOR_OPEN_WAIT_SECONDS: f64 = 5.0;
/// A door that closes again is used at most this often.
pub const MAXIMUM_DOOR_USES: i32 = 2;
const DOOR_CHECK_TICKS: i64 = 5;
/// An appraisal is waited on this long, seconds.
pub const DOOR_APPRAISAL_WAIT_SECONDS: f64 = 2.0;
/// A waiting walk plans on once nothing has needed the character this
/// long, seconds.
pub const PAUSE_SETTLE_SECONDS: f64 = 1.5;
/// The character stands still once within this of one spot...
pub const STILL_METERS: f32 = 0.05;
/// ...for this long, seconds.
pub const STILL_SECONDS: f64 = 0.25;
/// Routes pass the creatures and players within this of the character.
pub const CROWD_REACH: f32 = 30.0;
/// The next stretch of route looked along for a creature stepping onto it.
pub const CROWD_LOOK_AHEAD_METERS: f32 = 6.0;
/// Every this many quanta.
pub const CROWD_CHECK_TICKS: i64 = 15;
/// A creature counts as planned with while within this of where it stood.
pub const CROWD_MOVED_METERS: f32 = 0.75;
/// A detour is planned at most once every this many seconds.
pub const DETOUR_INTERVAL_SECONDS: f64 = 1.0;
/// A walk stopped by a creature waits this long for it to move aside...
pub const SHUFFLE_WAIT_SECONDS: f64 = 1.5;
/// ...at most this many times...
pub const MAXIMUM_SHUFFLES: i32 = 2;
/// ...and may wait again once it has walked on this far.
pub const SHUFFLE_PROGRESS_METERS: f32 = 3.0;
/// How long the grid kept between walks outlives the last walk, seconds.
pub const GRID_IDLE_SECONDS: f64 = 30.0;
/// How far around a goal a walk that found no route says what it looked at.
const NARRATED_GOAL_SURROUNDINGS: f32 = 12.0;

/// A door a walk waits on: to appraise it, or to use it and see it open.
#[derive(Debug, Clone, PartialEq)]
struct DoorWait {
    door: NavDoor,
    use_at_tick: i64,
    used: bool,
    deadline_seconds: f64,
    appraising: bool,
}

/// One walk, route or follow request and everything it remembers.
#[derive(Debug)]
struct Request {
    sequence: i64,
    object_id: u32,
    arrival_meters: f32,
    walk: bool,
    place: Option<(u32, Vec3)>,
    onto: bool,
    follow: bool,
    /// Set while the request is the controller's active one.
    is_active: bool,
    /// Set once the request has ended.
    ended: bool,
    player_movement_input_frames: Option<i64>,
    goal_radius: f32,
    recovering: bool,
    chasing: Option<u32>,
    leader_seen: bool,
    leader_last_seen: Vec3,
    leader_seen_at: f64,
    asleep: bool,
    settled_leader: Vec3,
    leader_aloft_since: f64,
    step_offs: i32,
    leader_radius: f32,
    settled: bool,
    waiting_to_see: bool,
    was_in_portal_space: bool,
    follow_retry_at: f64,
    follow_replanned_at: f64,
    portal_used_at: f64,
    follow_said: Option<String>,
    goal: Vec3,
    goal_known: bool,
    replans: i32,
    recoveries: i32,
    builds: i32,
    built_for_objects: bool,
    rebuild_why: Option<String>,
    staged: bool,
    stages: i32,
    avoid: Vec<NavAvoidance>,
    stuck_spots: Vec<NavAvoidance>,
    /// Since when a stage that ended at the edge of the loaded ground has
    /// waited for the ground beyond it, and whether it still does.
    ground_wait_since: Option<f64>,
    awaits_ground: bool,
    avoided_object: Option<NavBlocker>,
    planned_avoid: Vec<NavAvoidance>,
    planned_obstacles: Vec<NavAvoidance>,
    blocked_by: Option<NavBlocker>,
    arrival_reason: Option<String>,
    ends_in_sight: bool,
    announced_leap: i32,
    waiting_on: Option<DoorWait>,
    door_uses: HashMap<u32, i32>,
    appraised_doors: HashSet<u32>,
    avoided_doors: HashSet<u32>,
    avoided_door: Option<(NavDoor, String)>,
    planned_crowd: Vec<NavAvoidance>,
    detouring: bool,
    detoured_at: f64,
    drives: i32,
    corners_run_around: i32,
    corners_turned_in_place: i32,
    shuffles: i32,
    shuffled_at: Vec3,
    shuffle_until: f64,
    passing_avoid: Vec<NavAvoidance>,
    paused_for: Option<String>,
    free_since: Option<f64>,
}

impl Request {
    fn new(
        sequence: i64,
        object_id: u32,
        arrival_meters: f32,
        walk: bool,
        place: Option<(u32, Vec3)>,
        onto: bool,
        follow: bool,
    ) -> Self {
        Self {
            sequence,
            object_id,
            arrival_meters,
            walk,
            place,
            onto,
            follow,
            is_active: false,
            ended: false,
            player_movement_input_frames: None,
            goal_radius: 0.0,
            recovering: false,
            chasing: None,
            leader_seen: false,
            leader_last_seen: Vec3::ZERO,
            leader_seen_at: f64::NEG_INFINITY,
            asleep: false,
            settled_leader: Vec3::ZERO,
            leader_aloft_since: f64::NAN,
            step_offs: 0,
            leader_radius: 0.0,
            settled: false,
            waiting_to_see: false,
            was_in_portal_space: false,
            follow_retry_at: 0.0,
            follow_replanned_at: f64::NEG_INFINITY,
            portal_used_at: f64::NAN,
            follow_said: None,
            goal: Vec3::ZERO,
            goal_known: false,
            replans: 0,
            recoveries: 0,
            builds: 0,
            built_for_objects: false,
            rebuild_why: None,
            staged: false,
            stages: 0,
            avoid: Vec::new(),
            stuck_spots: Vec::new(),
            ground_wait_since: None,
            awaits_ground: false,
            avoided_object: None,
            planned_avoid: Vec::new(),
            planned_obstacles: Vec::new(),
            blocked_by: None,
            arrival_reason: None,
            ends_in_sight: true,
            announced_leap: -1,
            waiting_on: None,
            door_uses: HashMap::new(),
            appraised_doors: HashSet::new(),
            avoided_doors: HashSet::new(),
            avoided_door: None,
            planned_crowd: Vec::new(),
            detouring: false,
            detoured_at: f64::NEG_INFINITY,
            drives: 0,
            corners_run_around: 0,
            corners_turned_in_place: 0,
            shuffles: 0,
            shuffled_at: Vec3::ZERO,
            shuffle_until: 0.0,
            passing_avoid: Vec::new(),
            paused_for: None,
            free_since: None,
        }
    }

    /// The object a walk plans to: the portal a follow chases, or the
    /// request's own object.
    fn goal_object_id(&self) -> u32 {
        self.chasing.unwrap_or(self.object_id)
    }
}

/// A line of narration the controller says.
pub type Say = Box<dyn Fn(&str) + Send>;

/// The leap finder a leap was last aimed again with, kept while the grid
/// and the body's leaping stay the same.
type AimFinder = NavLeapFinder<Arc<NavGrid>>;

/// Plans and walks. See the module documentation.
pub struct WalkController {
    mode: PlanMode,
    say: Option<Say>,
    /// Where a walk's full narration goes besides the reports every walk
    /// makes; `None` while nobody listens.
    pub narration: Option<Say>,
    /// Raised when the grid is let go, whichever way.
    pub grid_released: Option<Box<dyn Fn() + Send>>,
    /// Whether to keep a grid built around the character while nothing is
    /// planned.
    pub show_grid: bool,
    incoming: Option<Request>,
    stop_incoming: bool,
    sequence: i64,
    report: WalkReport,
    grid: Option<Arc<NavGrid>>,
    search_notes: Arc<Mutex<Vec<String>>>,
    building: Option<GridBuilding>,
    grid_dungeon: u32,
    /// The cell surfaces of the landblocks a grid was built over, kept for
    /// naming the cell of each point a preview reports, with where each
    /// landblock was loaded when they were copied.
    grid_cells: Option<(Vec<PlacedLandblock>, Arc<[ac_physics::CellSurface]>)>,
    /// The floors of the objects placed in those landblocks' interior cells,
    /// kept and let go with them.
    grid_object_floors: Option<(Vec<PlacedLandblock>, Arc<[ac_physics::CellSurface]>)>,
    objects_last_seen: u64,
    classified_cell_id: u32,
    classified_sealed: bool,
    tick: i64,
    seconds: f64,
    grid_idle_since: f64,
    still_at: Vec3,
    still_since: f64,
    view_retry_tick: i64,
    active: Option<Request>,
    routing: Option<Job<NavRoute>>,
    routing_for: Option<i64>,
    driver: Option<RouteDriver>,
    route: Option<NavRoute>,
    goal: Option<(Vec3, f32)>,
    leap_ability: Option<NavLeapAbility>,
    aim_finder: std::cell::RefCell<Option<AimFinder>>,
    aim_refused: std::cell::RefCell<Option<String>>,
}

impl std::fmt::Debug for WalkController {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WalkController")
            .field("report", &self.report)
            .field("grid", &self.grid.is_some())
            .field("driving", &self.driver.is_some())
            .finish_non_exhaustive()
    }
}

fn flat(point: Vec3) -> Vec2 {
    point.truncate()
}

fn horizontal_distance(from: Vec3, to: Vec3) -> f32 {
    flat(from).distance(flat(to))
}

fn snap(coordinate: f32) -> f32 {
    (coordinate / DEFAULT_CELL_SIZE).floor() * DEFAULT_CELL_SIZE
}

fn point(at: Vec3) -> String {
    format!(
        "({}, {}, {})",
        fixed(at.x, 1),
        fixed(at.y, 1),
        fixed(at.z, 1)
    )
}

fn spot(at: Vec2) -> String {
    format!("({}, {})", fixed(at.x, 2), fixed(at.y, 2))
}

/// Degrees signed, clockwise positive, with nothing written as minus zero.
fn degrees(value: f32) -> String {
    if value.abs() < 0.05 {
        "0.0".to_owned()
    } else if value > 0.0 {
        format!("+{}", fixed(value, 1))
    } else {
        format!("-{}", fixed(-value, 1))
    }
}

/// A number written with at most two decimals (the `0.##` pattern).
fn at_most_two_decimals(value: f32) -> String {
    let text = fixed(value, 2);
    let trimmed = text.trim_end_matches('0').trim_end_matches('.');
    trimmed.to_owned()
}

fn pace_name(pace: MovePace) -> &'static str {
    match pace {
        MovePace::Walk => "walk",
        MovePace::Run => "run",
    }
}

fn flat_distance_to_segment(at: Vec3, from: Vec3, to: Vec3) -> f32 {
    let start = flat(from);
    let along = flat(to) - start;
    let length_squared = along.length_squared();
    let t = if length_squared > 1e-6 {
        ((flat(at) - start).dot(along) / length_squared).clamp(0.0, 1.0)
    } else {
        0.0
    };
    flat(at).distance(start + along * t)
}

fn lerp(a: Vec3, b: Vec3, t: f32) -> Vec3 {
    a * (1.0 - t) + b * t
}

/// A square region holding a rectangle with room around it, as (origin x,
/// origin y, size), or `None` when it would be larger than `largest`.
fn try_choose_square(minimum: Vec2, maximum: Vec2, largest: f32) -> Option<(f32, f32, f32)> {
    let span = (maximum.x - minimum.x).max(maximum.y - minimum.y) + 2.0 * REGION_MARGIN;
    let size = (MINIMUM_REGION.max(span) / 16.0).ceil() * 16.0;
    if size > largest {
        return None;
    }
    Some((
        snap((minimum.x + maximum.x) * 0.5 - size * 0.5),
        snap((minimum.y + maximum.y) * 0.5 - size * 0.5),
        size,
    ))
}

/// A square region holding both points with room around them, as (origin
/// x, origin y, size), or `None` when they are too far apart.
#[must_use]
pub fn try_choose_region(from: Vec3, to: Vec3) -> Option<(f32, f32, f32)> {
    try_choose_square(
        Vec2::new(from.x.min(to.x), from.y.min(to.y)),
        Vec2::new(from.x.max(to.x), from.y.max(to.y)),
        MAXIMUM_REGION,
    )
}

/// The region one stage of a walk to a goal too far for one region is
/// planned over: the largest region, holding the character with room behind
/// it and reaching as far toward the goal as it can.
#[must_use]
pub fn choose_stage_region(from: Vec3, to: Vec3) -> (f32, f32, f32) {
    let size = MAXIMUM_REGION;
    let half = size * 0.5;
    let toward = Vec2::new(to.x - from.x, to.y - from.y);
    let longest = toward.x.abs().max(toward.y.abs());
    let centre = flat(from)
        + if longest > 1e-3 {
            toward * ((half - REGION_MARGIN) / longest)
        } else {
            Vec2::ZERO
        };
    (snap(centre.x - half), snap(centre.y - half), size)
}

/// How near the goal a route must end for the character to stop within
/// `arrival_meters` of it, since the driver counts a leg's end as reached a
/// little before the character stands on it.
#[must_use]
pub fn planning_radius(arrival_meters: f32) -> f32 {
    (arrival_meters - ARRIVAL_RADIUS).max(DEFAULT_CELL_SIZE)
}

/// A route's legs from where the character is once planning has taken a
/// moment: the character's position, then the leg ends it has not yet
/// passed; with how many were skipped.
fn onward_legs(route: &NavRoute, position: Vec3) -> (Vec<Vec3>, usize) {
    let mut first = 1;
    while first < route.legs.len() - 1
        && (flat(route.legs[first]) - flat(position))
            .dot(flat(route.legs[first]) - flat(route.legs[first - 1]))
            <= 0.0
    {
        first += 1;
    }
    let skipped = first - 1;
    let mut legs = Vec::with_capacity(route.legs.len() - skipped);
    legs.push(position);
    legs.extend_from_slice(&route.legs[first..]);
    (legs, skipped)
}

/// The route that keeps out of the objects the server placed, unless only
/// a route through them arrives, or one through them ends more than
/// [`OBSTACLE_DETOUR_REACH`] nearer the goal.
fn around_obstacles(
    goal: Vec3,
    avoid: &[NavAvoidance],
    obstacles: &[NavAvoidance],
    find: impl Fn(&[NavAvoidance]) -> NavRoute,
) -> NavRoute {
    if obstacles.is_empty() {
        return find(avoid);
    }
    let both: Vec<NavAvoidance> = avoid.iter().chain(obstacles).copied().collect();
    let around = find(&both);
    if around.outcome == NavRouteOutcome::Routed && around.reason == "routed" {
        return around;
    }
    let through = find(avoid);
    if around.outcome != NavRouteOutcome::Routed {
        return through;
    }
    if through.outcome != NavRouteOutcome::Routed {
        return around;
    }
    let around_end = *around.legs.last().expect("routed");
    let through_end = *through.legs.last().expect("routed");
    if horizontal_distance(around_end, goal)
        <= horizontal_distance(through_end, goal) + OBSTACLE_DETOUR_REACH
    {
        around
    } else {
        through
    }
}

/// Whether a grid reaches far enough toward a goal beyond it to plan a
/// stage over from where the character stands.
fn reaches_toward(grid: &NavGrid, from: Vec3, goal: Vec3) -> bool {
    if grid.size() <= 2.0 * REGION_MARGIN {
        return false;
    }
    let x = goal.x.clamp(
        grid.origin_x() + REGION_MARGIN,
        grid.origin_x() + grid.size() - REGION_MARGIN,
    );
    let y = goal.y.clamp(
        grid.origin_y() + REGION_MARGIN,
        grid.origin_y() + grid.size() - REGION_MARGIN,
    );
    horizontal_distance(from, goal) - horizontal_distance(Vec3::new(x, y, 0.0), goal)
        >= 2.0 * MINIMUM_STAGE_PROGRESS
}

/// A follow's route: onto the floor the player stands on, walking or
/// leaping. A route that only ends near the player is not taken.
#[allow(clippy::too_many_arguments)]
fn follow_route(
    grid: &NavGrid,
    from: Vec3,
    player: Vec3,
    arrival: f32,
    spots: &[NavAvoidance],
    leaps: Option<NavLeapAbility>,
    crowd: &[NavAvoidance],
    player_radius: f32,
    notes: Option<&Mutex<Vec<String>>>,
) -> NavRoute {
    let on_floor = route::find(
        grid,
        from,
        player,
        arrival,
        &NavRouteRequest {
            avoid: spots,
            leaps,
            crowd,
            arrive_on_goal_floor: true,
            goal_radius: player_radius,
        },
    );
    if on_floor.outcome == NavRouteOutcome::Routed && on_floor.reason == "routed" {
        return on_floor;
    }
    let why = if on_floor.outcome == NavRouteOutcome::Routed {
        format!(
            "no route reaches the player's floor; the nearest a route comes is short of it ({})",
            on_floor.reason
        )
    } else {
        format!("no route reaches the player's floor ({})", on_floor.reason)
    };
    if let Some(notes) = notes {
        notes
            .lock()
            .expect("the search notes")
            .push(format!("{why}; holding where the character stands"));
    }
    NavRoute {
        outcome: NavRouteOutcome::NoPath,
        reason: why,
        path: Vec::new(),
        legs: Vec::new(),
        leaps: Vec::new(),
        ..on_floor
    }
}

/// Whether a body at `at` stands on a top too small to be more than a
/// perch, or on no floor the grid knows at all.
fn on_small_top(grid: &NavGrid, at: Vec3) -> bool {
    let node = grid.find_standing_node(at, START_RADIUS, START_HEIGHT_TOLERANCE);
    if node < 0 {
        return true;
    }
    let pieces = grid.surfaces().piece_of();
    let piece = pieces[node as usize];
    if piece < 0 {
        return true;
    }
    let mut count = 0;
    for &other in pieces {
        if other == piece {
            count += 1;
            if count >= SMALL_TOP_NODES {
                return false;
            }
        }
    }
    true
}

/// Whether any floor a body stands on lies within reach of a point.
fn has_floor_near(grid: &NavGrid, at: Vec3) -> bool {
    let reach = (FOLLOW_FLOOR_REACH / grid.cell_size()).ceil() as i32;
    let centre_x = ((at.x - grid.origin_x()) / grid.cell_size()).floor() as i32;
    let centre_y = ((at.y - grid.origin_y()) / grid.cell_size()).floor() as i32;
    for y in centre_y - reach..=centre_y + reach {
        for x in centre_x - reach..=centre_x + reach {
            let (first, count) = grid.nodes_in_column(x, y);
            for node in first..first + count {
                if (grid.position(node).z - at.z).abs() <= FOLLOW_FLOOR_REACH {
                    return true;
                }
            }
        }
    }
    false
}

/// The widest an object's collision reaches from where it stands, measured
/// flat, and no more than [`MAXIMUM_GOAL_RADIUS`].
fn radius_of(surfaces: &NavSurfaces, at: Vec3) -> f32 {
    let middle = flat(at);
    let mut radius = 0.0f32;
    for triangle in &surfaces.triangles {
        radius = radius.max(flat(triangle.a).distance(middle));
        radius = radius.max(flat(triangle.b).distance(middle));
        radius = radius.max(flat(triangle.c).distance(middle));
    }
    for cylinder in &surfaces.cylinders {
        radius = radius.max(flat(cylinder.base).distance(middle) + cylinder.radius);
    }
    radius.min(MAXIMUM_GOAL_RADIUS)
}

/// How many of the objects the server placed a route's legs pass through.
fn passed_obstacles(route: &NavRoute, obstacles: &[NavAvoidance]) -> usize {
    let mut passed = 0;
    for obstacle in obstacles {
        let centre = flat(obstacle.centre);
        for index in 1..route.legs.len() {
            let start = flat(route.legs[index - 1]);
            let along = flat(route.legs[index]) - start;
            let length_squared = along.length_squared();
            let t = if length_squared > 1e-6 {
                ((centre - start).dot(along) / length_squared).clamp(0.0, 1.0)
            } else {
                0.0
            };
            if centre.distance(start + along * t) < obstacle.radius
                && (route.legs[index].z - obstacle.centre.z).abs() <= NAVIGATION_OBSTACLE_HEIGHT
            {
                passed += 1;
                break;
            }
        }
    }
    passed
}

fn leaps_of(route: &NavRoute) -> Vec<RouteLeap> {
    route
        .leaps
        .iter()
        .map(|leap| RouteLeap {
            leg_index: leap.leg_index,
            power: leap.power,
            run: leap.run,
        })
        .collect()
}

/// How a request's goal is named in what a walk says.
fn label(request: &Request) -> String {
    let Some((cell_id, local)) = request.place else {
        return format!("0x{:08X}", request.object_id);
    };
    if cell_id != 0 {
        return format!(
            "0x{cell_id:08X} [{} {} {}]",
            fixed(local.x, 1),
            fixed(local.y, 1),
            fixed(local.z, 1)
        );
    }
    let north_south = (local.y - 127.0 * LANDBLOCK_SIZE - 84.0) / 240.0;
    let east_west = (local.x - 127.0 * LANDBLOCK_SIZE - 84.0) / 240.0;
    format!(
        "{}{}, {}{}",
        fixed(north_south.abs(), 3),
        if north_south < 0.0 { 'S' } else { 'N' },
        fixed(east_west.abs(), 3),
        if east_west < 0.0 { 'W' } else { 'E' }
    )
}

fn blocked_reason(request: &Request) -> String {
    match &request.blocked_by {
        Some(blocker) => format!(
            "the character stopped making progress beside {}{} (0x{:08X})",
            if blocker.is_closed_door {
                "a closed door, "
            } else if blocker.hostile {
                "a hostile monster, "
            } else {
                ""
            },
            blocker.name,
            blocker.object_id
        ),
        None => "the character stopped making progress".to_owned(),
    }
}

/// The route left to walk, and for one stage of a longer walk the straight
/// line on from its end to the goal.
fn remaining(request: &Request, driver: &RouteDriver, position: Vec3) -> f32 {
    let mut left = 0.0;
    let mut at = flat(position);
    for index in driver.leg_index() as usize..driver.legs().len() {
        let end = flat(driver.legs()[index]);
        left += at.distance(end);
        at = end;
    }
    if request.staged {
        left + at.distance(flat(request.goal))
    } else {
        left
    }
}

/// Whether the player's own movement input has asked the character to move
/// since the walk began, whatever the walk is doing. A route asked for alone
/// is never ended by it.
fn player_took_the_character(request: &mut Request, sample: &WalkBodySample) -> bool {
    if !request.walk {
        return false;
    }
    let Some(began) = request.player_movement_input_frames else {
        request.player_movement_input_frames = Some(sample.player_movement_input_frames);
        return false;
    };
    sample.player_movement_input_frames > began
}

/// Whether a set of crowd spots holds one within `meters` of a point.
fn crowd_near(crowd: &[NavAvoidance], at: Vec3, meters: f32) -> bool {
    crowd
        .iter()
        .any(|planned| horizontal_distance(planned.centre, at) <= meters)
}

impl WalkController {
    /// A controller that says its progress through `say`.
    #[must_use]
    pub fn new(mode: PlanMode, say: Option<Say>) -> Self {
        Self {
            mode,
            say,
            narration: None,
            grid_released: None,
            show_grid: false,
            incoming: None,
            stop_incoming: false,
            sequence: 0,
            report: WalkReport::default(),
            grid: None,
            search_notes: Arc::new(Mutex::new(Vec::new())),
            building: None,
            grid_dungeon: 0,
            grid_cells: None,
            grid_object_floors: None,
            objects_last_seen: 0,
            classified_cell_id: 0,
            classified_sealed: false,
            tick: 0,
            seconds: 0.0,
            grid_idle_since: f64::NAN,
            still_at: Vec3::ZERO,
            still_since: f64::NAN,
            view_retry_tick: 0,
            active: None,
            routing: None,
            routing_for: None,
            driver: None,
            route: None,
            goal: None,
            leap_ability: None,
            aim_finder: std::cell::RefCell::new(None),
            aim_refused: std::cell::RefCell::new(None),
        }
    }

    // ── What callers read ───────────────────────────────────────────────

    /// The grid most recently built.
    #[must_use]
    pub fn grid(&self) -> Option<&Arc<NavGrid>> {
        self.grid.as_ref()
    }

    /// The route most recently searched for.
    #[must_use]
    pub fn route(&self) -> Option<&NavRoute> {
        self.route.as_ref()
    }

    /// The goal of the most recent request that found its object, and how
    /// near counts as arriving.
    #[must_use]
    pub fn goal(&self) -> Option<(Vec3, f32)> {
        self.goal
    }

    /// The index in the route's legs of the leg end the character is
    /// walking toward, while it walks.
    #[must_use]
    pub fn leg_index(&self) -> Option<i32> {
        self.driver.as_ref().map(RouteDriver::leg_index)
    }

    /// Whether the route being walked is one stage of a walk beyond it.
    #[must_use]
    pub fn is_staged(&self) -> bool {
        self.active.as_ref().is_some_and(|active| active.staged)
    }

    /// Whether a grid is being built or a route searched for.
    #[must_use]
    pub fn is_searching(&self) -> bool {
        self.routing.is_some() || self.building.is_some()
    }

    /// Whether a grid build or route search under way has finished, so the
    /// next tick takes it up.
    #[must_use]
    pub fn search_finished(&self) -> bool {
        self.building
            .as_ref()
            .is_some_and(|b| b.build.is_finished())
            || self.routing.as_ref().is_some_and(Job::is_finished)
    }

    /// Whether a walk must wait for a grid build or route search under way.
    /// A build it waits on is marked so, whoever started it, so that the
    /// build failing ends the walk.
    fn waits_on_search(&mut self) -> bool {
        if let Some(building) = self.building.as_mut() {
            building.walk_waits = true;
            return true;
        }
        self.routing.is_some()
    }

    /// Whether a request is waiting, being planned or being walked.
    #[must_use]
    pub fn is_busy(&self) -> bool {
        self.incoming.is_some() || self.active.is_some()
    }

    /// Where the latest request stands.
    #[must_use]
    pub fn report(&self) -> &WalkReport {
        &self.report
    }

    /// The portals within `radius` of a point that routes keep out of,
    /// other than the one the latest walk goes to.
    #[must_use]
    pub fn portals_near(
        &self,
        goals: &dyn NavGoalSource,
        around: Vec3,
        radius: f32,
    ) -> Vec<NavAvoidance> {
        goals.find_portals(
            around,
            radius,
            self.report.object_id,
            self.goal.map_or(Vec3::splat(f32::MAX), |(at, _)| at),
        )
    }

    // ── Requests ────────────────────────────────────────────────────────

    /// Asks for a route to an object without walking it; returns the
    /// request's sequence.
    pub fn route_to(&mut self, object_id: u32, arrival_meters: f32) -> i64 {
        self.enqueue(object_id, arrival_meters, false, None, false, false)
    }

    /// Asks to walk to an object; returns the request's sequence.
    pub fn walk_to(&mut self, object_id: u32, arrival_meters: f32) -> i64 {
        self.enqueue(object_id, arrival_meters, true, None, false, false)
    }

    /// Asks to walk to a place, a point in the landblock-local frame of
    /// `cell_id` as /loc gives it (or for a cell of zero, from the corner of
    /// the first landblock). A walk to a place faces nothing on arrival.
    pub fn walk_to_place(&mut self, cell_id: u32, local: Vec3, arrival_meters: f32) -> i64 {
        self.enqueue(
            0,
            arrival_meters,
            true,
            Some((cell_id, local)),
            false,
            false,
        )
    }

    /// Asks to walk onto an object and stand on its top, jumping up where
    /// the character can.
    pub fn stand_on(&mut self, object_id: u32, arrival_meters: f32) -> i64 {
        self.enqueue(object_id, arrival_meters, true, None, true, false)
    }

    /// Asks to follow a player, keeping within `buffer_meters` behind them,
    /// until stopped.
    pub fn follow(&mut self, player_id: u32, buffer_meters: f32) -> i64 {
        self.enqueue(player_id, buffer_meters, true, None, false, true)
    }

    /// Ends the request under way, stopping the moves its walk began, on
    /// the next tick.
    pub fn stop(&mut self) {
        self.incoming = None;
        self.stop_incoming = true;
    }

    fn enqueue(
        &mut self,
        object_id: u32,
        arrival_meters: f32,
        walk: bool,
        place: Option<(u32, Vec3)>,
        onto: bool,
        follow: bool,
    ) -> i64 {
        let arrival_meters = if !(arrival_meters > 0.0) || !arrival_meters.is_finite() {
            DEFAULT_ARRIVAL_METERS
        } else {
            arrival_meters
        };
        self.sequence += 1;
        let sequence = self.sequence;
        self.incoming = Some(Request::new(
            sequence,
            object_id,
            arrival_meters,
            walk,
            place,
            onto,
            follow,
        ));
        self.stop_incoming = false;
        self.report = WalkReport {
            sequence,
            state: WalkState::Planning,
            object_id,
            remaining_meters: f32::NAN,
            replans: 0,
            reason: "waiting for the next frame".to_owned(),
            blocked_by_object_id: 0,
        };
        sequence
    }

    // ── Narration ───────────────────────────────────────────────────────

    /// Reports a line of the walk's progress, and narrates it.
    fn say(&self, line: &str) {
        if let Some(say) = &self.say {
            say(line);
        }
        if let Some(narration) = &self.narration {
            narration(line);
        }
    }

    /// Narrates a detail only a debugging listener wants.
    fn detail(&self, line: &str) {
        if let Some(narration) = &self.narration {
            narration(line);
        }
    }

    fn narrating(&self) -> bool {
        self.narration.is_some()
    }

    // ── The quantum ─────────────────────────────────────────────────────

    /// Advances planning and walking by one quantum, `elapsed_seconds` long.
    /// Call it on the thread that owns the physics world, before the
    /// player's movement input is read.
    pub fn tick(&mut self, cx: &mut WalkWorld<'_>, elapsed_seconds: f64) {
        self.tick += 1;
        if elapsed_seconds > 0.0 && elapsed_seconds.is_finite() {
            self.seconds += elapsed_seconds;
        }
        self.collect_build(cx);
        self.collect_route(cx);

        let incoming = self.incoming.take();
        let stop = std::mem::take(&mut self.stop_incoming);

        let sample = cx.body.try_sample();
        let in_world = sample.is_some();
        if let Some(sample) = &sample {
            if let Some(ability) = sample.leaps {
                self.leap_ability = Some(ability);
            }
            if self.still_since.is_nan() || sample.position.distance(self.still_at) > STILL_METERS {
                self.still_at = sample.position;
                self.still_since = self.seconds;
            }
        }
        if stop || incoming.is_some() {
            self.cancel(
                cx,
                if incoming.is_none() {
                    "stopped"
                } else {
                    "a newer request replaced it"
                },
            );
        }
        if let Some(incoming) = incoming {
            self.begin(cx, incoming, in_world);
        }

        if let Some(mut active) = self.active.take() {
            self.grid_idle_since = f64::NAN;
            match &sample {
                None => self.end(
                    cx,
                    &mut active,
                    WalkState::Lost,
                    "the character left the world",
                ),
                Some(sample) if player_took_the_character(&mut active, sample) => self.end(
                    cx,
                    &mut active,
                    WalkState::Interrupted,
                    "the player moved the character",
                ),
                Some(sample) => self.advance(cx, &mut active, sample),
            }
            if !active.ended {
                self.active = Some(active);
            }
            return;
        }
        self.release_unloaded_grid(cx);
        self.release_idle_grid();
        if let Some(sample) = sample
            && self.show_grid
        {
            self.keep_view_grid(cx, &sample);
        }
    }

    fn begin(&mut self, cx: &mut WalkWorld<'_>, mut request: Request, in_world: bool) {
        if !in_world {
            self.end(
                cx,
                &mut request,
                WalkState::Lost,
                "the character is not in the world",
            );
            return;
        }
        if request.follow {
            self.begin_follow(cx, request);
            return;
        }
        let located = match request.place {
            Some((cell_id, local)) => cx.goals.try_locate_place(cell_id, local),
            None => cx.goals.try_locate(request.object_id),
        };
        let Some(goal) = located else {
            let why = format!("the client has no position for {}", label(&request));
            self.end(cx, &mut request, WalkState::NoRoute, &why);
            return;
        };

        request.goal = goal;
        request.goal_known = true;
        if request.place.is_none()
            && !request.onto
            && let Some(surfaces) = cx.goals.try_get_surfaces(request.object_id)
        {
            request.goal_radius = radius_of(&surfaces, goal);
        }
        let verb = if request.walk { "Walk" } else { "Route" };
        let direction = if request.onto { "onto" } else { "to" };
        if self.narrating() && request.place.is_none() {
            self.detail(&format!(
                "{verb} {direction} {}: {}",
                label(&request),
                self.collision_of(cx, request.object_id, goal)
            ));
        }
        if self.narrating()
            && let Some(start) = cx.body.try_sample()
        {
            self.detail(&format!(
                "{verb} {direction} {}: from {} in cell 0x{:08X} toward {}, within {} m",
                label(&request),
                point(start.position),
                start.cell_id,
                point(goal),
                at_most_two_decimals(request.arrival_meters)
            ));
        }
        request.is_active = true;
        self.route = None;
        self.goal = Some((goal, request.arrival_meters));
        self.publish(&request, WalkState::Planning, "planning", f32::NAN);
        self.active = Some(request);
    }

    fn advance(&mut self, cx: &mut WalkWorld<'_>, active: &mut Request, sample: &WalkBodySample) {
        if active.follow && self.follow_holds(cx, active, sample) {
            return;
        }
        if self.paused(cx, active, sample) {
            return;
        }
        if let Some(wait) = active.waiting_on.clone() {
            self.wait_for_door(cx, active, wait, sample);
            return;
        }
        if active.shuffle_until > self.seconds {
            return;
        }
        if self.driver.is_some() {
            self.drive(cx, active, sample);
            return;
        }
        if self.waits_on_search() {
            return;
        }
        if sample.in_portal_space {
            self.end(
                cx,
                active,
                WalkState::Lost,
                "the character entered portal space",
            );
            return;
        }

        // A route starts from floor, so a walk asked for in the air plans
        // once the character lands, however long it is aloft.
        if sample.airborne {
            self.publish(
                active,
                WalkState::Planning,
                "waiting for the character to land",
                f32::NAN,
            );
            return;
        }

        let dungeon_cells = self.try_measure_dungeon(cx, sample.cell_id);
        let dungeon = (sample.cell_id & 0xFFFF_0000) | 0xFFFF;
        let grid_dungeon = if dungeon_cells.is_some() { dungeon } else { 0 };
        let mut usable = self.grid.clone().filter(|built| {
            built.body() == sample.body
                && self.grid_dungeon == grid_dungeon
                && built.contains(sample.position, COVER_MARGIN)
                && !self.is_stale(cx, built, grid_dungeon)
        });
        if let Some(have) = &usable
            && !active.built_for_objects
            && self.goal_stands_on_what_the_grid_has_not(cx, have, active, sample.body)
        {
            active.built_for_objects = true;
            active.rebuild_why =
                Some("the goal stands on something the grid has none of".to_owned());
            usable = None;
        }
        // A stage that waits for the ground beyond the loaded edge holds
        // while the grid still stands for the landblocks loaded, until it
        // has waited long enough to plan again and end.
        if active.awaits_ground {
            let waited = active
                .ground_wait_since
                .map_or(0.0, |since| self.seconds - since);
            if usable.is_some() && waited < GROUND_WAIT_SECONDS {
                return;
            }
            if usable.is_none() {
                // More ground has loaded: a new plan over it.
                active.builds = 0;
            }
            active.awaits_ground = false;
        }
        let mut staged = false;
        if !usable
            .as_ref()
            .is_some_and(|grid| grid.contains(active.goal, COVER_MARGIN))
        {
            if let Some((cells_minimum, cells_maximum)) = dungeon_cells {
                let at = flat(sample.position).min(flat(active.goal));
                let to = flat(sample.position).max(flat(active.goal));
                match try_choose_square(
                    cells_minimum.min(at),
                    cells_maximum.max(to),
                    MAXIMUM_DUNGEON_REGION,
                ) {
                    Some((x, y, size)) => self.build_grid(cx, active, sample, x, y, size, dungeon),
                    None => {
                        let why = format!(
                            "the goal lies too far outside this dungeon, {} m away",
                            fixed(horizontal_distance(sample.position, active.goal), 0)
                        );
                        self.end(cx, active, WalkState::NoRoute, &why);
                    }
                }
                return;
            }
            if let Some((x, y, size)) = try_choose_region(sample.position, active.goal) {
                self.build_grid(cx, active, sample, x, y, size, 0);
                return;
            }
            if !usable
                .as_ref()
                .is_some_and(|grid| reaches_toward(grid, sample.position, active.goal))
            {
                let (x, y, size) = choose_stage_region(sample.position, active.goal);
                self.build_grid(cx, active, sample, x, y, size, 0);
                return;
            }
            staged = true;
        }

        let grid = usable.expect("a usable grid");
        self.search_route(cx, active, grid, sample, staged, false);
    }

    /// Starts searching a grid for a route from where the character stands
    /// to the goal, or toward it for one stage, passing the creatures and
    /// players near the character where they stand now. A detour is
    /// searched while the walk goes on.
    fn search_route(
        &mut self,
        cx: &mut WalkWorld<'_>,
        active: &mut Request,
        grid: Arc<NavGrid>,
        sample: &WalkBodySample,
        staged: bool,
        detour: bool,
    ) {
        let from = sample.position;
        let to = active.goal;
        let arrival = planning_radius(if active.chasing.is_none() {
            active.arrival_meters
        } else {
            PORTAL_ARRIVAL_METERS
        });
        let mut avoid: Vec<NavAvoidance> = active.avoid.clone();
        avoid.extend_from_slice(&active.stuck_spots);
        avoid.extend_from_slice(&active.passing_avoid);
        avoid.extend(self.portals(cx, &grid, sample.body, active.goal_object_id(), to));
        active.passing_avoid.clear();
        let obstacles = self.obstacles(cx, &grid, sample.body, active.goal_object_id());
        let crowd = self.crowd(cx, sample, active.goal_object_id());
        active.planned_obstacles = obstacles.clone();
        active.planned_avoid = avoid.clone();
        active.planned_crowd = crowd.clone();
        active.detouring = detour;
        let leaps = sample.leaps;
        // A walk to a place ends on the floor the place stands on; a walk to
        // an object ends beside it. A follow arrives on the player's own floor
        // when a route reaches it.
        let on_goal_floor = active.place.is_some();
        let follow_floor = active.follow && active.chasing.is_none();
        let goal_radius = active.goal_radius;
        let mut onto = None;
        if active.onto && !staged {
            match cx.goals.try_get_surfaces(active.object_id) {
                Some(surfaces) => onto = Some(surfaces),
                None => {
                    let why = format!(
                        "the client has no collision for {} to stand on",
                        label(active)
                    );
                    self.end(cx, active, WalkState::NoRoute, &why);
                    return;
                }
            }
        }
        active.staged = staged;
        if self.narrating() && !detour {
            self.detail(&format!(
                "{} {} {}: searching {} {} from {} over {} m from ({}, {}); arrival within {} m{}; keeping out of {} spots and {} placed objects, passing {} creatures; {}",
                if active.walk { "Walk" } else { "Route" },
                if active.onto { "onto" } else { "to" },
                label(active),
                if staged { "toward" } else { "for" },
                point(to),
                point(from),
                fixed(grid.size(), 0),
                fixed(grid.origin_x(), 0),
                fixed(grid.origin_y(), 0),
                fixed(arrival, 1),
                if goal_radius > 0.0 {
                    format!(" of the side, the object reaching {} m", fixed(goal_radius, 1))
                } else {
                    String::new()
                },
                avoid.len(),
                obstacles.len(),
                crowd.len(),
                if leaps.is_none() { "no leaps" } else { "leaps allowed" }
            ));
        }
        // A follow that plans again around where it stuck holds only if
        // planning through that spot fails too.
        let notes = if active.stuck_spots.is_empty() {
            Some(Arc::clone(&self.search_notes))
        } else {
            None
        };
        self.routing_for = Some(active.sequence);
        let work = move || {
            if staged {
                around_obstacles(to, &avoid, &obstacles, |spots| {
                    route::find_toward(
                        &grid,
                        from,
                        to,
                        REGION_MARGIN,
                        &NavRouteRequest {
                            avoid: spots,
                            leaps,
                            crowd: &crowd,
                            ..NavRouteRequest::default()
                        },
                    )
                })
            } else if let Some(onto) = &onto {
                around_obstacles(to, &avoid, &obstacles, |spots| {
                    route::find_onto(
                        &grid,
                        from,
                        onto,
                        arrival,
                        &NavRouteRequest {
                            avoid: spots,
                            leaps,
                            crowd: &crowd,
                            ..NavRouteRequest::default()
                        },
                    )
                })
            } else if follow_floor {
                around_obstacles(to, &avoid, &obstacles, |spots| {
                    follow_route(
                        &grid,
                        from,
                        to,
                        arrival,
                        spots,
                        leaps,
                        &crowd,
                        goal_radius,
                        notes.as_deref(),
                    )
                })
            } else {
                around_obstacles(to, &avoid, &obstacles, |spots| {
                    route::find(
                        &grid,
                        from,
                        to,
                        arrival,
                        &NavRouteRequest {
                            avoid: spots,
                            leaps,
                            crowd: &crowd,
                            arrive_on_goal_floor: on_goal_floor,
                            goal_radius,
                        },
                    )
                })
            }
        };
        self.routing = Some(Job::start(self.mode, work));
    }

    /// The creatures and players near the character, each widened by the
    /// body's radius, for a route to pass.
    fn crowd(
        &self,
        cx: &WalkWorld<'_>,
        sample: &WalkBodySample,
        goal_object_id: u32,
    ) -> Vec<NavAvoidance> {
        cx.goals
            .find_crowd(sample.position, CROWD_REACH, goal_object_id)
            .into_iter()
            .map(|spot| NavAvoidance::new(spot.centre, spot.radius + sample.body.radius))
            .collect()
    }

    /// Whether a creature or player the route was not planned with stands
    /// across the next stretch of it.
    fn crowd_stepped_onto(
        &self,
        cx: &WalkWorld<'_>,
        active: &Request,
        driver: &RouteDriver,
        sample: &WalkBodySample,
    ) -> bool {
        if driver.leg_index() as usize >= driver.legs().len() {
            return false;
        }
        for spot in self.crowd(cx, sample, active.object_id) {
            if horizontal_distance(spot.centre, sample.position)
                > CROWD_LOOK_AHEAD_METERS + spot.radius
                || crowd_near(&active.planned_crowd, spot.centre, CROWD_MOVED_METERS)
            {
                continue;
            }
            let mut from = sample.position;
            let mut left = CROWD_LOOK_AHEAD_METERS;
            let mut index = driver.leg_index() as usize;
            while index < driver.legs().len() && left > 0.0 {
                let to = driver.legs()[index];
                let length = horizontal_distance(from, to);
                let end = if length > left {
                    lerp(from, to, left / length)
                } else {
                    to
                };
                if flat_distance_to_segment(spot.centre, from, end) < spot.radius
                    && (spot.centre.z - end.z).abs() <= NAVIGATION_OBSTACLE_HEIGHT
                {
                    return true;
                }
                left -= length;
                from = to;
                index += 1;
            }
        }
        false
    }

    /// Starts planning a way around creatures on the route from where the
    /// character is, while the walk goes on.
    fn start_detour(
        &mut self,
        cx: &mut WalkWorld<'_>,
        active: &mut Request,
        sample: &WalkBodySample,
    ) {
        let Some(grid) = self.grid.clone() else {
            return;
        };
        if grid.body() != sample.body
            || !grid.contains(sample.position, COVER_MARGIN)
            || (!active.staged && !grid.contains(active.goal, COVER_MARGIN))
        {
            return;
        }
        active.detoured_at = self.seconds;
        let staged = active.staged;
        self.search_route(cx, active, grid, sample, staged, true);
    }

    /// The hooks a drive asks the world through: corner arcs the grid lets
    /// a body brush along, leaps aimed again on the grid, and pieces of
    /// floor.
    fn drive_hooks<'h>(
        &'h self,
        can_cut: &'h dyn Fn(&[Vec3]) -> bool,
        aim: &'h dyn Fn(Vec3, Vec3, bool, bool) -> Option<RouteLeapAim>,
        onward: &'h dyn Fn(Vec3, Vec3, bool) -> Option<(RouteLeapAim, Vec3)>,
        same: &'h dyn Fn(Vec3, Vec3) -> bool,
    ) -> RouteDriveHooks<'h> {
        RouteDriveHooks {
            can_cut_along: self.grid.is_some().then_some(can_cut),
            careful_at: None,
            aim_leap_from: Some(aim),
            aim_onward: Some(onward),
            same_floor: Some(same),
        }
    }

    /// Advances the driver one quantum against its hooks.
    fn advance_driver(&mut self, sample: &RouteDriveSample) -> (RouteDriveStep, RouteDriver) {
        let mut driver = self.driver.take().expect("a driver");
        let step = {
            let can_cut =
                |path: &[Vec3]| self.grid.as_ref().is_some_and(|g| g.can_brush_along(path));
            let aim = |standing: Vec3, landing: Vec3, run: bool, at: bool| {
                self.aim_leap_from(standing, landing, run, at)
            };
            let onward =
                |standing: Vec3, landing: Vec3, run: bool| self.aim_onward(standing, landing, run);
            let same = |first: Vec3, second: Vec3| self.same_floor(first, second);
            let hooks = self.drive_hooks(&can_cut, &aim, &onward, &same);
            driver.advance(sample, &hooks)
        };
        (step, driver)
    }

    fn drive(&mut self, cx: &mut WalkWorld<'_>, active: &mut Request, sample: &WalkBodySample) {
        let door_ahead = if self.tick % DOOR_CHECK_TICKS == 0
            && !self.driver.as_ref().expect("driving").is_leaping()
        {
            self.closed_door_ahead(cx, sample)
        } else {
            None
        };
        if let Some(door) = door_ahead
            && !active.avoided_doors.contains(&door.object_id)
            && self.within_door_use_range(cx, &door, sample)
        {
            let mut driver = self.driver.take().expect("driving");
            self.apply(cx, &driver.cancel());
            self.retire(active, driver);
            self.open_door(cx, active, door, sample.body.radius);
            return;
        }
        if active.shuffles > 0
            && horizontal_distance(sample.position, active.shuffled_at) >= SHUFFLE_PROGRESS_METERS
        {
            active.shuffles = 0;
        }
        if self.tick % CROWD_CHECK_TICKS == 0
            && !self.driver.as_ref().expect("driving").is_leaping()
            && self.routing.is_none()
            && self.seconds - active.detoured_at >= DETOUR_INTERVAL_SECONDS
            && self.crowd_stepped_onto(cx, active, self.driver.as_ref().expect("driving"), sample)
        {
            self.start_detour(cx, active, sample);
        }
        let was_leaping = self.driver.as_ref().expect("driving").is_leaping();
        let drive_sample = RouteDriveSample {
            position: sample.position,
            heading_degrees: sample.heading_degrees,
            moves: sample.moves,
            in_portal_space: sample.in_portal_space,
            airborne: sample.airborne,
            turning: sample.turning,
            still: !self.still_since.is_nan() && self.seconds - self.still_since >= STILL_SECONDS,
            walk_stop_meters: sample.walk_stop_meters,
            run_stop_meters: sample.run_stop_meters,
        };
        let (step, mut driver) = self.advance_driver(&drive_sample);
        if !self.apply(cx, &step) {
            self.apply(cx, &driver.cancel());
            self.retire(active, driver);
            self.end(cx, active, WalkState::Stopped, "the client refused a move");
            return;
        }
        if step.jump.is_some() {
            self.narrate_jump(active, &driver);
        }
        // Said once for each leap.
        if driver.is_leaping() && !was_leaping && active.announced_leap != driver.leg_index() {
            active.announced_leap = driver.leg_index();
            let takeoff = driver.legs()[(driver.leg_index() - 1) as usize];
            let landing = driver.legs()[driver.leg_index() as usize];
            self.say(&format!(
                "Walk to {}: leaping from {} m to {} m, {} m on",
                label(active),
                fixed(takeoff.z, 1),
                fixed(landing.z, 1),
                fixed(horizontal_distance(takeoff, landing), 1)
            ));
        } else if was_leaping
            && !driver.is_leaping()
            && driver.leap_flew
            && driver.state() == RouteDriveState::Driving
        {
            self.say(&format!(
                "Walk to {}: the leap landed at {} m, {} m from where it was planned, after sliding {} m",
                label(active),
                fixed(sample.position.z, 1),
                fixed(driver.landing_error, 1),
                fixed(driver.landing_slide, 1)
            ));
        }
        // Only a leap that came down has a landing to measure.
        if was_leaping
            && !driver.is_leaping()
            && driver.leap_landed()
            && !sample.airborne
            && driver.state() != RouteDriveState::Interrupted
        {
            self.narrate_landing(active, &driver);
        }

        match driver.state() {
            RouteDriveState::Driving => {
                let left = remaining(active, &driver, sample.position);
                self.driver = Some(driver);
                self.publish(active, WalkState::Walking, "walking", left);
            }
            RouteDriveState::Arrived if active.recovering => {
                active.recovering = false;
                self.retire(active, driver);
                active.builds = 0;
                self.publish(
                    active,
                    WalkState::Planning,
                    "at a roomier spot; planning again",
                    f32::NAN,
                );
            }
            RouteDriveState::Arrived if active.staged => {
                self.retire(active, driver);
                self.next_stage(cx, active, sample);
            }
            RouteDriveState::Arrived => {
                self.retire(active, driver);
                if active.place.is_none() && !active.onto {
                    let goal = self.locate(cx, active);
                    self.face(cx, goal, sample);
                }
                if !active.ends_in_sight {
                    let why = active.arrival_reason.clone().unwrap_or_else(|| {
                        "no spot the character could reach can see the goal".to_owned()
                    });
                    self.end(cx, active, WalkState::ArrivedWithoutSight, &why);
                } else {
                    let why = match &active.arrival_reason {
                        Some(why) => format!("arrived; {why}"),
                        None => "arrived".to_owned(),
                    };
                    self.end(cx, active, WalkState::Arrived, &why);
                }
            }
            RouteDriveState::Blocked => {
                self.driver = Some(driver);
                self.blocked(cx, active, sample);
            }
            RouteDriveState::Interrupted => {
                self.retire(active, driver);
                self.end(
                    cx,
                    active,
                    WalkState::Interrupted,
                    "the player moved the character",
                );
            }
            RouteDriveState::LandedElsewhere => {
                let elsewhere = format!(
                    "the leap landed at {} m, {} m from where it was planned, after sliding {} m; planning on from there",
                    fixed(sample.position.z, 1),
                    fixed(driver.landing_error, 1),
                    fixed(driver.landing_slide, 1)
                );
                self.retire(active, driver);
                active.builds = 0;
                self.publish(active, WalkState::Planning, &elsewhere, f32::NAN);
                self.say(&format!("Walk to {}: {elsewhere}", label(active)));
            }
            RouteDriveState::Lost => {
                self.retire(active, driver);
                self.end(
                    cx,
                    active,
                    WalkState::Lost,
                    "the character entered portal space",
                );
            }
        }
    }

    fn narrate_jump(&self, active: &Request, driver: &RouteDriver) {
        if !self.narrating() {
            return;
        }
        let approach = driver.approach();
        let moves = match approach.moves_begun {
            0 => "from where it stood".to_owned(),
            1 => format!(
                "after one {} begun at {}",
                pace_name(approach.pace),
                spot(approach.begun_at)
            ),
            n => format!(
                "after {n} {} moves, the last begun at {}",
                pace_name(approach.pace),
                spot(approach.begun_at)
            ),
        };
        let let_go = if approach.let_go_meters.is_nan() {
            String::new()
        } else {
            format!(
                ", let go {} m short at {}",
                fixed(approach.let_go_meters, 2),
                spot(approach.let_go_at)
            )
        };
        let turned = if approach.turns == 0 {
            ", no turn".to_owned()
        } else {
            format!(
                ", {} turn{} through {} degrees from {}",
                approach.turns,
                if approach.turns == 1 { "" } else { "s" },
                fixed(approach.turned_degrees, 0),
                spot(approach.turned_at)
            )
        };
        let aimed = match approach.aimed {
            Some(aim) => format!(
                "; aimed from here at {} power {}, planned {} {}",
                fixed(aim.power, 2),
                if aim.run { "running" } else { "walking" },
                fixed(approach.planned_power, 2),
                if approach.planned_run {
                    "running"
                } else {
                    "walking"
                }
            ),
            None => format!(
                "; no leap from here is kept ({}), so flown as planned at {} power",
                self.aim_refused
                    .borrow()
                    .clone()
                    .unwrap_or_else(|| "not aimed".to_owned()),
                fixed(approach.planned_power, 2)
            ),
        };
        let skipped = if approach.walk_skipped > 0.0 {
            format!(
                "; leapt from where it stood rather than walk {} m to the planned takeoff",
                fixed(approach.walk_skipped, 1)
            )
        } else {
            String::new()
        };
        let adjusted = if approach.adjustments == 0 {
            String::new()
        } else {
            format!(
                "; went back to the takeoff {} time{} first",
                approach.adjustments,
                if approach.adjustments == 1 { "" } else { "s" }
            )
        };
        self.detail(&format!(
            "Walk to {}: charging the jump {} m from its takeoff {} at {}, {moves}{let_go}{turned}{aimed}{skipped}{adjusted}",
            label(active),
            fixed(approach.takeoff_error, 2),
            spot(approach.takeoff_at),
            spot(approach.charged_at)
        ));
    }

    fn narrate_landing(&self, active: &Request, driver: &RouteDriver) {
        if !self.narrating() {
            return;
        }
        self.detail(&format!(
            "Walk to {}: the leap came to rest {} m {} its landing and {} m to its {}, {} m above it; it came down {} m from where it charged, of {} m to its landing, flying {} degrees off the line to it after charging {} degrees off it, then sliding {} m {} degrees off that line; it rose {} m at its highest, where a jump at {} power rises {}; it came down at {}{}",
            label(active),
            fixed(driver.landing_long.abs(), 2),
            if driver.landing_long < 0.0 { "short of" } else { "past" },
            fixed(driver.landing_aside.abs(), 2),
            if driver.landing_aside < 0.0 { "left" } else { "right" },
            signed_two_decimals(driver.landing_rise),
            fixed(driver.landing_flown, 2),
            fixed(driver.landing_planned, 2),
            degrees(driver.landing_flight_degrees),
            degrees(driver.approach().facing_error),
            fixed(driver.landing_slide, 2),
            degrees(driver.landing_slide_degrees),
            fixed(driver.landing_peak, 2),
            fixed(flown(&driver.approach()), 2),
            self.planned_rise(&driver.approach()),
            point(driver.landing_touchdown),
            self.modelled_touchdown(driver)
        ));
    }

    /// Where the planner's own flight puts the leap just flown down and at
    /// rest, for narration.
    fn modelled_touchdown(&self, driver: &RouteDriver) -> String {
        let approach = driver.approach();
        let landing = approach.leg + 1;
        let (Some(grid), Some(ability)) = (&self.grid, self.leap_ability) else {
            return String::new();
        };
        if landing < 1 || landing as usize >= driver.legs().len() {
            return String::new();
        }
        let end = approach.flown_at.unwrap_or(driver.legs()[landing as usize]);
        let run = approach.aimed.map_or(approach.planned_run, |aim| aim.run);
        self.with_aim_finder(grid, ability, |finder| {
            match finder.follow(approach.charged_from, end, flown(&approach), run) {
                Some((came_down, rests)) => format!(
                    ", where the planner's flight comes down at {} and comes to rest at {}",
                    point(came_down),
                    point(rests)
                ),
                None => ", where the planner's flight comes down nowhere".to_owned(),
            }
        })
    }

    /// How high the body's own jump at the power a leap was flown at lifts
    /// it, as the planner takes it.
    fn planned_rise(&self, approach: &LeapApproach) -> String {
        let Some(ability) = self.leap_ability else {
            return "unknown".to_owned();
        };
        let height = ability.jump_height(flown(approach));
        let launch = (2.0 * NavLeapPhysics::GRAVITY * height).sqrt();
        let peak = height + launch * ability.physics.step_seconds.max(0.0);
        format!(
            "{} m, {} m with its first step, of {} m at full power",
            fixed(height, 2),
            fixed(peak, 2),
            fixed(ability.full_jump_height, 2)
        )
    }

    /// Opens a closed door beside the spot ahead of a character that stopped
    /// making progress. Otherwise keeps the next plans out of that spot,
    /// notes what stands beside it, and plans again or gives up.
    fn blocked(&mut self, cx: &mut WalkWorld<'_>, active: &mut Request, sample: &WalkBodySample) {
        active.recovering = false;
        if self.narrating() {
            self.narrate_stuck(active, sample);
        }
        let radians = sample.heading_degrees * (std::f32::consts::PI / 180.0);
        let ahead =
            sample.position + Vec3::new(radians.sin(), radians.cos(), 0.0) * BLOCKED_SPOT_AHEAD;
        active.blocked_by = cx.goals.try_find_blocker(ahead, BLOCKER_SEARCH_RADIUS);
        if cx.doors.is_some()
            && let Some(door) = active.blocked_by.clone().filter(|b| b.is_closed_door)
        {
            if let Some(driver) = self.driver.take() {
                self.retire(active, driver);
            }
            self.open_door(
                cx,
                active,
                NavDoor {
                    object_id: door.object_id,
                    name: door.name,
                    position: door.position,
                    radius: door.radius,
                },
                sample.body.radius,
            );
            return;
        }
        if let Some(mover) = active.blocked_by.clone().filter(|b| b.moves) {
            if active.shuffles < MAXIMUM_SHUFFLES && !mover.hostile {
                active.shuffles += 1;
                active.shuffled_at = sample.position;
                active.shuffle_until = self.seconds + SHUFFLE_WAIT_SECONDS;
                active.builds = 0;
                if let Some(driver) = self.driver.take() {
                    self.retire(active, driver);
                }
                let waiting = format!(
                    "{} (0x{:08X}) stands in the way; waiting for it to move ({} of {})",
                    mover.name, mover.object_id, active.shuffles, MAXIMUM_SHUFFLES
                );
                self.publish(active, WalkState::Walking, &waiting, f32::NAN);
                self.say(&format!("Walk to {}: {waiting}", label(active)));
                return;
            }
            if mover.radius > 0.0 {
                active.passing_avoid.push(NavAvoidance::new(
                    mover.position,
                    mover.radius + sample.body.radius,
                ));
            }
        } else {
            if let Some(solid) = active.blocked_by.clone().filter(|b| b.radius > 0.0) {
                active.avoid.push(NavAvoidance::new(
                    solid.position,
                    solid.radius + sample.body.radius,
                ));
                active.avoided_object = Some(solid);
            }
            // Only the first stops keep later plans out of the spot ahead: a
            // body that keeps stopping in one place would otherwise ring
            // itself in, as on the only stairs down.
            if active.replans < AVOIDED_STOPS {
                active
                    .stuck_spots
                    .push(NavAvoidance::new(ahead, BLOCKED_SPOT_RADIUS));
            }
        }
        if active.replans >= MAXIMUM_REPLANS {
            if let Some(driver) = self.driver.take() {
                self.retire(active, driver);
            }
            let why = blocked_reason(active);
            self.end(cx, active, WalkState::Blocked, &why);
            return;
        }

        // Planning again from the very spot a body is pressed against
        // something rarely frees it, so each stop after the first tries a
        // different way off it first.
        let attempt = active.recoveries;
        active.recoveries += 1;
        active.replans += 1;
        active.builds = 0;
        if let Some(driver) = self.driver.take() {
            self.retire(active, driver);
        }
        let mut again = format!(
            "{}; planning again ({} of {})",
            blocked_reason(active),
            active.replans,
            MAXIMUM_REPLANS
        );
        if let Some(recovery) = self.recover(cx, active, sample, attempt) {
            again.push_str(&format!(", {recovery} first"));
        }
        self.publish(active, WalkState::Planning, &again, f32::NAN);
        self.say(&format!("Walk to {}: {again}", label(active)));
    }

    /// Tries a way off whatever a stopped body met, a different one at each
    /// stop: none at the first, then stepping back, moving to the roomiest
    /// spot nearby, sidestepping toward the more open side, sidestepping the
    /// other way, and hopping forward, and round those again for a follow
    /// that keeps stopping. Says what it tried.
    fn recover(
        &mut self,
        cx: &mut WalkWorld<'_>,
        active: &mut Request,
        sample: &WalkBodySample,
        attempt: i32,
    ) -> Option<String> {
        let way = if attempt == 0 {
            0
        } else {
            (attempt - 1) % (MAXIMUM_REPLANS - 1) + 1
        };
        match way {
            1 => self.moves(
                cx,
                active,
                MoveDirection::Backward,
                format!("stepping back {} m", at_most_one_decimal(RECOVERY_METERS)),
            ),
            2 => {
                let Some(roomier) = self.try_find_roomier_spot(sample) else {
                    return Some("finding no roomier spot nearby".to_owned());
                };
                active.recovering = true;
                let driver =
                    RouteDriver::new(&[sample.position, roomier], &[], false).expect("two legs");
                self.drive_with(active, driver);
                Some(format!(
                    "moving to the roomiest spot nearby at {}",
                    point(roomier)
                ))
            }
            3 | 4 => {
                let open_left = self.room_toward(sample, true) >= self.room_toward(sample, false);
                let left = if way == 3 { open_left } else { !open_left };
                self.moves(
                    cx,
                    active,
                    if left {
                        MoveDirection::StrafeLeft
                    } else {
                        MoveDirection::StrafeRight
                    },
                    format!(
                        "sidestepping {} m to the {}{}",
                        at_most_one_decimal(RECOVERY_METERS),
                        if left { "left" } else { "right" },
                        if way == 3 { ", the more open side" } else { "" }
                    ),
                )
            }
            5 => {
                if !cx.body.begin_jump(HOP_POWER, Some(MovePace::Walk)) {
                    return None;
                }
                active.shuffle_until = self.seconds + RECOVERY_SECONDS;
                Some("hopping forward".to_owned())
            }
            _ => None,
        }
    }

    fn moves(
        &self,
        cx: &mut WalkWorld<'_>,
        active: &mut Request,
        direction: MoveDirection,
        what: String,
    ) -> Option<String> {
        if !cx
            .body
            .begin_move(MoveRequest::new(direction, MovePace::Walk, RECOVERY_METERS))
        {
            return None;
        }
        active.shuffle_until = self.seconds + RECOVERY_SECONDS;
        Some(what)
    }

    /// How far the nearest wall stands from the floor a step to one side of
    /// the body, or zero with no floor there.
    fn room_toward(&self, sample: &WalkBodySample, left: bool) -> f32 {
        let Some(grid) = &self.grid else {
            return 0.0;
        };
        let radians = sample.heading_degrees * (std::f32::consts::PI / 180.0);
        let side = Vec3::new(radians.cos(), -radians.sin(), 0.0)
            * if left {
                -RECOVERY_METERS
            } else {
                RECOVERY_METERS
            };
        let node = grid.find_standing_node(sample.position + side, 0.5, START_HEIGHT_TOLERANCE);
        if node >= 0 && grid.is_clear(node) {
            grid.wall_distance(node).min(100.0)
        } else {
            0.0
        }
    }

    /// The clear floor within [`ROOMIER_SPOT_REACH`] a body walks straight
    /// to that stands farthest from walls, when it has clearly more room
    /// than where the body stands.
    fn try_find_roomier_spot(&self, sample: &WalkBodySample) -> Option<Vec3> {
        let grid = self.grid.as_ref()?;
        let here = grid.find_standing_node(sample.position, 0.5, START_HEIGHT_TOLERANCE);
        if here < 0 {
            return None;
        }
        let room = grid.wall_distance(here).min(100.0);
        let mut best = room + ROOMIER_SPOT_MARGIN;
        let mut found = Vec3::ZERO;
        let reach = (ROOMIER_SPOT_REACH / grid.cell_size()).ceil() as i32;
        let (cx, cy) = grid.column_of(here);
        for y in cy - reach..=cy + reach {
            for x in cx - reach..=cx + reach {
                let (first, count) = grid.nodes_in_column(x, y);
                for node in first..first + count {
                    let at = grid.position(node);
                    let wall = grid.wall_distance(node).min(100.0);
                    if !grid.is_clear(node)
                        || wall <= best
                        || (at.z - sample.position.z).abs() > START_HEIGHT_TOLERANCE
                        || horizontal_distance(at, sample.position) > ROOMIER_SPOT_REACH
                        || horizontal_distance(at, sample.position) < ROOMIER_SPOT_LEAST
                        || !grid.can_walk_straight(here, node)
                    {
                        continue;
                    }
                    best = wall;
                    found = at;
                }
            }
        }
        (best > room + ROOMIER_SPOT_MARGIN).then_some(found)
    }

    /// Narrates where a walk stopped making progress.
    fn narrate_stuck(&self, active: &Request, sample: &WalkBodySample) {
        let leg = match &self.driver {
            Some(driver) if (driver.leg_index() as usize) < driver.legs().len() => {
                let end = driver.legs()[driver.leg_index() as usize];
                format!(
                    "leg {} of {} toward {}, {} m away",
                    driver.leg_index(),
                    driver.legs().len() - 1,
                    point(end),
                    fixed(sample.position.distance(end), 1)
                )
            }
            _ => "no leg".to_owned(),
        };
        let mut walls = "no floor the grid knows under it".to_owned();
        if let Some(grid) = &self.grid
            && grid.contains(sample.position, 0.0)
        {
            let node = grid.find_standing_node(sample.position, 0.5, START_HEIGHT_TOLERANCE);
            if node >= 0 {
                walls = format!(
                    "nearest wall {} m from the floor at {}, {}, body radius {} m",
                    fixed(grid.wall_distance(node), 2),
                    point(grid.position(node)),
                    if grid.is_clear(node) {
                        "clear"
                    } else {
                        "too near a wall or ledge to be clear"
                    },
                    fixed(sample.body.radius, 2)
                );
            }
        }
        self.detail(&format!(
            "Walk to {}: stopped making progress at {}, heading {}°, on {leg}; {walls}",
            label(active),
            point(sample.position),
            fixed(sample.heading_degrees, 0)
        ));
    }

    /// Stops a walk where the character stands while something else needs
    /// the character, and plans again from there once nothing has needed it
    /// for [`PAUSE_SETTLE_SECONDS`].
    fn paused(
        &mut self,
        cx: &mut WalkWorld<'_>,
        active: &mut Request,
        sample: &WalkBodySample,
    ) -> bool {
        if !active.walk || self.driver.as_ref().is_some_and(RouteDriver::is_leaping) {
            return false;
        }
        let need = cx.paused_by.and_then(|paused_by| paused_by());
        if need.is_none() && active.paused_for.is_none() {
            return false;
        }
        if sample.in_portal_space {
            self.end(
                cx,
                active,
                WalkState::Lost,
                "the character entered portal space",
            );
            return true;
        }
        if let Some(need) = need {
            active.free_since = None;
            if let Some(mut driver) = self.driver.take() {
                self.apply(cx, &driver.cancel());
                self.retire(active, driver);
            }
            if self.routing_for == Some(active.sequence) {
                self.routing_for = None;
            }
            if let Some(opening) = &active.waiting_on
                && !opening.appraising
                && let Some(uses) = active.door_uses.get_mut(&opening.door.object_id)
            {
                *uses -= 1;
            }
            active.waiting_on = None;
            if active.paused_for.as_deref() != Some(need.as_str()) {
                let waiting = format!("waiting: {need}");
                active.paused_for = Some(need);
                self.publish(active, WalkState::Waiting, &waiting, f32::NAN);
                self.say(&format!("Walk to {}: {waiting}", label(active)));
            }
            return true;
        }
        let free_since = *active.free_since.get_or_insert(self.seconds);
        if self.seconds - free_since < PAUSE_SETTLE_SECONDS {
            return true;
        }
        active.paused_for = None;
        active.free_since = None;
        active.builds = 0;
        const AGAIN: &str =
            "nothing needs the character any more; planning on from where it stands";
        self.publish(active, WalkState::Planning, AGAIN, f32::NAN);
        self.say(&format!("Walk to {}: {AGAIN}", label(active)));
        true
    }

    /// A closed door within the look-ahead along the leg the character is
    /// walking.
    fn closed_door_ahead(&self, cx: &WalkWorld<'_>, sample: &WalkBodySample) -> Option<NavDoor> {
        let doors = cx.doors.as_deref()?;
        let driver = self.driver.as_ref()?;
        if driver.leg_index() as usize >= driver.legs().len() {
            return None;
        }
        let from = flat(sample.position);
        let end = driver.legs()[driver.leg_index() as usize];
        let toward = flat(end) - from;
        let length = toward.length();
        if length < 1e-3 {
            return None;
        }
        let ahead = from + toward * (length.min(DOOR_LOOK_AHEAD_METERS) / length);
        doors.try_find_closed_door(sample.position, ahead.extend(end.z), DOOR_CORRIDOR_METERS)
    }

    /// Whether the character stands near enough a door for the client to use
    /// it where it stands.
    fn within_door_use_range(
        &self,
        cx: &WalkWorld<'_>,
        door: &NavDoor,
        sample: &WalkBodySample,
    ) -> bool {
        let use_reach = cx
            .doors
            .as_deref()
            .map_or(0.0, |doors| doors.use_reach(door.object_id));
        let reach = use_reach.max(if door.radius > 0.0 {
            door.radius + sample.body.radius + DOOR_USE_MARGIN
        } else {
            0.0
        });
        !(reach > 0.0) || horizontal_distance(sample.position, door.position) <= reach
    }

    /// Has the client open a closed door the walk stopped at and waits for
    /// it to open. A door never appraised is appraised first, and one that is
    /// locked, or keeps closing, is planned around.
    fn open_door(
        &mut self,
        cx: &mut WalkWorld<'_>,
        active: &mut Request,
        door: NavDoor,
        body_radius: f32,
    ) {
        let Some(doors) = cx.doors.as_deref_mut() else {
            return;
        };
        let locked = doors.is_locked(door.object_id);
        if locked.is_none()
            && active.appraised_doors.insert(door.object_id)
            && doors.appraise(door.object_id)
        {
            let checking = format!(
                "checking whether {} (0x{:08X}) is locked",
                door.name, door.object_id
            );
            active.waiting_on = Some(DoorWait {
                door,
                use_at_tick: self.tick,
                used: false,
                deadline_seconds: self.seconds + DOOR_APPRAISAL_WAIT_SECONDS,
                appraising: true,
            });
            self.publish(active, WalkState::Walking, &checking, f32::NAN);
            self.say(&format!("Walk to {}: {checking}", label(active)));
            return;
        }
        if locked == Some(true) {
            self.avoid_door(cx, active, door, body_radius, "a locked door");
            return;
        }
        let uses = active.door_uses.get(&door.object_id).copied().unwrap_or(0);
        if uses >= MAXIMUM_DOOR_USES {
            self.avoid_door(cx, active, door, body_radius, "a door kept closing");
            return;
        }
        active.door_uses.insert(door.object_id, uses + 1);
        let opening = format!("opening {} (0x{:08X})", door.name, door.object_id);
        active.waiting_on = Some(DoorWait {
            door,
            use_at_tick: self.tick + DOOR_USE_SETTLE_TICKS,
            used: false,
            deadline_seconds: 0.0,
            appraising: false,
        });
        self.publish(active, WalkState::Walking, &opening, f32::NAN);
        self.say(&format!("Walk to {}: {opening}", label(active)));
    }

    /// Uses the door once the character's stop has landed, and plans again
    /// once the door is open.
    fn wait_for_door(
        &mut self,
        cx: &mut WalkWorld<'_>,
        active: &mut Request,
        wait: DoorWait,
        sample: &WalkBodySample,
    ) {
        if sample.in_portal_space {
            self.end(
                cx,
                active,
                WalkState::Lost,
                "the character entered portal space",
            );
            return;
        }
        let Some(doors) = cx.doors.as_deref_mut() else {
            active.waiting_on = None;
            return;
        };
        if doors.is_open(wait.door.object_id) {
            active.waiting_on = None;
            active.builds = 0;
            let open = format!("{} is open, planning again", wait.door.name);
            self.publish(active, WalkState::Planning, &open, f32::NAN);
            return;
        }
        if wait.appraising {
            if doors.is_locked(wait.door.object_id).is_none()
                && self.seconds < wait.deadline_seconds
            {
                return;
            }
            active.waiting_on = None;
            self.open_door(cx, active, wait.door, sample.body.radius);
            return;
        }
        if !wait.used {
            if self.tick < wait.use_at_tick {
                return;
            }
            doors.use_door(wait.door.object_id);
            active.waiting_on = Some(DoorWait {
                used: true,
                deadline_seconds: self.seconds + DOOR_OPEN_WAIT_SECONDS,
                ..wait
            });
            return;
        }
        if self.seconds >= wait.deadline_seconds {
            self.avoid_door(
                cx,
                active,
                wait.door,
                sample.body.radius,
                "a closed door would not open",
            );
        }
    }

    /// Keeps the walk's later plans out of a door that is locked or would
    /// not open, widened by the body's radius, and plans again. A door whose
    /// footprint is not known, or that the walk already kept out of, ends the
    /// walk blocked.
    fn avoid_door(
        &mut self,
        cx: &mut WalkWorld<'_>,
        active: &mut Request,
        door: NavDoor,
        body_radius: f32,
        why: &str,
    ) {
        active.waiting_on = None;
        if door.radius <= 0.0 || !active.avoided_doors.insert(door.object_id) {
            self.blocked_at_door(cx, active, &door, why);
            return;
        }
        active
            .avoid
            .push(NavAvoidance::new(door.position, door.radius + body_radius));
        let around = format!(
            "{why}: {} (0x{:08X}); planning a way around it",
            door.name, door.object_id
        );
        active.avoided_door = Some((door, why.to_owned()));
        active.builds = 0;
        if let Some(driver) = self.driver.take() {
            self.retire(active, driver);
        }
        self.publish(active, WalkState::Planning, &around, f32::NAN);
        self.say(&format!("Walk to {}: {around}", label(active)));
    }

    fn blocked_at_door(
        &mut self,
        cx: &mut WalkWorld<'_>,
        active: &mut Request,
        door: &NavDoor,
        why: &str,
    ) {
        active.waiting_on = None;
        active.blocked_by = Some(NavBlocker {
            object_id: door.object_id,
            name: door.name.clone(),
            is_closed_door: true,
            ..NavBlocker::default()
        });
        let reason = format!("{why}: {} (0x{:08X})", door.name, door.object_id);
        self.end(cx, active, WalkState::Blocked, &reason);
    }

    /// Plans the next stage of a walk from where its last stage ended, or
    /// gives up after too many.
    fn next_stage(
        &mut self,
        cx: &mut WalkWorld<'_>,
        active: &mut Request,
        sample: &WalkBodySample,
    ) {
        active.staged = false;
        active.builds = 0;
        active.stages += 1;
        active.goal = self.locate(cx, active);
        self.goal = Some((active.goal, active.arrival_meters));
        let away = horizontal_distance(sample.position, active.goal);
        if active.stages >= MAXIMUM_STAGES {
            let why = format!(
                "the goal was still {} m away after {} stages",
                fixed(away, 0),
                active.stages
            );
            self.end(cx, active, WalkState::NoRoute, &why);
            return;
        }
        active.built_for_objects = false;
        active.rebuild_why = None;
        let next = format!(
            "stage {} walked; planning the next toward the goal, {} m away",
            active.stages,
            fixed(away, 0)
        );
        self.publish(active, WalkState::Planning, &next, f32::NAN);
        self.say(&format!("Walk to {}: {next}", label(active)));
    }

    #[allow(clippy::too_many_arguments)]
    fn build_grid(
        &mut self,
        cx: &mut WalkWorld<'_>,
        active: &mut Request,
        sample: &WalkBodySample,
        origin_x: f32,
        origin_y: f32,
        size: f32,
        dungeon: u32,
    ) {
        if active.builds >= MAXIMUM_BUILDS_PER_PLAN {
            self.end(
                cx,
                active,
                WalkState::NoRoute,
                "no grid covers both the character and the goal",
            );
            return;
        }
        active.builds += 1;
        let why = active
            .rebuild_why
            .clone()
            .unwrap_or_else(|| "a walk needs one over its route".to_owned());
        if !self.start_build(
            cx,
            origin_x,
            origin_y,
            size,
            sample.body,
            dungeon,
            why,
            true,
        ) {
            self.end(
                cx,
                active,
                WalkState::NoRoute,
                "no collision is loaded around the character",
            );
        }
    }

    /// The horizontal extent of every cell of the sealed dungeon a cell lies
    /// in, or `None` outside one or before its cells are resident.
    fn try_measure_dungeon(&mut self, cx: &WalkWorld<'_>, cell_id: u32) -> Option<(Vec2, Vec2)> {
        let is_sealed = cx.is_sealed_dungeon?;
        if cell_id == 0 {
            return None;
        }
        if cell_id != self.classified_cell_id {
            self.classified_cell_id = cell_id;
            self.classified_sealed = is_sealed(cell_id);
        }
        if !self.classified_sealed {
            return None;
        }
        geometry::measure_cells(cx.physics, (cell_id & 0xFFFF_0000) | 0xFFFF)
    }

    /// The objects the server placed on a grid, each widened by the body's
    /// radius, for a route to keep out of.
    fn obstacles(
        &self,
        cx: &WalkWorld<'_>,
        grid: &NavGrid,
        body: NavBody,
        goal_object_id: u32,
    ) -> Vec<NavAvoidance> {
        let half = grid.size() * 0.5;
        let centre = Vec3::new(grid.origin_x() + half, grid.origin_y() + half, 0.0);
        cx.goals
            .find_obstacles(
                centre,
                half * std::f32::consts::SQRT_2,
                goal_object_id,
                Some(grid.object_ids()),
            )
            .into_iter()
            .map(|found| NavAvoidance::new(found.centre, found.radius + body.radius))
            .collect()
    }

    /// The portals on a grid other than the one the walk goes to, each
    /// widened by the body's radius.
    fn portals(
        &self,
        cx: &WalkWorld<'_>,
        grid: &NavGrid,
        body: NavBody,
        goal_object_id: u32,
        goal: Vec3,
    ) -> Vec<NavAvoidance> {
        let half = grid.size() * 0.5;
        let centre = Vec3::new(grid.origin_x() + half, grid.origin_y() + half, 0.0);
        cx.goals
            .find_portals(
                centre,
                half * std::f32::consts::SQRT_2,
                goal_object_id,
                goal,
            )
            .into_iter()
            .map(|found| NavAvoidance::new(found.centre, found.radius + body.radius))
            .collect()
    }

    fn apply(&self, cx: &mut WalkWorld<'_>, step: &RouteDriveStep) -> bool {
        if step.stop_travel {
            cx.body.stop_move(MoveChannel::Travel);
        }
        if step.stop_turn {
            cx.body.stop_move(MoveChannel::Turn);
        }
        let mut accepted = true;
        if let Some(travel) = step.travel {
            accepted &= cx.body.begin_move(travel);
        }
        if let Some(turn) = step.turn {
            accepted &= cx.body.begin_move(turn);
        }
        if let Some(power) = step.jump {
            accepted &= cx.body.begin_jump(power, step.jump_pace);
        }
        accepted
    }

    fn face(&self, cx: &mut WalkWorld<'_>, goal: Vec3, sample: &WalkBodySample) {
        let dx = goal.x - sample.position.x;
        let dy = goal.y - sample.position.y;
        if dx * dx + dy * dy < 0.01 {
            return;
        }
        let heading = dx.atan2(dy) * (180.0 / std::f32::consts::PI);
        let error = signed_degrees(heading - sample.heading_degrees);
        if error.abs() <= FACE_TOLERANCE_DEGREES {
            return;
        }
        cx.body.begin_move(MoveRequest::new(
            if error > 0.0 {
                MoveDirection::TurnRight
            } else {
                MoveDirection::TurnLeft
            },
            MovePace::Run,
            error.abs(),
        ));
    }

    fn cancel(&mut self, cx: &mut WalkWorld<'_>, reason: &str) {
        let Some(mut active) = self.active.take() else {
            return;
        };
        if let Some(mut driver) = self.driver.take() {
            self.apply(cx, &driver.cancel());
            self.retire(&mut active, driver);
        }
        self.end(cx, &mut active, WalkState::Stopped, reason);
        if !active.ended {
            self.active = Some(active);
        }
    }

    /// Hands a request a new drive, counting it among its drives.
    fn drive_with(&mut self, request: &mut Request, driver: RouteDriver) {
        if let Some(old) = self.driver.take() {
            self.retire(request, old);
        }
        request.drives += 1;
        self.driver = Some(driver);
    }

    /// Lets a drive go, keeping the corners it took for the walk's summary.
    fn retire(&self, request: &mut Request, driver: RouteDriver) {
        request.corners_run_around += driver.corners_run_around;
        request.corners_turned_in_place += driver.corners_turned_in_place;
    }

    fn end(
        &mut self,
        cx: &mut WalkWorld<'_>,
        request: &mut Request,
        state: WalkState,
        reason: &str,
    ) {
        if request.follow
            && request.is_active
            && matches!(
                state,
                WalkState::Arrived
                    | WalkState::ArrivedWithoutSight
                    | WalkState::NoRoute
                    | WalkState::Blocked
            )
        {
            self.keep_following(cx, request, state, reason);
            return;
        }
        if request.is_active {
            request.is_active = false;
            request.ended = true;
            if let Some(driver) = self.driver.take() {
                self.retire(request, driver);
            }
        }
        request.ended = true;
        let remaining_meters = match (request.goal_known, cx.body.try_sample()) {
            (true, Some(sample)) => horizontal_distance(sample.position, self.locate(cx, request)),
            _ => f32::NAN,
        };
        self.publish(request, state, reason, remaining_meters);
        self.say(&format!(
            "{} to {}: {reason}",
            if request.walk { "Walk" } else { "Route" },
            label(request)
        ));
        if request.drives > 0 {
            self.say(&format!(
                "Walk to {}: ran around {} corners and turned in place at {}",
                label(request),
                request.corners_run_around,
                request.corners_turned_in_place
            ));
        }
    }

    fn publish(
        &mut self,
        request: &Request,
        state: WalkState,
        reason: &str,
        remaining_meters: f32,
    ) {
        if request.sequence < self.report.sequence {
            return;
        }
        self.report = WalkReport {
            sequence: request.sequence,
            state,
            object_id: request.object_id,
            remaining_meters,
            replans: request.replans,
            reason: reason.to_owned(),
            blocked_by_object_id: if state == WalkState::Blocked {
                request.blocked_by.as_ref().map_or(0, |b| b.object_id)
            } else {
                0
            },
        };
    }

    fn release_grid(&mut self) {
        self.grid = None;
        self.grid_dungeon = 0;
        self.grid_cells = None;
        self.grid_object_floors = None;
        self.grid_idle_since = f64::NAN;
        *self.aim_finder.borrow_mut() = None;
        if let Some(released) = &self.grid_released {
            released();
        }
    }

    /// Lets go of the grid kept between walks once nothing it was built over
    /// is loaded any more.
    fn release_unloaded_grid(&mut self, cx: &WalkWorld<'_>) {
        let Some(grid) = self.grid.clone() else {
            return;
        };
        if self.building.is_some() {
            return;
        }
        let unloaded = if self.grid_dungeon != 0 {
            self.is_stale(cx, &grid, self.grid_dungeon)
        } else {
            !grid
                .landblock_ids()
                .iter()
                .any(|&id| cx.physics.is_landblock_terrain_resident(id))
        };
        if unloaded {
            self.release_grid();
        }
    }

    /// Lets go of the grid once no walk has needed it for
    /// [`GRID_IDLE_SECONDS`].
    fn release_idle_grid(&mut self) {
        if self.grid.is_none() || self.building.is_some() || self.show_grid {
            self.grid_idle_since = f64::NAN;
            return;
        }
        if self.grid_idle_since.is_nan() {
            self.grid_idle_since = self.seconds;
            return;
        }
        if self.seconds - self.grid_idle_since < GRID_IDLE_SECONDS {
            return;
        }
        self.release_grid();
        self.say(&format!(
            "Navmesh: let the grid go after {} s without a walk",
            fixed(GRID_IDLE_SECONDS as f32, 0)
        ));
    }

    /// Whether a grid no longer stands for the world: the resident
    /// landblocks it was built from have changed, or the landblock of a grid
    /// over a whole sealed dungeon has left.
    fn is_stale(&self, cx: &WalkWorld<'_>, grid: &NavGrid, dungeon: u32) -> bool {
        if dungeon != 0 {
            return geometry::measure_cells(cx.physics, dungeon).is_none();
        }
        let resident = geometry::overlapping_landblocks(
            cx.physics,
            grid.origin_x(),
            grid.origin_y(),
            grid.size(),
        );
        if resident.len() != grid.landblock_ids().len() {
            return true;
        }
        resident.iter().any(|id| !grid.landblock_ids().contains(id))
    }

    /// Starts building a grid over a region, or over the whole of the sealed
    /// dungeon `dungeon` names when it is not zero.
    #[allow(clippy::too_many_arguments)]
    fn start_build(
        &mut self,
        cx: &WalkWorld<'_>,
        origin_x: f32,
        origin_y: f32,
        size: f32,
        body: NavBody,
        dungeon: u32,
        why: String,
        walk_waits: bool,
    ) -> bool {
        let stands_on = |id: u32| cx.goals.stands_still(id);
        let captured: Option<NavGeometry> = if dungeon == 0 {
            geometry::capture(cx.physics, origin_x, origin_y, size, Some(&stands_on))
        } else {
            geometry::capture_dungeon(
                cx.physics,
                dungeon,
                origin_x,
                origin_y,
                size,
                Some(&stands_on),
            )
        };
        let Some(captured) = captured else {
            return false;
        };
        self.share_build(captured, body, dungeon, why, walk_waits);
        true
    }

    /// Starts building a grid from geometry already captured, as the
    /// controller's own, which every walk, preview and reach search over the
    /// same ground can then wait on.
    fn share_build(
        &mut self,
        captured: NavGeometry,
        body: NavBody,
        dungeon: u32,
        why: String,
        walk_waits: bool,
    ) -> SharedBuild {
        let (origin_x, origin_y, size) = (captured.origin_x, captured.origin_y, captured.size);
        let landblock_ids = captured.landblock_ids.clone();
        let object_ids = Arc::new(captured.object_ids.clone());
        let building = GridBuilding {
            build: SharedBuild::start(self.mode, move || {
                NavGrid::build(&captured, body, DEFAULT_CELL_SIZE).map_err(|e| format!("{e:?}"))
            }),
            dungeon,
            why,
            body,
            origin_x,
            origin_y,
            size,
            landblock_ids,
            object_ids,
            walk_waits,
        };
        let build = building.build.clone();
        self.building = Some(building);
        build
    }

    /// Whether the objects a body meets in a grid's region have changed
    /// since it was built.
    fn objects_moved(&self, cx: &WalkWorld<'_>, grid: &NavGrid) -> bool {
        self.objects(cx, grid) != grid.object_fingerprint()
    }

    /// Whether the objects have changed and stayed changed from one look to
    /// the next.
    fn objects_moved_and_stayed(&mut self, cx: &WalkWorld<'_>, grid: &NavGrid) -> bool {
        let now = self.objects(cx, grid);
        let stayed = now != grid.object_fingerprint() && now == self.objects_last_seen;
        self.objects_last_seen = now;
        stayed
    }

    /// Whether the goal stands on or in an object whose collision the grid
    /// has none of, which is an object the server placed after the grid was
    /// built.
    fn goal_stands_on_what_the_grid_has_not(
        &self,
        cx: &WalkWorld<'_>,
        grid: &NavGrid,
        active: &Request,
        body: NavBody,
    ) -> bool {
        let goal = flat(active.goal);
        for entry in cx.physics.shadows.capture_entries() {
            if grid.object_ids().contains(&entry.entity_id)
                || !cx.goals.stands_still(entry.entity_id)
            {
                continue;
            }
            // Measured from the part's footprint, never from the position it
            // is registered at.
            let footprint = geometry::footprint_of(&entry, cx.physics.world());
            let from_goal = flat(footprint.centre).distance(goal);
            if from_goal > GOAL_OBJECT_REACH + footprint.radius {
                continue;
            }
            if from_goal <= footprint.radius + body.radius {
                return true;
            }
        }
        false
    }

    /// What the objects a body meets in a grid's region come to now.
    fn objects(&self, cx: &WalkWorld<'_>, grid: &NavGrid) -> u64 {
        let stands_on = |id: u32| cx.goals.stands_still(id);
        geometry::fingerprint_objects(
            cx.physics,
            grid.origin_x(),
            grid.origin_y(),
            grid.size(),
            Some(&stands_on),
        )
    }

    /// Keeps a grid around the character while it is shown: the whole
    /// dungeon inside a sealed one.
    fn keep_view_grid(&mut self, cx: &WalkWorld<'_>, sample: &WalkBodySample) {
        if self.building.is_some() || self.tick < self.view_retry_tick {
            return;
        }
        if let Some((minimum, maximum)) = self.try_measure_dungeon(cx, sample.cell_id) {
            let dungeon = (sample.cell_id & 0xFFFF_0000) | 0xFFFF;
            let why = match self.grid.clone() {
                Some(whole) if whole.body() == sample.body && self.grid_dungeon == dungeon => {
                    if self.is_stale(cx, &whole, dungeon) {
                        Some("the dungeon's landblock left")
                    } else if self.objects_moved_and_stayed(cx, &whole) {
                        Some("the objects in it changed")
                    } else {
                        None
                    }
                }
                _ => Some("there was none over this dungeon"),
            };
            let Some(why) = why else {
                return;
            };
            let at = flat(sample.position);
            let built = try_choose_square(minimum.min(at), maximum.max(at), MAXIMUM_DUNGEON_REGION)
                .is_some_and(|(x, y, size)| {
                    self.start_build(cx, x, y, size, sample.body, dungeon, why.to_owned(), false)
                });
            if !built {
                self.view_retry_tick = self.tick + VIEW_RETRY_TICKS;
            }
            return;
        }
        let why = match self.grid.clone() {
            Some(grid) if grid.body() == sample.body && self.grid_dungeon == 0 => {
                if grid.contains(sample.position, VIEW_REGION / 8.0) {
                    if self.objects_moved_and_stayed(cx, &grid) {
                        Some("the objects in it changed")
                    } else {
                        None
                    }
                } else {
                    Some("the character walked out of it")
                }
            }
            _ => Some("there was none around the character"),
        };
        let Some(why) = why else {
            return;
        };
        let half = VIEW_REGION * 0.5;
        if !self.start_build(
            cx,
            snap(sample.position.x - half),
            snap(sample.position.y - half),
            VIEW_REGION,
            sample.body,
            0,
            why.to_owned(),
            false,
        ) {
            self.view_retry_tick = self.tick + VIEW_RETRY_TICKS;
        }
    }

    fn collect_build(&mut self, cx: &mut WalkWorld<'_>) {
        if !self
            .building
            .as_ref()
            .is_some_and(|b| b.build.is_finished())
        {
            return;
        }
        let building = self.building.take().expect("finished");
        let grid = match building.build.wait() {
            Ok(grid) => grid,
            Err(message) => {
                self.say(&format!("Navmesh: the build failed: {message}"));
                // The cell surfaces a search over it copied go with it.
                self.grid_cells = None;
                self.grid_object_floors = None;
                if !building.walk_waits {
                    return;
                }
                if let Some(mut active) = self.active.take() {
                    let why = format!("the grid build failed: {message}");
                    self.end(cx, &mut active, WalkState::NoRoute, &why);
                    if !active.ended {
                        self.active = Some(active);
                    }
                }
                return;
            }
        };
        self.grid_dungeon = building.dungeon;
        let report = grid.report();
        self.say(&format!(
            "Navmesh: {} standing points ({} clear) over {} m from {} landblocks in {} ms, built because {}",
            report.nodes,
            report.clear_nodes,
            fixed(grid.size(), 0),
            grid.landblock_ids().len(),
            fixed(report.milliseconds as f32, 0),
            building.why
        ));
        self.grid = Some(grid);
    }
}

/// The power a leap was flown at: as aimed again where it was, else as
/// planned.
fn flown(approach: &LeapApproach) -> f32 {
    approach
        .aimed
        .map_or(approach.planned_power, |aim| aim.power)
}

/// Starts a follow's next plan afresh.
fn restart_follow(request: &mut Request) {
    request.settled = false;
    request.ground_wait_since = None;
    request.awaits_ground = false;
    request.builds = 0;
    request.replans = 0;
    request.stages = 0;
    request.avoid.clear();
    request.stuck_spots.clear();
    request.follow_retry_at = 0.0;
    request.follow_said = None;
}

impl WalkController {
    // ── Routes found ────────────────────────────────────────────────────

    fn collect_route(&mut self, cx: &mut WalkWorld<'_>) {
        let notes: Vec<String> =
            std::mem::take(&mut *self.search_notes.lock().expect("the search notes"));
        for note in notes {
            if let Some(noted) = &self.active {
                self.detail(&format!("Follow {}: {note}", label(noted)));
            }
        }
        if !self.routing.as_ref().is_some_and(Job::is_finished) {
            return;
        }
        let routing = self.routing.take().expect("finished");
        let requester_sequence = self.routing_for.take();
        let Some(mut requester) = self.active.take() else {
            return;
        };
        if requester_sequence == Some(requester.sequence) {
            self.take_route(cx, &mut requester, routing);
        }
        if !requester.ended {
            self.active = Some(requester);
        }
    }

    fn take_route(
        &mut self,
        cx: &mut WalkWorld<'_>,
        requester: &mut Request,
        routing: Job<NavRoute>,
    ) {
        if requester.detouring {
            requester.detouring = false;
            let driving = self
                .driver
                .as_ref()
                .is_some_and(|d| d.state() == RouteDriveState::Driving && !d.is_leaping());
            let Ok(detour) = routing.take() else {
                return;
            };
            let Some(now) = cx.body.try_sample() else {
                return;
            };
            if !driving || detour.outcome != NavRouteOutcome::Routed || detour.legs.len() < 2 {
                return;
            }
            let (onward, skipped) = onward_legs(&detour, now.position);
            let leaps: Vec<RouteLeap> = detour
                .leaps
                .iter()
                .filter(|leap| leap.leg_index - skipped as i32 >= 1)
                .map(|leap| RouteLeap {
                    leg_index: leap.leg_index - skipped as i32,
                    power: leap.power,
                    run: leap.run,
                })
                .collect();
            let driver = RouteDriver::new(&onward, &leaps, true).expect("a detour's legs");
            self.drive_with(requester, driver);
            if requester.follow {
                self.detail(&format!(
                    "Follow {}: planned again toward the player on the way, {} m",
                    label(requester),
                    fixed(detour.length, 1)
                ));
            } else {
                self.say(&format!(
                    "Walk to {}: planned a way around a creature or player on the route",
                    label(requester)
                ));
            }
            self.route = Some(detour);
            return;
        }
        let route = match routing.take() {
            Ok(route) => route,
            Err(message) => {
                let why = format!("the search failed: {message}");
                self.end(cx, requester, WalkState::NoRoute, &why);
                return;
            }
        };
        self.route = Some(route.clone());
        if route.outcome == NavRouteOutcome::Routed {
            requester.avoided_door = None;
            requester.avoided_object = None;
            let passed = passed_obstacles(&route, &requester.planned_obstacles);
            if passed > 0 {
                self.say(&format!(
                    "Route: no way around {passed} of the objects the server placed arrives, so the route passes them"
                ));
            }
        }
        let who = if requester.follow {
            "Follow"
        } else {
            "Walk to"
        };
        if route.outcome != NavRouteOutcome::Routed && !requester.stuck_spots.is_empty() {
            // Where the only way on passes the spot the character stuck at,
            // keeping out of it leaves no route: plan through it again.
            requester.stuck_spots.clear();
            const THROUGH: &str =
                "no route keeps out of where the character stuck; planning through it again";
            self.publish(requester, WalkState::Planning, THROUGH, f32::NAN);
            self.say(&format!("{who} {}: {THROUGH}", label(requester)));
            return;
        }
        if route.outcome != NavRouteOutcome::Routed
            && !requester.built_for_objects
            && let Some(searched) = self.grid.clone()
            && self.objects_moved(cx, &searched)
        {
            // The grid was built before the server had placed everything
            // here: build it again with what stands here now and search once
            // more.
            requester.built_for_objects = true;
            let why = "no route was found with the objects the grid had";
            requester.rebuild_why = Some(why.to_owned());
            self.grid = None;
            let again = format!("{why}; building it again with what stands here now");
            self.publish(requester, WalkState::Planning, &again, f32::NAN);
            self.say(&format!("{who} {}: {again}", label(requester)));
            return;
        }
        if route.outcome != NavRouteOutcome::Routed {
            if let Some((door, why)) = requester.avoided_door.clone() {
                requester.blocked_by = Some(NavBlocker {
                    object_id: door.object_id,
                    name: door.name.clone(),
                    is_closed_door: true,
                    ..NavBlocker::default()
                });
                let reason = format!(
                    "{why}: {} (0x{:08X}), and no other way around it was found",
                    door.name, door.object_id
                );
                self.end(cx, requester, WalkState::Blocked, &reason);
            } else if let Some(solid) = requester.avoided_object.clone() {
                let reason = format!(
                    "{} (0x{:08X}) stands in the way, and no other way around it was found",
                    solid.name, solid.object_id
                );
                requester.blocked_by = Some(solid);
                self.end(cx, requester, WalkState::Blocked, &reason);
            } else if requester.replans > 0 {
                let reason = format!(
                    "{}, and no other way around it was found",
                    blocked_reason(requester)
                );
                self.end(cx, requester, WalkState::Blocked, &reason);
            } else {
                if self.narrating() {
                    self.narrate_no_route(requester, &route);
                }
                self.end(cx, requester, WalkState::NoRoute, &route.reason);
            }
            return;
        }
        self.say(&format!(
            "Route: {} legs, {} m, {} expansions in {} ms",
            route.legs.len() - 1,
            fixed(route.length, 1),
            route.expansions,
            fixed(route.milliseconds as f32, 0)
        ));
        if self.narrating() {
            const SHOWN: usize = 24;
            let points: Vec<String> = route.legs.iter().take(SHOWN).map(|at| point(*at)).collect();
            let more = if route.legs.len() > SHOWN {
                format!(" and {} more", route.legs.len() - SHOWN)
            } else {
                String::new()
            };
            let leaps = if route.leaps.is_empty() {
                "no leaps".to_owned()
            } else {
                route
                    .leaps
                    .iter()
                    .map(|leap| {
                        format!(
                            "leg {} at {} power {}",
                            leap.leg_index,
                            fixed(leap.power, 2),
                            if leap.run { "running" } else { "walking" }
                        )
                    })
                    .collect::<Vec<_>>()
                    .join(", ")
            };
            self.detail(&format!(
                "Route points: {}{more}; {leaps}; {}",
                points.join(" -> "),
                if route.ends_in_sight {
                    "ends in sight of the goal"
                } else {
                    "ends out of sight of the goal"
                }
            ));
        }
        if requester.staged {
            let left = horizontal_distance(*route.legs.last().expect("routed"), requester.goal);
            let before = cx.body.try_sample().map_or(left, |here| {
                horizontal_distance(here.position, requester.goal)
            });
            if before - left < MINIMUM_STAGE_PROGRESS {
                // The stage ended at the edge of the loaded ground short of
                // its region's: the landblocks beyond are still loading, as
                // just after a portal. Plan again once they have.
                if let Some(grid) = &self.grid
                    && grid.distance_beyond_ground(requester.goal)
                        > grid.distance_beyond_region(requester.goal) + grid.cell_size()
                {
                    let since = *requester.ground_wait_since.get_or_insert(self.seconds);
                    if self.seconds - since < GROUND_WAIT_SECONDS {
                        if !requester.awaits_ground {
                            self.say(&format!("{who} {}: {WAITING_FOR_GROUND}", label(requester)));
                        }
                        requester.awaits_ground = true;
                        self.publish(requester, WalkState::Planning, WAITING_FOR_GROUND, f32::NAN);
                        return;
                    }
                }
                let why = format!(
                    "no way on toward the goal was found; it is {} m away",
                    fixed(before, 0)
                );
                self.end(cx, requester, WalkState::NoRoute, &why);
                return;
            }
            requester.ground_wait_since = None;
            requester.awaits_ground = false;
            if !requester.walk {
                let why = format!(
                    "a route was found for the first {} m; the rest is planned on the way",
                    fixed(route.length, 0)
                );
                self.end(cx, requester, WalkState::Planned, &why);
                return;
            }
            let driver = RouteDriver::new(&route.legs, &leaps_of(&route), false).expect("a route");
            self.drive_with(requester, driver);
            self.publish(
                requester,
                WalkState::Walking,
                "walking",
                route.length + left,
            );
            return;
        }
        if !requester.walk {
            self.end(cx, requester, WalkState::Planned, "a route was found");
            return;
        }
        if route.legs.len() < 2 {
            if requester.place.is_none()
                && !requester.onto
                && let Some(sample) = cx.body.try_sample()
            {
                let goal = self.locate(cx, requester);
                self.face(cx, goal, &sample);
            }
            if !route.ends_in_sight {
                let why = format!("already there; {}", route.reason);
                self.end(cx, requester, WalkState::ArrivedWithoutSight, &why);
            } else {
                self.end(cx, requester, WalkState::Arrived, "already there");
            }
            return;
        }
        requester.arrival_reason = (route.reason != "routed").then(|| route.reason.clone());
        requester.ends_in_sight = route.ends_in_sight;
        let driver = RouteDriver::new(&route.legs, &leaps_of(&route), false).expect("a route");
        self.drive_with(requester, driver);
        self.publish(requester, WalkState::Walking, "walking", route.length);
    }

    // ── Following ───────────────────────────────────────────────────────

    /// Starts a follow: players only, and waiting for the player when they
    /// are out of sight.
    fn begin_follow(&mut self, cx: &mut WalkWorld<'_>, mut request: Request) {
        if !cx.goals.is_player(request.object_id) {
            let why = format!(
                "only players can be followed, and {} is not one",
                label(&request)
            );
            self.end(cx, &mut request, WalkState::NoRoute, &why);
            return;
        }
        request.is_active = true;
        self.route = None;
        if self.narrating()
            && let Some(start) = cx.body.try_sample()
        {
            self.detail(&format!(
                "Follow {}: from {} in cell 0x{:08X}, keeping within {} m; {}",
                label(&request),
                point(start.position),
                start.cell_id,
                at_most_two_decimals(request.arrival_meters),
                self.collision_of(cx, request.object_id, start.position)
            ));
        }
        match cx.goals.try_locate(request.object_id) {
            None => {
                let why = format!("waiting to see {}", label(&request));
                self.follow_waits(&mut request, &why);
            }
            Some(leader) => {
                self.see_leader(cx, &mut request, leader);
                self.publish(&request, WalkState::Planning, "planning", f32::NAN);
            }
        }
        self.active = Some(request);
    }

    /// Takes where the player stands now as the follow's goal.
    fn see_leader(&mut self, cx: &WalkWorld<'_>, request: &mut Request, leader: Vec3) {
        if !request.leader_seen
            && let Some(surfaces) = cx.goals.try_get_surfaces(request.object_id)
        {
            request.leader_radius = radius_of(&surfaces, leader);
        }
        request.leader_seen = true;
        request.leader_last_seen = leader;
        request.goal = leader;
        request.goal_known = true;
        request.goal_radius = request.leader_radius;
        self.goal = Some((leader, request.arrival_meters));
    }

    /// Keeps a follow on the player each quantum, before the walk plans or
    /// drives. True when nothing more is to be done this quantum.
    fn follow_holds(
        &mut self,
        cx: &mut WalkWorld<'_>,
        active: &mut Request,
        sample: &WalkBodySample,
    ) -> bool {
        if sample.in_portal_space {
            self.stop_driving(cx, active);
            active.was_in_portal_space = true;
            self.follow_waits(active, "in portal space");
            return true;
        }
        if active.was_in_portal_space {
            active.was_in_portal_space = false;
            active.chasing = None;
            restart_follow(active);
            self.say(&format!(
                "Follow {}: out of portal space; looking for the player",
                label(active)
            ));
        }
        if let Some(portal) = active.chasing {
            return self.chase_portal(cx, active, portal);
        }

        let Some(leader) = cx.goals.try_locate(active.object_id) else {
            if active.leader_seen
                && self.driver.is_none()
                && let Some((portal_id, portal_at)) = cx
                    .goals
                    .try_find_portal(active.leader_last_seen, FOLLOW_PORTAL_REACH)
            {
                self.chase_through_portal(active, portal_id, portal_at, "vanished beside");
                return false;
            }
            if self.driver.is_some() {
                return false;
            }
            let why = format!("waiting to see {}", label(active));
            self.follow_waits(active, &why);
            return true;
        };

        // The client moves a player who enters a portal or recalls to where
        // they went at once, far faster than anyone runs.
        if active.leader_seen
            && self.seconds - active.leader_seen_at <= FOLLOW_TELEPORT_SECONDS
            && horizontal_distance(leader, active.leader_last_seen) > FOLLOW_TELEPORT_METERS
        {
            self.detail(&format!(
                "Follow {}: the player moved from {} to {} at once, as a portal or a recall moves them",
                label(active),
                point(active.leader_last_seen),
                point(leader)
            ));
            if let Some((entered_id, entered_at)) = cx
                .goals
                .try_find_portal(active.leader_last_seen, FOLLOW_PORTAL_REACH)
            {
                self.stop_driving(cx, active);
                self.routing_for = None;
                self.chase_through_portal(active, entered_id, entered_at, "entered");
                return false;
            }
            self.stop_driving(cx, active);
            if horizontal_distance(sample.position, leader) > FOLLOW_TELEPORT_REACH {
                active.asleep = true;
                self.say(&format!(
                    "Follow {}: the player was moved out of reach, {} m away, with no portal beside them; waiting until they come back within reach",
                    label(active),
                    fixed(horizontal_distance(sample.position, leader), 0)
                ));
            }
            restart_follow(active);
        }
        active.leader_seen_at = self.seconds;
        active.leader_last_seen = leader;

        // Asleep after the player was moved out of reach.
        if active.asleep {
            let gone = horizontal_distance(sample.position, leader);
            if gone > FOLLOW_TELEPORT_REACH {
                self.stop_driving(cx, active);
                let why = format!(
                    "the player is out of reach, {} m away; waiting for them to come back",
                    fixed(gone, 0)
                );
                self.publish(active, WalkState::Waiting, &why, f32::NAN);
                return true;
            }
            active.asleep = false;
            restart_follow(active);
            self.say(&format!(
                "Follow {}: the player is back within reach, {} m away; following",
                label(active),
                fixed(gone, 0)
            ));
        }

        // A player with no floor under them is in the air: the follow waits
        // for them to land.
        let aloft = self
            .grid
            .as_ref()
            .is_some_and(|seen| seen.contains(leader, 0.0) && !has_floor_near(seen, leader));
        if aloft {
            if active.leader_aloft_since.is_nan() {
                active.leader_aloft_since = self.seconds;
            }
            if self.seconds - active.leader_aloft_since < FOLLOW_ALOFT_SECONDS {
                if self.driver.is_some() || self.waits_on_search() {
                    return false;
                }
                self.publish(
                    active,
                    WalkState::Walking,
                    "waiting for the player to land",
                    f32::NAN,
                );
                return true;
            }
        } else {
            active.leader_aloft_since = f64::NAN;
        }

        if let Some(leaping) = self.driver.as_ref().map(RouteDriver::is_leaping) {
            active.leader_last_seen = leader;
            if horizontal_distance(leader, active.goal) > FOLLOW_REPLAN_METERS
                && self.seconds - active.follow_replanned_at >= FOLLOW_REPLAN_SECONDS
                && !leaping
                && self.routing.is_none()
            {
                active.follow_replanned_at = self.seconds;
                let planned = active.goal;
                self.see_leader(cx, active, leader);
                self.detail(&format!(
                    "Follow {}: the player moved from {} to {}; planning again on the way",
                    label(active),
                    point(planned),
                    point(leader)
                ));
                self.start_detour(cx, active, sample);
                if self.routing.is_none() {
                    self.stop_driving(cx, active);
                    active.builds = 0;
                }
            }
            return false;
        }

        let was_seen = active.leader_seen;
        if self.waits_on_search() {
            active.leader_last_seen = leader;
            return false;
        }
        if self.seconds < active.follow_retry_at {
            return true;
        }
        let away = horizontal_distance(sample.position, leader);
        if active.settled {
            active.leader_last_seen = leader;
            let moved_across = horizontal_distance(leader, active.settled_leader)
                > FOLLOW_SLACK_METERS
                && away > active.arrival_meters + active.leader_radius + FOLLOW_SLACK_METERS;
            let climbed = (leader.z - active.settled_leader.z).abs() > FOLLOW_CLIMB_METERS;
            if !moved_across && !climbed {
                let holding = format!(
                    "keeping behind {}, {} m away",
                    label(active),
                    fixed(away, 1)
                );
                self.publish(active, WalkState::Walking, &holding, away);
                return true;
            }
            self.detail(&format!(
                "Follow {}: the player moved to {}, {} m away; following",
                label(active),
                point(leader),
                fixed(away, 1)
            ));
            restart_follow(active);
        }
        if !was_seen || active.waiting_to_see {
            active.waiting_to_see = false;
            restart_follow(active);
            self.say(&format!(
                "Follow {}: the player is in sight at {}",
                label(active),
                point(leader)
            ));
        }
        self.see_leader(cx, active, leader);
        false
    }

    /// Turns a follow toward the portal the player went through.
    fn chase_through_portal(
        &mut self,
        active: &mut Request,
        portal_id: u32,
        portal_at: Vec3,
        how: &str,
    ) {
        active.chasing = Some(portal_id);
        active.goal = portal_at;
        active.goal_known = true;
        active.goal_radius = 0.0;
        restart_follow(active);
        self.goal = Some((portal_at, PORTAL_ARRIVAL_METERS));
        let through = format!(
            "the player {how} portal 0x{portal_id:08X} at {}; following through it",
            point(portal_at)
        );
        self.publish(active, WalkState::Walking, &through, f32::NAN);
        self.say(&format!("Follow {}: {through}", label(active)));
    }

    /// Walks into the portal the player left by, uses it once there, and
    /// waits for it to take the character.
    fn chase_portal(&mut self, cx: &mut WalkWorld<'_>, active: &mut Request, portal: u32) -> bool {
        if self.driver.is_some() || self.waits_on_search() {
            return false;
        }
        if !active.settled {
            return false;
        }
        if active.portal_used_at.is_nan()
            || self.seconds - active.portal_used_at >= PORTAL_USE_SECONDS
        {
            active.portal_used_at = self.seconds;
            if let Some(doors) = cx.doors.as_deref_mut() {
                doors.use_door(portal);
                self.say(&format!(
                    "Follow {}: using portal 0x{portal:08X}",
                    label(active)
                ));
            }
        }
        let entering = format!("entering portal 0x{portal:08X} after {}", label(active));
        self.publish(active, WalkState::Walking, &entering, f32::NAN);
        true
    }

    /// A follow's walk ended, as a walk would: it holds behind the player,
    /// or tries again shortly.
    fn keep_following(
        &mut self,
        cx: &mut WalkWorld<'_>,
        request: &mut Request,
        state: WalkState,
        reason: &str,
    ) {
        if let Some(driver) = self.driver.take() {
            self.retire(request, driver);
        }
        request.builds = 0;
        request.replans = 0;
        request.avoid.clear();
        request.stuck_spots.clear();
        if matches!(state, WalkState::Arrived | WalkState::ArrivedWithoutSight) {
            request.recoveries = 0;
            request.settled = true;
            request.follow_said = None;
            request.step_offs = 0;
            request.settled_leader = request.leader_last_seen;
            if let Some(portal) = request.chasing {
                self.detail(&format!(
                    "Follow {}: at portal 0x{portal:08X}",
                    label(request)
                ));
                return;
            }
            let holding = format!("keeping behind {}", label(request));
            self.publish(request, WalkState::Walking, &holding, f32::NAN);
            self.detail(&format!("Follow {}: {holding}; {reason}", label(request)));
            return;
        }
        request.settled = false;
        request.follow_retry_at = self.seconds + FOLLOW_RETRY_SECONDS;
        if self.try_step_off(cx, request) {
            return;
        }
        let again = format!("{reason}; trying again");
        self.publish(request, WalkState::Waiting, &again, f32::NAN);
        if request.follow_said.as_deref() != Some(reason) {
            request.follow_said = Some(reason.to_owned());
            self.say(&format!("Follow {}: {again}", label(request)));
        } else {
            self.detail(&format!("Follow {}: {again}", label(request)));
        }
    }

    /// Steps a follower off a top too small to walk on, toward the player,
    /// when no route leaves it, at most [`MAXIMUM_STEP_OFFS`] times running.
    fn try_step_off(&mut self, cx: &mut WalkWorld<'_>, request: &mut Request) -> bool {
        let Some(grid) = self.grid.clone() else {
            return false;
        };
        if request.step_offs >= MAXIMUM_STEP_OFFS {
            return false;
        }
        let Some(sample) = cx.body.try_sample() else {
            return false;
        };
        if !grid.contains(sample.position, 0.0) || !on_small_top(&grid, sample.position) {
            return false;
        }
        request.step_offs += 1;
        let toward = self.locate(cx, request);
        self.face(cx, toward, &sample);
        cx.body.begin_move(MoveRequest::new(
            MoveDirection::Forward,
            MovePace::Walk,
            STEP_OFF_METERS,
        ));
        let stepping = format!(
            "standing on a top no route leaves; stepping off toward the player ({} of {})",
            request.step_offs, MAXIMUM_STEP_OFFS
        );
        self.publish(request, WalkState::Walking, &stepping, f32::NAN);
        self.say(&format!("Follow {}: {stepping}", label(request)));
        true
    }

    /// Holds a follow where the character stands, saying why once.
    fn follow_waits(&mut self, request: &mut Request, why: &str) {
        request.settled = false;
        request.waiting_to_see = why.starts_with("waiting to see");
        self.publish(request, WalkState::Waiting, why, f32::NAN);
        if request.follow_said.as_deref() != Some(why) {
            request.follow_said = Some(why.to_owned());
            self.say(&format!("Follow {}: {why}", label(request)));
        }
    }

    fn stop_driving(&mut self, cx: &mut WalkWorld<'_>, request: &mut Request) {
        if let Some(mut driver) = self.driver.take() {
            self.apply(cx, &driver.cancel());
            self.retire(request, driver);
        }
    }

    // ── What is said about goals ────────────────────────────────────────

    /// Narrates why no route reached a goal.
    fn narrate_no_route(&self, request: &Request, route: &NavRoute) {
        let Some(grid) = &self.grid else {
            return;
        };
        if request.staged {
            return;
        }
        let goal = request.goal;
        self.detail(&format!(
            "No route to {} ({:?}, {} expansions); goal {} {} the grid",
            label(request),
            route.outcome,
            route.expansions,
            point(goal),
            if grid.contains(goal, 0.0) {
                "inside"
            } else {
                "outside"
            }
        ));
        let near: Vec<String> = request
            .planned_avoid
            .iter()
            .filter(|spot| {
                horizontal_distance(spot.centre, goal) <= NARRATED_GOAL_SURROUNDINGS + spot.radius
            })
            .map(|spot| format!("{} r {}", point(spot.centre), fixed(spot.radius, 1)))
            .collect();
        self.detail(&format!(
            "Kept out of near the goal: {}",
            if near.is_empty() {
                "nothing".to_owned()
            } else {
                near.join("; ")
            }
        ));

        let ring = request.goal_radius + request.arrival_meters.min(2.5);
        let mut spots = Vec::new();
        for step in 0..16 {
            let angle = step as f32 * std::f32::consts::PI / 8.0;
            let at = Vec3::new(
                goal.x + angle.sin() * ring,
                goal.y + angle.cos() * ring,
                goal.z,
            );
            let node = grid.find_node(at, 0.75, GOAL_HEIGHT_TOLERANCE);
            if node < 0 {
                spots.push(format!("{}°: no clear spot", fixed(step as f32 * 22.5, 0)));
                continue;
            }
            let feet = grid.position(node);
            let toward = Vec2::new(feet.x - goal.x, feet.y - goal.y);
            let away = toward.length();
            let seen = if request.goal_radius > 0.0 && away > 1e-3 {
                goal + (toward * ((request.goal_radius + grid.cell_size() * 1.5).min(away) / away))
                    .extend(0.0)
            } else {
                goal
            };
            let sees = grid.can_see(node, seen);
            let crossed = request
                .planned_avoid
                .iter()
                .any(|spot| flat_distance_to_segment(spot.centre, feet, seen) < spot.radius);
            let mut why = String::new();
            if !sees {
                let target = seen + Vec3::new(0.0, 0.0, TARGET_HEIGHT);
                why = if target.z >= grid.ceiling(node) {
                    format!(
                        " (the goal at {} m is above the ceiling at {} m)",
                        fixed(target.z, 1),
                        fixed(grid.ceiling(node), 1)
                    )
                } else if let Some(&(x, y, low, high, line)) =
                    grid.sight_blockers(node, seen).first()
                {
                    format!(
                        " (a wall from {} to {} m at ({}, {}) crosses the line at {} m)",
                        fixed(low, 1),
                        fixed(high, 1),
                        fixed(grid.origin_x() + (x as f32 + 0.5) * grid.cell_size(), 1),
                        fixed(grid.origin_y() + (y as f32 + 0.5) * grid.cell_size(), 1),
                        fixed(line, 1)
                    )
                } else {
                    " (the goal is below this spot's floor)".to_owned()
                };
            }
            spots.push(format!(
                "{}°: z {}, {}{why}{}",
                fixed(step as f32 * 22.5, 0),
                fixed(feet.z, 1),
                if sees { "sees" } else { "no sight" },
                if crossed {
                    ", line crosses a kept-out spot"
                } else {
                    ""
                }
            ));
        }
        self.detail(&format!(
            "Around the goal at {} m: {}",
            fixed(ring, 1),
            spots.join("; ")
        ));
    }

    /// What a walk found of an object's collision, for narration.
    fn collision_of(&self, cx: &WalkWorld<'_>, object_id: u32, at: Vec3) -> String {
        let Some(surfaces) = cx.goals.try_get_surfaces(object_id) else {
            return "the client has no collision for it, so it is seen and reached at the point it stands on".to_owned();
        };
        let mut minimum = Vec3::splat(f32::INFINITY);
        let mut maximum = Vec3::splat(f32::NEG_INFINITY);
        for triangle in &surfaces.triangles {
            minimum = minimum.min(triangle.a.min(triangle.b.min(triangle.c)));
            maximum = maximum.max(triangle.a.max(triangle.b.max(triangle.c)));
        }
        for cylinder in &surfaces.cylinders {
            minimum = minimum.min(cylinder.base - Vec3::new(cylinder.radius, cylinder.radius, 0.0));
            maximum = maximum
                .max(cylinder.base + Vec3::new(cylinder.radius, cylinder.radius, cylinder.height));
        }
        format!(
            "collision of {} triangles and {} cylinders from {} to {}, reaching {} m from where it stands",
            surfaces.triangles.len(),
            surfaces.cylinders.len(),
            point(minimum),
            point(maximum),
            fixed(radius_of(&surfaces, at), 1)
        )
    }

    /// Where a request's goal stands now, or where it stood when planned once
    /// the client has lost it.
    fn locate(&self, cx: &WalkWorld<'_>, request: &Request) -> Vec3 {
        let located = match request.place {
            Some((cell_id, local)) => cx.goals.try_locate_place(cell_id, local),
            None => cx.goals.try_locate(request.goal_object_id()),
        };
        located.unwrap_or(request.goal)
    }

    // ── Leaps aimed again on the grid ───────────────────────────────────

    fn with_aim_finder<R>(
        &self,
        grid: &Arc<NavGrid>,
        ability: NavLeapAbility,
        use_finder: impl FnOnce(&AimFinder) -> R,
    ) -> R {
        let mut slot = self.aim_finder.borrow_mut();
        let fresh = !matches!(
            &*slot,
            Some(finder) if std::ptr::eq(finder.grid(), &**grid) && finder.ability() == ability
        );
        if fresh {
            *slot = Some(NavLeapFinder::new(Arc::clone(grid), ability));
        }
        use_finder(slot.as_ref().expect("made above"))
    }

    /// Aims a leap again from exactly where the body stands, on the grid its
    /// route was planned over.
    fn aim_leap_from(
        &self,
        standing: Vec3,
        landing: Vec3,
        run: bool,
        at_its_takeoff: bool,
    ) -> Option<RouteLeapAim> {
        let grid = self.grid.as_ref()?;
        let ability = self.leap_ability?;
        if !grid.contains(standing, 0.0) || !grid.contains(landing, 0.0) {
            return None;
        }
        let aimed = self.with_aim_finder(grid, ability, |finder| {
            finder.aim_from_explained(standing, landing, run, at_its_takeoff)
        });
        match aimed {
            Ok(aim) => {
                *self.aim_refused.borrow_mut() = None;
                Some(RouteLeapAim {
                    power: aim.power,
                    run: aim.run,
                })
            }
            Err(refused) => {
                *self.aim_refused.borrow_mut() = Some(refused);
                None
            }
        }
    }

    /// A leap from where the body stands onto the floor a planned landing
    /// lies on, with the spot it comes to rest on.
    fn aim_onward(&self, standing: Vec3, landing: Vec3, run: bool) -> Option<(RouteLeapAim, Vec3)> {
        let grid = self.grid.as_ref()?;
        let ability = self.leap_ability?;
        if !grid.contains(standing, 0.0) || !grid.contains(landing, 0.0) {
            return None;
        }
        self.with_aim_finder(grid, ability, |finder| {
            finder.aim_onward(standing, landing, run)
        })
        .map(|(aim, spot)| {
            (
                RouteLeapAim {
                    power: aim.power,
                    run: aim.run,
                },
                spot,
            )
        })
    }

    /// Whether two points stand on the same piece of floor of the grid.
    fn same_floor(&self, first: Vec3, second: Vec3) -> bool {
        let Some(grid) = &self.grid else {
            return false;
        };
        if !grid.contains(first, 0.0) || !grid.contains(second, 0.0) {
            return false;
        }
        let one = grid.find_standing_node(first, 0.5, START_HEIGHT_TOLERANCE);
        let other = grid.find_standing_node(second, 0.5, START_HEIGHT_TOLERANCE);
        if one < 0 || other < 0 {
            return false;
        }
        let pieces = grid.surfaces().piece_of();
        pieces[one as usize] >= 0 && pieces[one as usize] == pieces[other as usize]
    }
}
