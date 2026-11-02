//! Walks against a simulated body and world.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/Navigation/NavigationWalkControllerTests.cs`
//! (the cases named after theirs below, with that file's harness: the
//! simulated body, the goals, the fake doors and the flat and walled worlds).
//! Searches run inline here, so no test waits on a worker.

use std::cell::{Cell, RefCell};
use std::collections::{HashMap, HashSet};
use std::rc::Rc;
use std::sync::{Arc, Mutex};

use ac_nav::driver::RouteTurning;
use ac_nav::motor::{
    MoveChannel, MoveChannelSnapshot, MoveDirection, MovePace, MoveRequest, ScriptedMoveSnapshot,
    ScriptedMoveState,
};
use ac_nav::route::NavAvoidance;
use ac_nav::sources::{except_the_one_the_walk_ends, on_the_cell_floor, portal_footprint};
use ac_nav::walk::{
    self, DEFAULT_ARRIVAL_METERS, GRID_IDLE_SECONDS, MAXIMUM_REGION, MAXIMUM_REPLANS, NavBlocker,
    NavDoor, NavDoors, NavGoalSource, PlanMode, REGION_MARGIN, WalkBody, WalkBodySample,
    WalkController, WalkReport, WalkState, WalkWorld,
};
use ac_nav::{NavBody, NavSurfaces};
use ac_physics::{CellSurface, PhysicsEngine, TerrainSurface};
use glam::{Vec2, Vec3};

const FRAME: f32 = 1.0 / 30.0;
const TARGET: u32 = 0x5000_0001;
const OTHER: u32 = 0x5000_0002;
const DOOR: u32 = 0x7A00_0001;

fn v(x: f32, y: f32, z: f32) -> Vec3 {
    Vec3::new(x, y, z)
}

/// A body that carries out scripted moves the way the client does, at
/// fixed speeds, sliding off a round obstacle and reporting a move blocked
/// once it stops making progress.
struct SimBody {
    sequence: i64,
    travel: MoveChannelSnapshot,
    turn: MoveChannelSnapshot,
    turn_remaining: f32,
    stalled_seconds: f32,
    run_speed: f32,
    walk_speed: f32,
    turn_speed: f32,
    walk_turn_speed: f32,
    seconds: f64,
    walking_seconds: f64,
    turning: Option<RouteTurning>,
    travel_stops: i32,
    path: Vec<Vec2>,
    travel_directions: Vec<MoveDirection>,
    position: Vec3,
    heading: f32,
    stuck: bool,
    in_portal_space: bool,
    player_movement_input_frames: i64,
    cell_id: u32,
    obstacle: Option<(Vec2, f32)>,
    obstacle_active: Option<Rc<dyn Fn() -> bool>>,
    airborne: bool,
    moves_begun: i32,
    jumps: i32,
    still_frames: i32,
}

const STALL_SECONDS: f32 = 1.5;

impl SimBody {
    fn at(position: Vec3) -> Self {
        Self {
            sequence: 0,
            travel: MoveChannelSnapshot::default(),
            turn: MoveChannelSnapshot::default(),
            turn_remaining: 0.0,
            stalled_seconds: 0.0,
            run_speed: 4.0,
            walk_speed: 1.5,
            turn_speed: 180.0,
            walk_turn_speed: 180.0,
            seconds: 0.0,
            walking_seconds: 0.0,
            turning: None,
            travel_stops: 0,
            path: Vec::new(),
            travel_directions: Vec::new(),
            position,
            heading: 0.0,
            stuck: false,
            in_portal_space: false,
            player_movement_input_frames: 0,
            cell_id: 0,
            obstacle: None,
            obstacle_active: None,
            airborne: false,
            moves_begun: 0,
            jumps: 0,
            still_frames: 0,
        }
    }

    fn flat(&self) -> Vec2 {
        self.position.truncate()
    }

    fn travelling(&self) -> bool {
        self.travel.state == ScriptedMoveState::Moving
    }

    fn interrupt(&mut self) {
        if self.travel.state == ScriptedMoveState::Moving {
            self.travel.state = ScriptedMoveState::Interrupted;
        }
        if self.turn.state == ScriptedMoveState::Moving {
            self.turn.state = ScriptedMoveState::Interrupted;
        }
    }

    fn heading_error_to(&self, target: Vec2) -> f32 {
        let toward = target - self.flat();
        let heading = toward.x.atan2(toward.y) * (180.0 / std::f32::consts::PI);
        ((heading - self.heading) % 360.0 + 540.0) % 360.0 - 180.0
    }

    fn integrate(&mut self, seconds: f32) {
        self.seconds += f64::from(seconds);
        let walking = self.travel.state == ScriptedMoveState::Moving
            && self.travel.request.pace == MovePace::Walk;
        if self.turn.state == ScriptedMoveState::Moving {
            let turn_speed = if walking {
                self.walk_turn_speed
            } else {
                self.turn_speed
            };
            let step = self.turn_remaining.min(turn_speed * seconds);
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
        if self.travel.state != ScriptedMoveState::Moving {
            self.still_frames += 1;
            return;
        }
        self.still_frames = 0;
        if walking {
            self.walking_seconds += f64::from(seconds);
        }
        let speed = if self.travel.request.pace == MovePace::Run {
            self.run_speed
        } else {
            self.walk_speed
        };
        // As OpenAC's simulated body: every travel goes the way the body
        // faces, and a move with an amount runs until the walk stops it.
        let radians = self.heading * std::f32::consts::PI / 180.0;
        let intended =
            self.position + Vec3::new(radians.sin(), radians.cos(), 0.0) * speed * seconds;
        let next = if self.stuck {
            self.position
        } else {
            self.slide_off_obstacle(intended)
        };
        let moved = next.truncate().distance(self.flat());
        self.position = next;
        self.path.push(self.flat());
        self.stalled_seconds = if moved < speed * seconds * 0.25 {
            self.stalled_seconds + seconds
        } else {
            0.0
        };
        self.travel.elapsed_seconds += seconds;
        if self.stalled_seconds > STALL_SECONDS {
            self.travel.state = ScriptedMoveState::Blocked;
        }
    }

    fn slide_off_obstacle(&self, intended: Vec3) -> Vec3 {
        let Some((centre, radius)) = self.obstacle else {
            return intended;
        };
        if self
            .obstacle_active
            .as_ref()
            .is_some_and(|active| !active())
        {
            return intended;
        }
        let offset = intended.truncate() - centre;
        let distance = offset.length();
        if distance >= radius {
            return intended;
        }
        let pushed = if distance > 1e-4 {
            centre + offset / distance * radius
        } else {
            self.flat()
        };
        pushed.extend(intended.z)
    }
}

/// The walk's view of the shared simulated body.
struct BodyHandle(Rc<RefCell<SimBody>>);

impl WalkBody for BodyHandle {
    fn try_sample(&self) -> Option<WalkBodySample> {
        let body = self.0.borrow();
        Some(WalkBodySample {
            position: body.position,
            heading_degrees: body.heading,
            body: NavBody::player(0.6, 1.5),
            moves: ScriptedMoveSnapshot {
                travel: body.travel,
                strafe: MoveChannelSnapshot::default(),
                turn: body.turn,
                jump_sequence: 0,
                jump_charging: false,
            },
            in_portal_space: body.in_portal_space,
            cell_id: body.cell_id,
            leaps: None,
            airborne: body.airborne,
            turning: body.turning,
            walk_stop_meters: 0.0,
            run_stop_meters: 0.0,
            player_movement_input_frames: body.player_movement_input_frames,
        })
    }

    fn begin_move(&mut self, request: MoveRequest) -> bool {
        let mut body = self.0.borrow_mut();
        body.moves_begun += 1;
        body.sequence += 1;
        let begun = MoveChannelSnapshot {
            sequence: body.sequence,
            state: ScriptedMoveState::Moving,
            request,
            covered: 0.0,
            elapsed_seconds: 0.0,
        };
        if matches!(
            request.direction,
            MoveDirection::TurnLeft | MoveDirection::TurnRight
        ) {
            body.turn = begun;
            body.turn_remaining = request.amount;
        } else {
            body.travel = begun;
            body.stalled_seconds = 0.0;
            body.travel_directions.push(request.direction);
        }
        true
    }

    fn stop_move(&mut self, channel: MoveChannel) -> bool {
        let mut body = self.0.borrow_mut();
        if channel == MoveChannel::Turn && body.turn.state == ScriptedMoveState::Moving {
            body.turn.state = ScriptedMoveState::Stopped;
            return true;
        }
        if channel == MoveChannel::Travel && body.travel.state == ScriptedMoveState::Moving {
            body.travel.state = ScriptedMoveState::Stopped;
            body.travel_stops += 1;
            return true;
        }
        false
    }

    fn begin_jump(&mut self, _power: f32, _leave_at: Option<MovePace>) -> bool {
        self.0.borrow_mut().jumps += 1;
        true
    }
}

#[derive(Default)]
struct Goals {
    at: HashMap<u32, Vec3>,
    blocker: Option<NavBlocker>,
    players: HashSet<u32>,
    surfaces: HashMap<u32, NavSurfaces>,
    /// The objects a grid stands on.
    standing: HashSet<u32>,
    /// The objects the server placed, as a walk asks for them.
    obstacles: Vec<NavAvoidance>,
    /// The creatures and players standing around, as a walk asks for them
    /// now.
    crowd_now: Option<Box<dyn Fn() -> Vec<NavAvoidance>>>,
    /// The portals standing about.
    portals: Vec<NavAvoidance>,
    /// The portals by id and where they stand.
    portal_ids: Vec<(u32, Vec3)>,
    /// When set, places in a cell stand on its floor as the client's goal
    /// source stands them.
    floors: Option<PhysicsEngine>,
}

impl NavGoalSource for Goals {
    fn try_locate(&self, object_id: u32) -> Option<Vec3> {
        self.at.get(&object_id).copied()
    }

    fn try_find_blocker(&self, _position: Vec3, _radius: f32) -> Option<NavBlocker> {
        self.blocker.clone()
    }

    fn try_locate_place(&self, cell_id: u32, local: Vec3) -> Option<Vec3> {
        Some(self.floors.as_ref().map_or(local, |floors| {
            on_the_cell_floor(floors, cell_id, local, None)
        }))
    }

    fn try_get_surfaces(&self, object_id: u32) -> Option<NavSurfaces> {
        self.surfaces.get(&object_id).cloned()
    }

    fn is_player(&self, object_id: u32) -> bool {
        self.players.contains(&object_id)
    }

    fn stands_still(&self, entity_id: u32) -> bool {
        self.standing.contains(&entity_id)
    }

    fn find_obstacles(
        &self,
        _around: Vec3,
        _radius: f32,
        _goal_object_id: u32,
        _in_grid: Option<&HashSet<u32>>,
    ) -> Vec<NavAvoidance> {
        self.obstacles.clone()
    }

    fn find_crowd(&self, _around: Vec3, _radius: f32, _goal_object_id: u32) -> Vec<NavAvoidance> {
        self.crowd_now
            .as_ref()
            .map_or_else(Vec::new, |crowd| crowd())
    }

    fn find_portals(
        &self,
        _around: Vec3,
        _radius: f32,
        _goal_object_id: u32,
        goal: Vec3,
    ) -> Vec<NavAvoidance> {
        let footprints: Vec<NavAvoidance> =
            self.portals.iter().copied().map(portal_footprint).collect();
        except_the_one_the_walk_ends(&footprints, goal)
    }

    fn try_find_portal(&self, near: Vec3, radius: f32) -> Option<(u32, Vec3)> {
        self.portal_ids
            .iter()
            .find(|(_, at)| at.truncate().distance(near.truncate()) <= radius)
            .copied()
    }

    fn try_global_of(&self, world: Vec3) -> Option<Vec3> {
        Some(world)
    }
}

/// A single door that opens a few polls after it is used, or never.
struct FakeDoors {
    door: NavDoor,
    at: Vec2,
    opens: bool,
    polls_until_open: Cell<i32>,
    open: Rc<Cell<bool>>,
    uses: i32,
    dropped_uses: i32,
    reach: f32,
    used_from: Vec<f32>,
    locked_when_appraised: Option<bool>,
    appraisals: i32,
    locked: Option<bool>,
    seen_ahead: bool,
    body: Option<Rc<RefCell<SimBody>>>,
}

impl FakeDoors {
    fn new(door: NavDoor, at: Vec2, opens: bool) -> Self {
        Self {
            door,
            at,
            opens,
            polls_until_open: Cell::new(-1),
            open: Rc::new(Cell::new(false)),
            uses: 0,
            dropped_uses: 0,
            reach: 0.0,
            used_from: Vec::new(),
            locked_when_appraised: None,
            appraisals: 0,
            locked: None,
            seen_ahead: true,
            body: None,
        }
    }
}

impl NavDoors for FakeDoors {
    fn try_find_closed_door(&self, from: Vec3, to: Vec3, corridor: f32) -> Option<NavDoor> {
        if self.open.get() || !self.seen_ahead {
            return None;
        }
        let start = from.truncate();
        let along = to.truncate() - start;
        let length_squared = along.length_squared();
        let t = if length_squared > 1e-6 {
            (self.at - start).dot(along) / length_squared
        } else {
            0.0
        };
        (t >= 0.0 && self.at.distance(start + along * t.min(1.0)) <= corridor)
            .then(|| self.door.clone())
    }

    fn is_open(&self, _door_id: u32) -> bool {
        let polls = self.polls_until_open.get();
        if polls > 0 {
            self.polls_until_open.set(polls - 1);
            if polls - 1 == 0 {
                self.open.set(true);
            }
        }
        self.open.get()
    }

    fn use_door(&mut self, _door_id: u32) {
        self.uses += 1;
        if let Some(body) = &self.body {
            let body = body.borrow();
            self.used_from.push(body.flat().distance(self.at));
            // The client drops a use when a stop that lands after it cancels
            // the walk into the door's use range.
            if body.still_frames < 1 {
                self.dropped_uses += 1;
                return;
            }
        }
        if self.opens {
            self.polls_until_open.set(10);
        }
    }

    fn use_reach(&self, _door_id: u32) -> f32 {
        self.reach
    }

    fn is_locked(&self, _door_id: u32) -> Option<bool> {
        self.locked
    }

    fn appraise(&mut self, _door_id: u32) -> bool {
        self.appraisals += 1;
        self.locked = self.locked_when_appraised;
        self.locked_when_appraised.is_some()
    }
}

fn flat_world(landblocks_north: u32) -> PhysicsEngine {
    let mut physics = PhysicsEngine::new();
    for index in 0..landblocks_north {
        physics.add_landblock(
            0xA9B4_FFFF + (index << 16),
            TerrainSurface::new(&[0; 81], &[0.0; 256], 0, 0, None),
            Vec::new(),
            Vec::new(),
            0.0,
            index as f32 * 192.0,
        );
    }
    physics
}

/// A room 20 m square, 30 m below its landblock's terrain, split along y = 10
/// by a wall 3 m high with a doorway 2 m wide centred at each x in
/// `doorways`.
fn room_with_doorways(doorways: &[f32]) -> PhysicsEngine {
    const FLOOR: f32 = -30.0;
    const TOP: f32 = -27.0;
    let mut vertices: HashMap<u16, Vec3> = HashMap::from([
        (0, v(0.0, 0.0, FLOOR)),
        (1, v(20.0, 0.0, FLOOR)),
        (2, v(20.0, 20.0, FLOOR)),
        (3, v(0.0, 20.0, FLOOR)),
    ]);
    let mut polygons: Vec<Vec<u16>> = vec![vec![0, 1, 2, 3]];
    let mut start = 0.0;
    let mut ends: Vec<f32> = doorways.iter().map(|centre| centre - 1.0).collect();
    ends.sort_by(f32::total_cmp);
    ends.push(20.0);
    for end in ends {
        if end > start {
            let first = vertices.len() as u16;
            vertices.insert(first, v(start, 10.0, FLOOR));
            vertices.insert(first + 1, v(end, 10.0, FLOOR));
            vertices.insert(first + 2, v(end, 10.0, TOP));
            vertices.insert(first + 3, v(start, 10.0, TOP));
            polygons.push(vec![first, first + 1, first + 2, first + 3]);
        }
        start = end + 2.0;
    }
    let mut physics = PhysicsEngine::new();
    physics.add_landblock(
        0xA9B4_FFFF,
        TerrainSurface::new(&[0; 81], &[0.0; 256], 0, 0, None),
        vec![CellSurface::new(0xA9B4_0100, &vertices, &polygons)],
        Vec::new(),
        0.0,
        0.0,
    );
    physics
}

/// Records every object a walk asks the client to use.
#[derive(Default)]
struct UseRecorder {
    used: Vec<u32>,
}

impl NavDoors for UseRecorder {
    fn try_find_closed_door(&self, _from: Vec3, _to: Vec3, _corridor: f32) -> Option<NavDoor> {
        None
    }

    fn is_open(&self, _door_id: u32) -> bool {
        true
    }

    fn use_door(&mut self, door_id: u32) {
        self.used.push(door_id);
    }
}

/// The triangles of a convex polygon, fanned from its first corner.
fn triangles(polygon: &[Vec3]) -> Vec<ac_nav::NavTriangle> {
    (2..polygon.len())
        .map(|index| ac_nav::NavTriangle::new(polygon[0], polygon[index - 1], polygon[index]))
        .collect()
}

/// Flat ground with the polygons of one cell standing on it.
fn cell_world(cell: &[Vec<Vec3>]) -> PhysicsEngine {
    let mut vertices: HashMap<u16, Vec3> = HashMap::new();
    let mut polygons: Vec<Vec<u16>> = Vec::new();
    for polygon in cell {
        let mut indices = Vec::new();
        for corner in polygon {
            let index = vertices.len() as u16;
            vertices.insert(index, *corner);
            indices.push(index);
        }
        polygons.push(indices);
    }
    let mut physics = PhysicsEngine::new();
    physics.add_landblock(
        0xA9B4_FFFF,
        TerrainSurface::new(&[0; 81], &[0.0; 256], 0, 0, None),
        vec![CellSurface::new(0xA9B4_0100, &vertices, &polygons)],
        Vec::new(),
        0.0,
        0.0,
    );
    physics
}

/// A box `low`..`high` across standing on the ground, `height` tall: its
/// top and four sides.
fn block(low: Vec2, high: Vec2, height: f32) -> Vec<Vec<Vec3>> {
    let (x0, y0, x1, y1, h) = (low.x, low.y, high.x, high.y, height);
    vec![
        vec![v(x0, y0, h), v(x1, y0, h), v(x1, y1, h), v(x0, y1, h)],
        vec![v(x0, y0, 0.0), v(x1, y0, 0.0), v(x1, y0, h), v(x0, y0, h)],
        vec![v(x1, y0, 0.0), v(x1, y1, 0.0), v(x1, y1, h), v(x1, y0, h)],
        vec![v(x1, y1, 0.0), v(x0, y1, 0.0), v(x0, y1, h), v(x1, y1, h)],
        vec![v(x0, y1, 0.0), v(x0, y0, 0.0), v(x0, y0, h), v(x0, y1, h)],
    ]
}

/// A stone 2 m across and 3 m tall standing on the ground.
fn stone_block() -> Vec<Vec<Vec3>> {
    block(Vec2::new(51.0, 51.0), Vec2::new(53.0, 53.0), 3.0)
}

/// A post 0.8 m across and 2 m tall, its top too small to walk on.
fn post_top() -> Vec<Vec<Vec3>> {
    block(Vec2::new(39.6, 39.6), Vec2::new(40.4, 40.4), 2.0)
}

/// A block 4 m across and 2.5 m tall, higher than a body steps or reaches
/// without leaping.
fn furnace() -> Vec<Vec<Vec3>> {
    block(Vec2::new(50.0, 50.0), Vec2::new(54.0, 54.0), 2.5)
}

/// A ramp 6 m long rising 2 m to a deck 4 m across.
fn ramp_deck() -> Vec<Vec<Vec3>> {
    vec![
        vec![
            v(50.0, 46.0, 0.0),
            v(54.0, 46.0, 0.0),
            v(54.0, 52.0, 2.0),
            v(50.0, 52.0, 2.0),
        ],
        vec![
            v(50.0, 52.0, 2.0),
            v(54.0, 52.0, 2.0),
            v(54.0, 56.0, 2.0),
            v(50.0, 56.0, 2.0),
        ],
    ]
}

/// The top of [`raised_slab_world`]'s slab as triangles.
fn slab_top() -> Vec<ac_nav::NavTriangle> {
    vec![
        ac_nav::NavTriangle::new(v(50.0, 50.0, 1.0), v(54.0, 50.0, 1.0), v(54.0, 54.0, 1.0)),
        ac_nav::NavTriangle::new(v(50.0, 50.0, 1.0), v(54.0, 54.0, 1.0), v(50.0, 54.0, 1.0)),
    ]
}

/// Flat ground with a slab 4 m across and 1 m up, higher than a body steps.
fn raised_slab_world() -> PhysicsEngine {
    cell_world(&[vec![
        v(50.0, 50.0, 1.0),
        v(54.0, 50.0, 1.0),
        v(54.0, 54.0, 1.0),
        v(50.0, 54.0, 1.0),
    ]])
}

/// A room like [`room_with_doorways`], but with the dividing wall extending
/// beyond the floor so the route cannot walk around either end.
fn impassable_room_wall() -> PhysicsEngine {
    const FLOOR: f32 = -30.0;
    const TOP: f32 = -27.0;
    let vertices: HashMap<u16, Vec3> = HashMap::from([
        (0, v(0.0, 0.0, FLOOR)),
        (1, v(20.0, 0.0, FLOOR)),
        (2, v(20.0, 20.0, FLOOR)),
        (3, v(0.0, 20.0, FLOOR)),
        (4, v(-100.0, 10.0, FLOOR)),
        (5, v(100.0, 10.0, FLOOR)),
        (6, v(100.0, 10.0, TOP)),
        (7, v(-100.0, 10.0, TOP)),
    ]);
    let mut physics = PhysicsEngine::new();
    physics.add_landblock(
        0xA9B4_FFFF,
        TerrainSurface::new(&[0; 81], &[0.0; 256], 0, 0, None),
        vec![CellSurface::new(
            0xA9B4_0100,
            &vertices,
            &[vec![0, 1, 2, 3], vec![4, 5, 6, 7]],
        )],
        Vec::new(),
        0.0,
        0.0,
    );
    physics
}

/// A landblock holding a dungeon 30 m below its terrain: two rooms joined by
/// a corridor, 190 m long unless `corridor_end` moves the far room.
fn dungeon_world(first_cell: u32, corridor_end: f32) -> PhysicsEngine {
    let floor = |cell_id: u32, x0: f32, y0: f32, x1: f32, y1: f32| {
        let vertices: HashMap<u16, Vec3> = HashMap::from([
            (0, v(x0, y0, -30.0)),
            (1, v(x1, y0, -30.0)),
            (2, v(x1, y1, -30.0)),
            (3, v(x0, y1, -30.0)),
        ]);
        CellSurface::new(cell_id, &vertices, &[vec![0, 1, 2, 3]])
    };
    let mut physics = PhysicsEngine::new();
    physics.add_landblock(
        0xA9B4_FFFF,
        TerrainSurface::new(&[0; 81], &[0.0; 256], 0, 0, None),
        vec![
            floor(first_cell, 10.0, 10.0, 40.0, 50.0),
            floor(first_cell + 1, 40.0, 28.0, corridor_end, 36.0),
            floor(
                first_cell + 2,
                corridor_end,
                20.0,
                corridor_end + 20.0,
                50.0,
            ),
        ],
        Vec::new(),
        0.0,
        0.0,
    );
    physics
}

struct Harness {
    physics: PhysicsEngine,
    body: Rc<RefCell<SimBody>>,
    goals: Goals,
    doors: Option<FakeDoors>,
    recorder: Option<UseRecorder>,
    need: Rc<RefCell<Option<String>>>,
    sealed: Option<Box<dyn Fn(u32) -> bool>>,
    said: Arc<Mutex<Vec<String>>>,
    narrated: Arc<Mutex<Vec<String>>>,
    walk: WalkController,
}

fn collecting(lines: &Arc<Mutex<Vec<String>>>) -> walk::Say {
    let lines = Arc::clone(lines);
    Box::new(move |line: &str| lines.lock().unwrap().push(line.to_owned()))
}

impl Harness {
    fn new(physics: PhysicsEngine, body: SimBody, goals: Goals) -> Self {
        let said = Arc::new(Mutex::new(Vec::new()));
        Self {
            physics,
            body: Rc::new(RefCell::new(body)),
            goals,
            doors: None,
            recorder: None,
            need: Rc::new(RefCell::new(None)),
            sealed: None,
            walk: WalkController::new(PlanMode::Inline, Some(collecting(&said))),
            said,
            narrated: Arc::new(Mutex::new(Vec::new())),
        }
    }

    /// Grids are built and routes searched on workers, as the client does.
    fn threaded(mut self) -> Self {
        self.walk = WalkController::new(PlanMode::Threads, Some(collecting(&self.said)));
        self
    }

    /// The walk's uses are recorded.
    fn recording_uses(mut self) -> Self {
        self.recorder = Some(UseRecorder::default());
        self
    }

    fn used(&self) -> Vec<u32> {
        self.recorder
            .as_ref()
            .map_or_else(Vec::new, |r| r.used.clone())
    }

    /// Narration goes to `narrated` from now on.
    fn narrating(mut self) -> Self {
        self.walk.narration = Some(collecting(&self.narrated));
        self
    }

    /// Cells for which `sealed` says so lie in a sealed dungeon.
    fn sealed(mut self, sealed: impl Fn(u32) -> bool + 'static) -> Self {
        self.sealed = Some(Box::new(sealed));
        self
    }

    fn said(&self) -> Vec<String> {
        self.said.lock().unwrap().clone()
    }

    fn narrated(&self) -> Vec<String> {
        self.narrated.lock().unwrap().clone()
    }

    fn with_doors(mut self, mut doors: FakeDoors) -> Self {
        let open = Rc::clone(&doors.open);
        if self.body.borrow().obstacle.is_some() {
            self.body.borrow_mut().obstacle_active = Some(Rc::new(move || !open.get()));
        }
        doors.body = Some(Rc::clone(&self.body));
        self.doors = Some(doors);
        self
    }

    fn tick(&mut self) {
        let mut handle = BodyHandle(Rc::clone(&self.body));
        let need = Rc::clone(&self.need);
        let paused = move || need.borrow().clone();
        let sealed = self.sealed.as_deref();
        let mut world = WalkWorld {
            physics: &self.physics,
            body: &mut handle,
            goals: &self.goals,
            doors: match (&mut self.doors, &mut self.recorder) {
                (Some(doors), _) => Some(doors as &mut dyn NavDoors),
                (None, Some(recorder)) => Some(recorder as &mut dyn NavDoors),
                (None, None) => None,
            },
            paused_by: Some(&paused),
            is_sealed_dungeon: sealed.map(|f| f as &dyn Fn(u32) -> bool),
        };
        self.walk.tick(&mut world, f64::from(FRAME));
    }

    /// A path preview over the world as it stands (searched inline).
    fn preview(&mut self, target: walk::PreviewTarget, arrival: f32) -> walk::NavPlan {
        let mut handle = BodyHandle(Rc::clone(&self.body));
        let sealed = self.sealed.as_deref();
        let world = WalkWorld {
            physics: &self.physics,
            body: &mut handle,
            goals: &self.goals,
            doors: None,
            paused_by: None,
            is_sealed_dungeon: sealed.map(|f| f as &dyn Fn(u32) -> bool),
        };
        self.walk.preview_path(&world, target, arrival).take()
    }

    fn integrate(&self, seconds: f32) {
        self.body.borrow_mut().integrate(seconds);
    }

    fn report(&self) -> WalkReport {
        self.walk.report().clone()
    }

    fn run_until(
        &mut self,
        done: impl Fn(&WalkReport, &SimBody) -> bool,
        seconds: f32,
    ) -> WalkReport {
        let mut simulated = 0.0;
        let mut ticks = 0;
        while simulated < seconds && ticks < 200_000 {
            ticks += 1;
            self.tick();
            let report = self.report();
            if done(&report, &self.body.borrow()) {
                return report;
            }
            if report.state == WalkState::Planning || self.walk.is_searching() {
                continue;
            }
            self.integrate(FRAME);
            simulated += FRAME;
        }
        self.report()
    }

    fn run_until_settled(&mut self, seconds: f32) -> WalkReport {
        self.run_until(
            |report, _| {
                !matches!(
                    report.state,
                    WalkState::Planning | WalkState::Walking | WalkState::Waiting
                )
            },
            seconds,
        )
    }

    /// Ticks through a stretch of time without moving the body.
    fn tick_for(&mut self, seconds: f32) {
        let mut simulated = 0.0;
        while simulated < seconds {
            self.tick();
            simulated += FRAME;
        }
    }

    fn run(&mut self, seconds: f32) {
        let mut simulated = 0.0;
        while simulated < seconds {
            self.tick();
            self.integrate(FRAME);
            simulated += FRAME;
        }
    }
}

fn goals_with(entries: &[(u32, Vec3)]) -> Goals {
    Goals {
        at: entries.iter().copied().collect(),
        ..Goals::default()
    }
}

fn harness(goals: &[(u32, Vec3)]) -> Harness {
    Harness::new(
        flat_world(1),
        SimBody::at(v(40.0, 40.0, 0.0)),
        goals_with(goals),
    )
}

fn in_range(value: f32, low: f32, high: f32) {
    assert!(
        value >= low && value <= high,
        "{value} is not within {low}..{high}"
    );
}

#[test]
fn a_walk_is_planned_walked_to_its_goal_and_ends_facing_it() {
    let mut h = harness(&[(TARGET, v(60.0, 75.0, 0.0))]);

    let sequence = h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);
    h.integrate(3.0);

    assert_eq!(report.sequence, sequence);
    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    let body = h.body.borrow();
    let left = body.flat().distance(Vec2::new(60.0, 75.0));
    in_range(left, 0.0, DEFAULT_ARRIVAL_METERS + 0.05);
    assert!((left - report.remaining_meters).abs() < 0.05);
    assert!(!body.travelling());
    in_range(
        body.heading_error_to(Vec2::new(60.0, 75.0)).abs(),
        0.0,
        10.5,
    );
}

fn a_walk_ends_within(arrival_meters: f32) {
    let mut h = harness(&[(TARGET, v(60.0, 75.0, 0.0))]);

    h.walk.walk_to(TARGET, arrival_meters);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    in_range(report.remaining_meters, 0.0, arrival_meters + 0.05);
}

#[test]
fn a_walk_ends_within_the_metre_it_was_asked_to_arrive_within() {
    a_walk_ends_within(1.0);
}

#[test]
fn a_walk_ends_within_the_four_metres_it_was_asked_to_arrive_within() {
    a_walk_ends_within(4.0);
}

#[test]
fn the_grid_is_let_go_after_the_idle_window_without_a_walk() {
    let mut h = harness(&[(TARGET, v(60.0, 75.0, 0.0))]);
    let released_count = std::sync::Arc::new(std::sync::atomic::AtomicI32::new(0));
    let shared = std::sync::Arc::clone(&released_count);
    h.walk.grid_released = Some(Box::new(move || {
        shared.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
    }));

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.run_until_settled(120.0);
    assert!(h.walk.grid().is_some());

    h.tick_for(GRID_IDLE_SECONDS as f32 - 1.0);
    assert!(h.walk.grid().is_some());
    assert_eq!(released_count.load(std::sync::atomic::Ordering::SeqCst), 0);

    h.tick_for(2.0);
    assert!(h.walk.grid().is_none());
    assert_eq!(released_count.load(std::sync::atomic::Ordering::SeqCst), 1);
}

#[test]
fn a_walk_inside_the_idle_window_keeps_the_grid_and_restarts_the_clock() {
    let mut h = harness(&[
        (TARGET, v(60.0, 75.0, 0.0)),
        (TARGET + 1, v(45.0, 45.0, 0.0)),
    ]);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.run_until_settled(120.0);
    h.tick_for(20.0);
    h.walk.walk_to(TARGET + 1, DEFAULT_ARRIVAL_METERS);
    h.run_until_settled(120.0);
    let kept = std::sync::Arc::clone(h.walk.grid().expect("a grid"));

    h.tick_for(20.0);
    assert!(std::sync::Arc::ptr_eq(
        &kept,
        h.walk.grid().expect("the same grid")
    ));

    h.tick_for(11.0);
    assert!(h.walk.grid().is_none());
}

#[test]
fn the_debug_view_pins_the_grid_until_it_is_turned_off() {
    let mut h = harness(&[(TARGET, v(60.0, 75.0, 0.0))]);
    h.walk.show_grid = true;

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.run_until_settled(120.0);
    h.tick_for(45.0);
    assert!(h.walk.grid().is_some());

    h.walk.show_grid = false;
    h.tick_for(31.0);
    assert!(h.walk.grid().is_none());
}

#[test]
fn a_walk_asked_for_in_the_air_waits_however_long_the_character_is_aloft_and_plans_from_where_it_lands()
 {
    let mut body = SimBody::at(v(40.0, 40.0, 3.0));
    body.airborne = true;
    let mut h = Harness::new(
        flat_world(1),
        body,
        goals_with(&[(TARGET, v(60.0, 75.0, 0.0))]),
    );

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.tick_for(12.0);

    assert_eq!(h.report().state, WalkState::Planning);
    assert_eq!(h.report().reason, "waiting for the character to land");
    assert!(h.walk.route().is_none());

    {
        let mut body = h.body.borrow_mut();
        body.position = v(40.0, 40.0, 0.0);
        body.airborne = false;
    }
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
}

#[test]
fn a_walk_waits_while_something_else_needs_the_character_and_goes_on_from_where_it_was_left() {
    let mut h = harness(&[(TARGET, v(40.0, 150.0, 0.0))]);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.run_until(|_, body| body.position.y > 60.0, 120.0);
    *h.need.borrow_mut() = Some("a plugin is running Attack".to_owned());
    h.tick();

    assert_eq!(h.report().state, WalkState::Waiting);
    assert_eq!(h.report().reason, "waiting: a plugin is running Attack");
    assert!(!h.body.borrow().travelling());
    h.body.borrow_mut().position = v(55.0, 70.0, 0.0);
    h.run(10.0);
    assert_eq!(h.report().state, WalkState::Waiting);
    assert_eq!(h.body.borrow().flat(), Vec2::new(55.0, 70.0));

    *h.need.borrow_mut() = None;
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    assert_eq!(report.replans, 0);
    in_range(
        h.body.borrow().flat().distance(Vec2::new(40.0, 150.0)),
        0.0,
        DEFAULT_ARRIVAL_METERS + 0.05,
    );
}

#[test]
fn a_grid_over_landblocks_that_have_all_unloaded_is_let_go() {
    let mut h = Harness::new(
        flat_world(2),
        SimBody::at(v(40.0, 40.0, 0.0)),
        goals_with(&[(TARGET, v(40.0, 180.0, 0.0))]),
    );
    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    assert_eq!(h.run_until_settled(120.0).state, WalkState::Arrived);
    assert_eq!(h.walk.grid().expect("a grid").landblock_ids().len(), 2);

    h.physics.remove_landblock(0xA9B4_FFFF);
    h.tick();
    assert!(
        h.walk.grid().is_some(),
        "a grid still partly loaded is kept"
    );

    h.physics.remove_landblock(0xA9B5_FFFF);
    h.tick();
    assert!(h.walk.grid().is_none());
}

#[test]
fn a_route_asked_for_alone_is_found_without_moving_the_character() {
    let mut h = harness(&[(TARGET, v(60.0, 75.0, 0.0))]);

    h.walk.route_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Planned);
    assert_eq!(
        h.walk.route().expect("a route").outcome,
        ac_nav::NavRouteOutcome::Routed
    );
    assert_eq!(h.body.borrow().moves_begun, 0);
}

#[test]
fn stopping_a_walk_ends_it_and_stops_its_moves() {
    let mut h = harness(&[(TARGET, v(40.0, 150.0, 0.0))]);
    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.run_until(|report, _| report.state == WalkState::Walking, 120.0);
    h.run(1.0);

    h.walk.stop();
    h.tick();

    assert_eq!(h.report().state, WalkState::Stopped);
    assert!((h.report().remaining_meters - (150.0 - h.body.borrow().position.y)).abs() < 0.05);
    assert!(!h.body.borrow().travelling());
    assert!(!h.walk.is_busy());
}

#[test]
fn what_is_left_is_measured_to_where_the_object_stands_when_the_walk_ends() {
    let mut h = harness(&[(TARGET, v(40.0, 150.0, 0.0))]);
    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.run_until(|report, _| report.state == WalkState::Walking, 120.0);

    h.goals.at.insert(TARGET, v(40.0, 120.0, 0.0));
    h.walk.stop();
    h.tick();

    assert_eq!(h.report().state, WalkState::Stopped);
    assert!((h.report().remaining_meters - (120.0 - h.body.borrow().position.y)).abs() < 0.05);
}

#[test]
fn the_player_moving_the_character_ends_the_walk() {
    let mut h = harness(&[(TARGET, v(40.0, 150.0, 0.0))]);
    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.run_until(|report, _| report.state == WalkState::Walking, 120.0);
    h.run(1.0);

    h.body.borrow_mut().interrupt();
    h.tick();

    assert_eq!(h.report().state, WalkState::Interrupted);
    assert!(!h.walk.is_busy());
}

#[test]
fn the_player_moving_ends_a_walk_that_is_still_planning() {
    let mut body = SimBody::at(v(40.0, 40.0, 3.0));
    body.airborne = true;
    let mut h = Harness::new(
        flat_world(1),
        body,
        goals_with(&[(TARGET, v(60.0, 75.0, 0.0))]),
    );
    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.tick_for(1.0);
    assert_eq!(h.report().state, WalkState::Planning);

    h.body.borrow_mut().player_movement_input_frames += 1;
    h.tick();

    assert_eq!(h.report().state, WalkState::Interrupted);
    assert!(!h.walk.is_busy());
}

#[test]
fn a_character_that_cannot_move_plans_again_then_gives_up_naming_what_blocked_it() {
    let mut body = SimBody::at(v(40.0, 40.0, 0.0));
    body.stuck = true;
    let mut goals = goals_with(&[(TARGET, v(40.0, 150.0, 0.0))]);
    goals.blocker = Some(NavBlocker {
        object_id: DOOR,
        name: "Door".to_owned(),
        is_closed_door: true,
        ..NavBlocker::default()
    });
    let mut h = Harness::new(flat_world(1), body, goals);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Blocked, "{}", report.reason);
    assert_eq!(report.replans, MAXIMUM_REPLANS);
    assert_eq!(report.blocked_by_object_id, DOOR);
    assert!(
        report.reason.contains("a closed door, Door (0x7A000001)"),
        "{}",
        report.reason
    );
    assert!((report.remaining_meters - 110.0).abs() < 0.05);
    assert!(!h.body.borrow().travelling());
}

#[test]
fn a_newer_request_replaces_the_walk_under_way() {
    let mut h = harness(&[(TARGET, v(40.0, 150.0, 0.0)), (OTHER, v(70.0, 40.0, 0.0))]);
    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.run_until(|report, _| report.state == WalkState::Walking, 120.0);

    let second = h.walk.walk_to(OTHER, DEFAULT_ARRIVAL_METERS);
    let settled = h.run_until_settled(120.0);

    assert_eq!(settled.sequence, second);
    assert_eq!(settled.state, WalkState::Arrived, "{}", settled.reason);
    in_range(
        h.body.borrow().flat().distance(Vec2::new(70.0, 40.0)),
        0.0,
        DEFAULT_ARRIVAL_METERS + 0.75,
    );
}

#[test]
fn an_object_the_client_cannot_place_has_no_route_and_no_distance() {
    let mut h = harness(&[]);

    h.walk.walk_to(OTHER, DEFAULT_ARRIVAL_METERS);
    h.tick();

    assert_eq!(h.report().state, WalkState::NoRoute);
    assert!(
        h.report().reason.contains("0x50000002"),
        "{}",
        h.report().reason
    );
    assert!(h.report().remaining_meters.is_nan());
}

#[test]
fn a_goal_too_far_away_for_one_region_is_walked_to_in_stages() {
    let mut h = Harness::new(
        flat_world(3),
        SimBody::at(v(40.0, 40.0, 0.0)),
        goals_with(&[(TARGET, v(60.0, 520.0, 0.0))]),
    );

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(300.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    in_range(
        h.body.borrow().flat().distance(Vec2::new(60.0, 520.0)),
        0.0,
        DEFAULT_ARRIVAL_METERS + 0.05,
    );
    assert_eq!(report.replans, 0);
}

#[test]
fn a_walk_toward_a_goal_beyond_the_loaded_world_ends_where_it_can_get_no_nearer() {
    let mut h = harness(&[(TARGET, v(40.0, 900.0, 0.0))]);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(200.0);

    assert_eq!(report.state, WalkState::NoRoute);
    assert!(
        report.reason.contains("no way on toward the goal"),
        "{}",
        report.reason
    );
    assert!(h.body.borrow().position.y > 150.0);
}

#[test]
fn a_stage_region_holds_the_character_and_reaches_toward_the_goal() {
    let from = v(500.0, 500.0, 0.0);
    let margin = REGION_MARGIN - 0.25;
    for goal in [
        v(1500.0, 500.0, 0.0),
        v(-400.0, -600.0, 0.0),
        v(700.0, 1400.0, 0.0),
    ] {
        let (origin_x, origin_y, size) = walk::choose_stage_region(from, goal);

        assert_eq!(size, MAXIMUM_REGION);
        in_range(from.x - origin_x, margin, size - margin);
        in_range(from.y - origin_y, margin, size - margin);
        let centre = Vec2::new(origin_x + size * 0.5, origin_y + size * 0.5);
        assert!(
            centre.distance(goal.truncate()) < from.truncate().distance(goal.truncate()) - 100.0
        );
    }
}

#[test]
fn a_planning_region_holds_both_ends_with_room_around_them() {
    let from = v(10.0, 10.0, 0.0);
    let to = v(130.0, 40.0, 0.0);

    let (origin_x, origin_y, size) = walk::try_choose_region(from, to).expect("a region");

    let margin = REGION_MARGIN - 0.25;
    for end in [from, to] {
        in_range(end.x - origin_x, margin, size - margin);
        in_range(end.y - origin_y, margin, size - margin);
    }
    assert!(walk::try_choose_region(from, v(400.0, 10.0, 0.0)).is_none());
}

fn door(position: Vec3, radius: f32) -> NavDoor {
    NavDoor {
        object_id: DOOR,
        name: "Door".to_owned(),
        position,
        radius,
    }
}

#[test]
fn a_closed_door_on_the_route_is_opened_and_walked_through() {
    let mut body = SimBody::at(v(40.0, 40.0, 0.0));
    body.obstacle = Some((Vec2::new(40.0, 60.0), 0.3));
    let doors = FakeDoors::new(door(Vec3::ZERO, 0.0), Vec2::new(40.0, 60.0), true);
    let mut h = Harness::new(
        flat_world(1),
        body,
        goals_with(&[(TARGET, v(40.0, 80.0, 0.0))]),
    )
    .with_doors(doors);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    let doors = h.doors.as_ref().unwrap();
    assert_eq!(doors.uses, 1);
    assert_eq!(doors.dropped_uses, 0);
    assert_eq!(report.replans, 0);
    assert!(h.body.borrow().position.y > 60.0);
}

#[test]
fn a_door_is_walked_up_to_before_it_is_used_so_the_client_need_not_walk_in() {
    let mut body = SimBody::at(v(40.0, 40.0, 0.0));
    body.obstacle = Some((Vec2::new(40.0, 60.0), 0.3));
    let mut doors = FakeDoors::new(door(v(40.0, 60.0, 0.0), 0.3), Vec2::new(40.0, 60.0), true);
    doors.reach = 1.2;
    let mut h = Harness::new(
        flat_world(1),
        body,
        goals_with(&[(TARGET, v(40.0, 80.0, 0.0))]),
    )
    .with_doors(doors);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    let doors = h.doors.as_ref().unwrap();
    assert_eq!(doors.uses, 1);
    assert_eq!(doors.dropped_uses, 0);
    assert_eq!(doors.used_from.len(), 1);
    assert!(
        doors.used_from[0] <= 1.2,
        "the door was used from {:.2} m away, outside the client's 1.20 m use range",
        doors.used_from[0]
    );
}

#[test]
fn a_door_that_will_not_open_ends_the_walk_blocked_naming_it() {
    let mut body = SimBody::at(v(40.0, 40.0, 0.0));
    body.obstacle = Some((Vec2::new(40.0, 60.0), 0.3));
    let doors = FakeDoors::new(door(Vec3::ZERO, 0.0), Vec2::new(40.0, 60.0), false);
    let mut h = Harness::new(
        flat_world(1),
        body,
        goals_with(&[(TARGET, v(40.0, 80.0, 0.0))]),
    )
    .with_doors(doors);
    // This door never opens, so the body meets it whatever the client says.
    h.body.borrow_mut().obstacle_active = None;

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Blocked, "{}", report.reason);
    assert_eq!(report.blocked_by_object_id, DOOR);
    assert!(
        report.reason.contains("would not open: Door (0x7A000001)"),
        "{}",
        report.reason
    );
    assert_eq!(h.doors.as_ref().unwrap().uses, 1);
    assert!(!h.body.borrow().travelling());
}

fn locked_door_room(doorways: &[f32], locked: Option<bool>) -> Harness {
    let mut body = SimBody::at(v(5.0, 5.0, -30.0));
    body.obstacle = Some((Vec2::new(5.0, 10.0), 0.5));
    let mut doors = FakeDoors::new(door(v(5.0, 10.0, -30.0), 0.5), Vec2::new(5.0, 10.0), false);
    doors.locked_when_appraised = locked;
    let h = Harness::new(
        room_with_doorways(doorways),
        body,
        goals_with(&[(TARGET, v(5.0, 15.0, -30.0))]),
    )
    .with_doors(doors);
    h.body.borrow_mut().obstacle_active = None;
    h
}

#[test]
fn a_locked_door_is_walked_around_without_being_tried_when_another_way_arrives() {
    let mut h = locked_door_room(&[5.0, 15.0], Some(true));

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    let doors = h.doors.as_ref().unwrap();
    assert_eq!(doors.appraisals, 1);
    assert_eq!(doors.uses, 0);
    assert!(h.body.borrow().position.y > 12.0);
}

#[test]
fn a_locked_door_on_the_only_way_ends_the_walk_blocked_naming_it_without_trying_it() {
    let mut h = locked_door_room(&[5.0], Some(true));

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Blocked, "{}", report.reason);
    assert_eq!(report.blocked_by_object_id, DOOR);
    assert!(
        report
            .reason
            .contains("a locked door: Door (0x7A000001), and no other way around it was found"),
        "{}",
        report.reason
    );
    assert_eq!(h.doors.as_ref().unwrap().uses, 0);
}

#[test]
fn a_door_that_will_not_open_is_walked_around_when_another_way_arrives() {
    let mut h = locked_door_room(&[5.0, 15.0], None);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    assert_eq!(h.doors.as_ref().unwrap().uses, 1);
    assert!(h.body.borrow().position.y > 12.0);
}

#[test]
fn following_something_that_is_not_a_player_ends_at_once() {
    let mut h = harness(&[(OTHER, v(60.0, 60.0, 0.0))]);

    h.walk.follow(OTHER, 3.0);
    h.tick();

    assert_eq!(h.report().state, WalkState::NoRoute);
    assert!(
        h.report().reason.contains("only players can be followed"),
        "{}",
        h.report().reason
    );
    assert!(!h.walk.is_busy());
}

#[test]
fn following_a_player_keeps_behind_them_and_goes_on_when_they_move() {
    let mut goals = goals_with(&[(OTHER, v(40.0, 70.0, 0.0))]);
    goals.players.insert(OTHER);
    let mut h = Harness::new(flat_world(1), SimBody::at(v(40.0, 40.0, 0.0)), goals);

    h.walk.follow(OTHER, 3.0);
    h.run_until(
        |report, _| report.reason.starts_with("keeping behind"),
        120.0,
    );
    assert_eq!(h.report().state, WalkState::Walking);
    in_range(
        h.body.borrow().flat().distance(Vec2::new(40.0, 70.0)),
        0.0,
        3.05,
    );
    assert!(h.walk.is_busy(), "a follow never ends by itself");

    h.goals.at.insert(OTHER, v(40.0, 110.0, 0.0));
    h.run_until(
        |report, body| report.reason.starts_with("keeping behind") && body.position.y > 100.0,
        120.0,
    );
    in_range(
        h.body.borrow().flat().distance(Vec2::new(40.0, 110.0)),
        0.0,
        3.05,
    );
}

/// A request reports planning at once, and plans on the next quantum.
#[test]
fn a_walk_report_starts_planning_on_the_next_frame() {
    let mut h = harness(&[(TARGET, v(60.0, 75.0, 0.0))]);
    let sequence = h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);

    assert_eq!(h.report().sequence, sequence);
    assert_eq!(h.report().state, WalkState::Planning);
    assert_eq!(h.report().reason, "waiting for the next frame");
    assert!(h.walk.is_busy());
}

// ── the cases ported with the runtime wiring ───────────────────────────

/// A sealed dungeon's grid is let go once the dungeon's landblock has
/// unloaded, as when the character portals out.
#[test]
fn a_dungeons_grid_is_let_go_once_the_dungeon_has_unloaded() {
    const ROOM: u32 = 0xA9B4_0100;
    let mut body = SimBody::at(v(20.0, 20.0, -30.0));
    body.cell_id = ROOM;
    let mut h = Harness::new(
        dungeon_world(ROOM, 230.0),
        body,
        goals_with(&[(TARGET, v(240.0, 32.0, -30.0))]),
    )
    .sealed(|cell| cell == ROOM);
    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);
    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    assert!(h.walk.grid().is_some());

    h.physics.remove_landblock(0xA9B4_FFFF);
    h.tick();

    assert!(h.walk.grid().is_none());
}

/// Whoever listens to a walk's narration hears every line the walk
/// reports, and on top of them where it started and headed and the points
/// of the route it found.
#[test]
fn a_walks_narration_carries_its_reports_and_where_it_starts_heads_and_goes() {
    let mut body = SimBody::at(v(40.0, 40.0, 0.0));
    body.cell_id = 0xA9B4_0021;
    let mut h = Harness::new(
        flat_world(1),
        body,
        goals_with(&[(TARGET, v(40.0, 150.0, 0.0))]),
    )
    .narrating();

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    assert_eq!(h.run_until_settled(120.0).state, WalkState::Arrived);

    let said = h.said();
    let narrated = h.narrated();
    assert!(!said.is_empty());
    let heard: Vec<&String> = narrated.iter().filter(|line| said.contains(line)).collect();
    assert_eq!(heard, said.iter().collect::<Vec<_>>());
    assert!(narrated.iter().any(|line| line.contains(
        "from (40.0, 40.0, 0.0) in cell 0xA9B40021 toward (40.0, 150.0, 0.0), within 2.5 m"
    )));
    assert!(
        narrated
            .iter()
            .any(|line| line.starts_with("Route points: (")
                && line.contains("ends in sight of the goal"))
    );
    assert!(!said.iter().any(|line| line.starts_with("Route points:")));
}

/// Narration writes its numbers with a decimal point and no grouping: the
/// numbers are for the log and the tests that read it. (OpenAC runs this
/// under a comma-decimal culture; Rust's formatting has no culture.)
#[test]
fn narration_writes_its_numbers_with_a_decimal_point() {
    let mut body = SimBody::at(v(40.0, 40.0, 0.0));
    body.cell_id = 0xA9B4_0021;
    let mut h = Harness::new(
        flat_world(1),
        body,
        goals_with(&[(TARGET, v(40.0, 150.0, 0.0))]),
    )
    .narrating();

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    assert_eq!(h.run_until_settled(120.0).state, WalkState::Arrived);

    let narrated = h.narrated();
    assert!(
        narrated
            .iter()
            .any(|line| line.contains("toward (40.0, 150.0, 0.0), within 2.5 m"))
    );
    assert!(narrated.iter().any(|line| {
        line.starts_with("Route: ")
            && line
                .split(" legs, ")
                .nth(1)
                .and_then(|rest| rest.split(" m, ").next())
                .is_some_and(|metres| {
                    metres.split_once('.').is_some_and(|(whole, tenth)| {
                        !whole.is_empty()
                            && whole.chars().all(|c| c.is_ascii_digit())
                            && tenth.len() == 1
                    })
                })
    }));
    let comma_decimal = |line: &String| {
        line.as_bytes()
            .windows(3)
            .any(|w| w[0].is_ascii_digit() && w[1] == b',' && w[2].is_ascii_digit())
    };
    assert!(!narrated.iter().any(comma_decimal));
}

/// A goal walled off from everywhere the character can reach is walked to
/// its nearest spot, and the walk says it ended without a line of sight
/// rather than that it arrived.
#[test]
fn a_walk_that_can_only_end_out_of_sight_of_its_goal_does_not_say_it_arrived() {
    let mut h = Harness::new(
        impassable_room_wall(),
        SimBody::at(v(10.0, 4.0, -30.0)),
        goals_with(&[(TARGET, v(10.0, 16.0, -30.0))]),
    );

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(
        report.state,
        WalkState::ArrivedWithoutSight,
        "{}",
        report.reason
    );
    assert!(
        report.reason.contains("without a line of sight"),
        "{}",
        report.reason
    );
    assert!(h.body.borrow().position.y < 10.0);
}

/// The player's own movement ends a walk whatever it is doing, here while
/// it still plans, before any move has begun.
#[test]
fn the_player_moving_ends_a_walk_that_is_still_planning_on_open_ground() {
    let mut h = harness(&[(TARGET, v(40.0, 150.0, 0.0))]);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.tick();
    assert_eq!(h.report().state, WalkState::Planning);
    h.body.borrow_mut().player_movement_input_frames += 1;
    h.tick();

    assert_eq!(h.report().state, WalkState::Interrupted);
    assert!(!h.walk.is_busy());
    h.run(5.0);
    assert_eq!(h.body.borrow().moves_begun, 0);
}

/// While a walk waits for something else that needs the character, the
/// player moving still ends it.
#[test]
fn the_player_moving_ends_a_walk_that_is_waiting() {
    let mut h = harness(&[(TARGET, v(40.0, 150.0, 0.0))]);
    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.run_until(|report, _| report.state == WalkState::Walking, 120.0);
    *h.need.borrow_mut() = Some("a plugin is running Attack".to_owned());
    h.run(1.0);
    assert_eq!(h.report().state, WalkState::Waiting);
    let moves = h.body.borrow().moves_begun;

    h.body.borrow_mut().player_movement_input_frames += 1;
    h.tick();
    *h.need.borrow_mut() = None;
    h.run(5.0);

    assert_eq!(h.report().state, WalkState::Interrupted);
    assert_eq!(h.body.borrow().moves_begun, moves);
}

/// Input the player gave before a walk was asked for does not end it.
#[test]
fn the_player_having_moved_before_a_walk_began_does_not_end_it() {
    let mut body = SimBody::at(v(40.0, 40.0, 0.0));
    body.player_movement_input_frames = 40;
    let mut h = Harness::new(
        flat_world(1),
        body,
        goals_with(&[(TARGET, v(40.0, 150.0, 0.0))]),
    );

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);

    assert_eq!(h.run_until_settled(120.0).state, WalkState::Arrived);
}

#[test]
fn a_walk_sets_off_again_only_once_nothing_has_needed_the_character_for_a_moment() {
    let mut h = harness(&[(TARGET, v(40.0, 150.0, 0.0))]);
    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.run_until(|report, _| report.state == WalkState::Walking, 120.0);
    *h.need.borrow_mut() = Some("a plugin is running Attack".to_owned());
    h.run(1.0);
    let moves = h.body.borrow().moves_begun;

    for _ in 0..5 {
        *h.need.borrow_mut() = None;
        h.run(walk::PAUSE_SETTLE_SECONDS as f32 * 0.5);
        *h.need.borrow_mut() = Some("a plugin is running LootCorpseIdle".to_owned());
        h.run(0.2);
    }

    assert_eq!(h.report().state, WalkState::Waiting);
    assert_eq!(
        h.report().reason,
        "waiting: a plugin is running LootCorpseIdle"
    );
    assert_eq!(h.body.borrow().moves_begun, moves);
    *h.need.borrow_mut() = None;
    assert_eq!(h.run_until_settled(120.0).state, WalkState::Arrived);
}

#[test]
fn a_route_asked_for_alone_is_found_while_something_else_needs_the_character() {
    let mut h = harness(&[(TARGET, v(60.0, 75.0, 0.0))]);
    *h.need.borrow_mut() = Some("a plugin is running Attack".to_owned());

    h.walk.route_to(TARGET, DEFAULT_ARRIVAL_METERS);

    assert_eq!(h.run_until_settled(120.0).state, WalkState::Planned);
}

#[test]
fn a_place_known_only_by_its_map_coordinates_lies_where_its_cell_places_it() {
    let here = v(15.259, -39.938, 0.005);
    let by_cell =
        ac_nav::sources::place_offset(0x019E_0114, v(10.0, -40.0, 0.005), 0x019E_0123, here);
    let by_map = ac_nav::sources::place_offset(0, v(202.0, 30296.0, 0.005), 0x019E_0123, here);

    assert!((by_cell.x - -5.259).abs() < 1e-3);
    assert!((by_cell.y - -0.062).abs() < 1e-3);
    assert!((by_cell.x - by_map.x).abs() < 1e-2);
    assert!((by_cell.y - by_map.y).abs() < 1e-2);
    assert!((by_cell.z - by_map.z).abs() < 1e-3);
}

/// A grid is built before the server has placed everything around the
/// character, as a dungeon's is on arrival. An object placed after it is
/// floor the next walk stands on.
#[test]
fn an_object_placed_after_the_grid_was_built_is_floor_the_next_walk_stands_on() {
    const CRATE: u32 = 0x8000_2AB0;
    let mut h = Harness::new(
        flat_world(1),
        SimBody::at(v(40.0, 40.0, 0.0)),
        Goals::default(),
    );
    h.walk.walk_to_place(0xA9B4_0001, v(45.0, 40.0, 0.0), 1.0);
    assert_eq!(h.run_until_settled(120.0).state, WalkState::Arrived);

    let (shadows, world) = h.physics.shadows_and_world();
    shadows
        .register(
            world,
            CRATE,
            &ac_physics::SingleShape {
                gfx_obj_id: 0,
                radius: 2.0,
                collision_type: ac_physics::ShadowCollisionType::Cylinder,
                cyl_height: 0.5,
                scale: 1.0,
            },
            ac_physics::RegistrationSite {
                world_pos: v(60.0, 40.0, 0.0),
                rotation: glam::Quat::IDENTITY,
                world_offset_x: 0.0,
                world_offset_y: 0.0,
                landblock_id: 0xA9B4_FFFF,
                seed_cell_id: 0xA9B4_0001,
            },
            0,
            ac_physics::EntityCollisionFlags::default(),
            false,
            None,
        )
        .expect("the crate registers");
    h.goals.standing.insert(CRATE);
    h.walk.walk_to_place(0xA9B4_0001, v(60.0, 40.0, 0.5), 1.0);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    let last = *h.walk.route().expect("a route").legs.last().expect("legs");
    assert!((last.z - 0.5).abs() < 0.05, "ends on the crate at {last}");
}

#[test]
fn a_walk_to_a_place_arrives_there_without_turning_to_face_it() {
    let mut h = Harness::new(
        flat_world(1),
        SimBody::at(v(40.0, 40.0, 0.0)),
        Goals::default(),
    );

    h.walk.walk_to_place(0xA9B4_0001, v(60.0, 75.0, 0.0), 2.0);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    assert_eq!(report.object_id, 0);
    in_range(
        h.body.borrow().flat().distance(Vec2::new(60.0, 75.0)),
        0.0,
        2.05,
    );
    assert!(!h.body.borrow().travelling());
}

fn keeping_behind(report: &WalkReport, _: &SimBody) -> bool {
    report.reason.starts_with("keeping behind")
}

fn walking_on(report: &WalkReport, _: &SimBody) -> bool {
    report.state == WalkState::Walking && report.reason == "walking"
}

fn following(goals: Goals) -> Harness {
    Harness::new(flat_world(1), SimBody::at(v(40.0, 40.0, 0.0)), goals)
}

fn player_at(at: Vec3) -> Goals {
    let mut goals = goals_with(&[(TARGET, at)]);
    goals.players.insert(TARGET);
    goals
}

/// A walk to a place ends on the floor that place stands on. A slab too
/// high to step onto, with no leap to reach it, is not arrived at from the
/// ground beside it; a walk to an object standing on the same slab still
/// ends beside it, as walks to objects do.
#[test]
fn a_walk_to_a_place_on_a_top_it_cannot_get_onto_finds_no_route_where_a_walk_to_an_object_there_arrives_beside()
 {
    let goal = v(52.0, 52.0, 1.0);
    let mut place = Harness::new(
        raised_slab_world(),
        SimBody::at(v(40.0, 40.0, 0.0)),
        Goals::default(),
    );
    place.walk.walk_to_place(0xA9B4_0001, goal, 2.5);
    let place_report = place.run_until_settled(120.0);

    let mut object = Harness::new(
        raised_slab_world(),
        SimBody::at(v(40.0, 40.0, 0.0)),
        goals_with(&[(TARGET, goal)]),
    );
    object.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let object_report = object.run_until_settled(120.0);

    assert_eq!(
        place_report.state,
        WalkState::NoRoute,
        "{}",
        place_report.reason
    );
    assert!(
        place_report.reason.contains("floor"),
        "{}",
        place_report.reason
    );
    assert_eq!(
        object_report.state,
        WalkState::Arrived,
        "{}",
        object_report.reason
    );
}

/// Standing on an object ends on its top. A slab too high to step onto,
/// with no leap to reach it, is not stood on from the ground beside it.
#[test]
fn standing_on_a_top_it_cannot_get_onto_finds_no_route_where_a_walk_to_it_arrives_beside() {
    let mut goals = goals_with(&[(TARGET, v(52.0, 52.0, 0.0))]);
    goals.surfaces.insert(
        TARGET,
        NavSurfaces {
            triangles: slab_top(),
            cylinders: Vec::new(),
        },
    );
    let mut h = Harness::new(raised_slab_world(), SimBody::at(v(40.0, 40.0, 0.0)), goals);

    h.walk.stand_on(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::NoRoute, "{}", report.reason);
    assert!(
        report.reason.contains("top of the object"),
        "{}",
        report.reason
    );
    assert_eq!(h.body.borrow().moves_begun, 0);
}

/// An object with a ramp up to its deck is stood on at the deck, not
/// partway up the ramp.
#[test]
fn standing_on_an_object_with_a_ramp_walks_up_it_and_ends_on_the_deck() {
    let mut goals = goals_with(&[(TARGET, v(52.0, 53.0, 0.0))]);
    goals.surfaces.insert(
        TARGET,
        NavSurfaces {
            triangles: ramp_deck().iter().flat_map(|p| triangles(p)).collect(),
            cylinders: Vec::new(),
        },
    );
    let mut h = Harness::new(
        cell_world(&ramp_deck()),
        SimBody::at(v(52.0, 36.0, 0.0)),
        goals,
    );

    h.walk.stand_on(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    let last = *h.walk.route().expect("a route").legs.last().expect("legs");
    assert!((last.z - 2.0).abs() < 0.05, "ends at {last}");
    in_range(h.body.borrow().position.y, 51.0, 56.0);
    assert!(!h.body.borrow().travelling());
}

/// An object whose collision is in the grid, such as a life stone, hides
/// its own middle, and a portal nearby leaves no fallback to end out of
/// sight. The walk still arrives beside it.
#[test]
fn a_walk_to_an_object_whose_collision_hides_its_middle_arrives_beside_it() {
    let mut goals = goals_with(&[(TARGET, v(52.0, 52.0, 0.0))]);
    goals.surfaces.insert(
        TARGET,
        NavSurfaces {
            triangles: stone_block().iter().flat_map(|p| triangles(p)).collect(),
            cylinders: Vec::new(),
        },
    );
    goals
        .portals
        .push(NavAvoidance::new(v(40.0, 60.0, 0.0), 1.0));
    let mut h = Harness::new(
        cell_world(&stone_block()),
        SimBody::at(v(40.0, 40.0, 0.0)),
        goals,
    );

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    in_range(
        h.body.borrow().flat().distance(Vec2::new(52.0, 52.0)),
        1.5,
        5.0,
    );
}

/// A debugging listener hears what a walk found of its object's collision,
/// what it searched for, and for a walk that found no route, what stood
/// around the goal.
#[test]
fn a_narrated_walk_that_finds_no_route_says_what_it_looked_at() {
    let mut goals = goals_with(&[(TARGET, v(52.0, 52.0, 0.0))]);
    goals.surfaces.insert(
        TARGET,
        NavSurfaces {
            triangles: slab_top(),
            cylinders: Vec::new(),
        },
    );
    let mut h =
        Harness::new(raised_slab_world(), SimBody::at(v(40.0, 40.0, 0.0)), goals).narrating();

    h.walk.stand_on(TARGET, DEFAULT_ARRIVAL_METERS);
    h.run_until_settled(120.0);

    let heard = h.narrated();
    assert!(
        heard
            .iter()
            .any(|l| l.contains("collision of 2 triangles and 0 cylinders"))
    );
    assert!(heard.iter().any(|l| l.contains(": searching for ")));
    assert!(heard.iter().any(|l| l.starts_with("No route to 0x")));
    assert!(heard.iter().any(|l| l.starts_with("Around the goal at ")));
}

/// A player walking on while the follower is on its way is chased without
/// stopping to plan again.
#[test]
fn following_a_player_who_keeps_moving_replans_on_the_way() {
    let mut h = following(player_at(v(40.0, 90.0, 0.0))).narrating();

    h.walk.follow(TARGET, 3.0);
    h.run_until(walking_on, 120.0);
    h.run(1.0);
    h.goals.at.insert(TARGET, v(70.0, 100.0, 0.0));
    h.run_until(keeping_behind, 120.0);

    in_range(
        h.body.borrow().flat().distance(Vec2::new(70.0, 100.0)),
        0.0,
        4.5,
    );
    assert!(h.narrated().iter().any(|l| l.contains("the player moved")));
}

#[test]
fn following_a_player_out_of_sight_waits_and_goes_on_once_they_are_seen() {
    let mut goals = Goals::default();
    goals.players.insert(TARGET);
    let mut h = following(goals);

    h.walk.follow(TARGET, 3.0);
    h.run(3.0);

    assert!(h.walk.is_busy());
    assert_eq!(h.report().state, WalkState::Waiting);
    assert!(
        h.report().reason.contains("waiting to see"),
        "{}",
        h.report().reason
    );

    h.goals.at.insert(TARGET, v(40.0, 70.0, 0.0));
    h.run_until(keeping_behind, 120.0);
    in_range(
        h.body.borrow().flat().distance(Vec2::new(40.0, 70.0)),
        0.0,
        4.5,
    );
}

/// A player who vanishes beside a portal went through it: the follower
/// walks into that portal, uses it, waits out portal space, and goes on
/// following once it sees the player.
#[test]
fn following_a_player_goes_through_the_portal_they_left_by() {
    const PORTAL: u32 = 0x7A00_0077;
    let mut goals = player_at(v(40.0, 70.0, 0.0));
    goals.at.insert(PORTAL, v(40.0, 74.0, 0.0));
    goals.portal_ids.push((PORTAL, v(40.0, 74.0, 0.0)));
    let mut h = following(goals).recording_uses();

    h.walk.follow(TARGET, 3.0);
    h.run_until(keeping_behind, 120.0);
    h.goals.at.remove(&TARGET);
    while !h.used().contains(&PORTAL) && h.body.borrow().seconds < 200.0 {
        h.tick();
        h.integrate(FRAME);
    }

    assert!(h.used().contains(&PORTAL));
    in_range(
        h.body.borrow().flat().distance(Vec2::new(40.0, 74.0)),
        0.0,
        1.5,
    );

    h.body.borrow_mut().in_portal_space = true;
    h.run(1.0);
    assert!(h.walk.is_busy());
    assert!(
        h.report().reason.contains("portal space"),
        "{}",
        h.report().reason
    );

    {
        let mut body = h.body.borrow_mut();
        body.in_portal_space = false;
        body.position = v(100.0, 100.0, 0.0);
    }
    h.goals.at.insert(TARGET, v(100.0, 130.0, 0.0));
    h.run_until(keeping_behind, 120.0);
    in_range(
        h.body.borrow().flat().distance(Vec2::new(100.0, 130.0)),
        0.0,
        4.5,
    );
}

/// The client keeps a player who enters a portal and moves them to where it
/// sent them, far away at once. The follower goes through the portal they
/// entered rather than turning toward where they landed.
#[test]
fn following_a_player_who_jumps_far_at_once_goes_through_the_portal_they_entered() {
    const PORTAL: u32 = 0x7A00_0077;
    let mut goals = player_at(v(40.0, 70.0, 0.0));
    goals.at.insert(PORTAL, v(40.0, 74.0, 0.0));
    goals.portal_ids.push((PORTAL, v(40.0, 74.0, 0.0)));
    let mut h =
        Harness::new(flat_world(2), SimBody::at(v(40.0, 40.0, 0.0)), goals).recording_uses();

    h.walk.follow(TARGET, 3.0);
    h.run_until(keeping_behind, 120.0);
    h.goals.at.insert(TARGET, v(40.0, 73.5, 0.0));
    h.run(0.2);
    h.goals.at.insert(TARGET, v(150.0, 300.0, 0.0));
    let started = h.body.borrow().seconds;
    while !h.used().contains(&PORTAL) && h.body.borrow().seconds < started + 60.0 {
        h.tick();
        h.integrate(FRAME);
    }

    assert!(h.used().contains(&PORTAL));
    in_range(
        h.body.borrow().flat().distance(Vec2::new(40.0, 74.0)),
        0.0,
        1.5,
    );
}

/// A player moved far away at once with no portal beside them, as by a
/// recall or a teleport, is out of reach: the follow sleeps, walking
/// nowhere, until they are within reach again. A short move is followed.
#[test]
fn following_sleeps_while_the_player_is_moved_far_away_with_no_portal() {
    let mut h = following(player_at(v(40.0, 70.0, 0.0)));
    h.walk.follow(TARGET, 3.0);
    h.run_until(keeping_behind, 120.0);

    h.goals.at.insert(TARGET, v(40.0, 120.0, 0.0));
    h.run(1.0);
    assert!(
        h.walk.is_busy(),
        "a player moved a short way at once is still followed"
    );
    h.run_until(keeping_behind, 120.0);
    let before = h.body.borrow().flat();

    h.goals.at.insert(TARGET, v(-600.0, 4800.0, 0.0));
    h.run(3.0);

    assert!(h.walk.is_busy());
    assert_eq!(h.report().state, WalkState::Waiting);
    assert!(
        h.report().reason.contains("out of reach"),
        "{}",
        h.report().reason
    );
    assert!(!h.body.borrow().travelling());
    in_range(h.body.borrow().flat().distance(before), 0.0, 1.0);

    h.goals.at.insert(TARGET, v(60.0, 140.0, 0.0));
    h.run_until(keeping_behind, 120.0);
    in_range(
        h.body.borrow().flat().distance(Vec2::new(60.0, 140.0)),
        0.0,
        4.5,
    );
}

#[test]
fn following_a_player_keeps_trying_when_the_way_is_blocked() {
    let mut body = SimBody::at(v(40.0, 40.0, 0.0));
    body.stuck = true;
    let mut h = Harness::new(flat_world(1), body, player_at(v(40.0, 150.0, 0.0)));

    h.walk.follow(TARGET, 3.0);
    let report = h.run_until(|r, _| r.reason.contains("trying again"), 200.0);

    assert!(report.reason.contains("trying again"), "{}", report.reason);
    h.run(3.0);
    assert!(h.walk.is_busy());
}

/// A follower holding behind the player goes on when the player climbs or
/// drops, as onto a platform, even without moving across.
#[test]
fn following_a_player_goes_on_when_they_climb_without_moving_across() {
    let mut h = following(player_at(v(40.0, 70.0, 0.0))).narrating();
    h.walk.follow(TARGET, 3.0);
    h.run_until(keeping_behind, 120.0);
    h.run(1.0);
    h.narrated.lock().unwrap().clear();

    h.goals.at.insert(TARGET, v(40.0, 70.0, 2.0));
    h.run(3.0);

    assert!(
        h.narrated()
            .iter()
            .any(|l| l.contains("the player moved to"))
    );
    assert!(h.walk.is_busy());
}

/// A player with no floor under them is in the air, as mid-jump, so the
/// follow waits for them to land before planning, rather than aiming at
/// whatever top stands near the arc.
#[test]
fn following_a_player_in_the_air_waits_for_them_to_land() {
    let mut h = following(player_at(v(40.0, 70.0, 0.0))).narrating();
    h.walk.follow(TARGET, 3.0);
    h.run_until(keeping_behind, 120.0);
    h.run(1.0);

    h.goals.at.insert(TARGET, v(40.0, 90.0, 1.5));
    h.run(0.5);
    assert!(
        h.report().reason.contains("waiting for the player to land"),
        "{}",
        h.report().reason
    );
    assert!(
        !h.narrated()
            .iter()
            .any(|l| l.contains("searching for (40.0, 90.0, 1.5)"))
    );

    h.goals.at.insert(TARGET, v(40.0, 90.0, 0.0));
    h.run_until(keeping_behind, 120.0);
    in_range(
        h.body.borrow().flat().distance(Vec2::new(40.0, 90.0)),
        0.0,
        4.5,
    );
}

/// A follower left standing on a top too small to walk on, with no route
/// off it, steps off toward the player and plans again from where it lands.
#[test]
fn following_from_a_top_with_no_route_off_steps_off_toward_the_player() {
    let mut h = Harness::new(
        cell_world(&post_top()),
        SimBody::at(v(40.0, 40.0, 2.0)),
        player_at(v(40.0, 55.0, 0.0)),
    )
    .narrating();
    let heard = Arc::clone(&h.narrated);

    h.walk.follow(TARGET, 3.0);
    h.run_until(
        move |_, _| {
            heard
                .lock()
                .unwrap()
                .iter()
                .any(|l| l.contains("stepping off"))
        },
        10.0,
    );

    assert!(h.narrated().iter().any(|l| l.contains("stepping off")));
    let body = h.body.borrow();
    assert!(
        body.travelling() || body.flat().y > 40.5,
        "the follower stepped off the top"
    );
}

/// A player on a block the follower can neither walk nor jump onto is not
/// followed to some other spot near them: the follower holds where it
/// stands, says why, and keeps trying.
#[test]
fn following_a_player_on_a_top_it_cannot_reach_holds_where_it_stands() {
    let mut h = Harness::new(
        cell_world(&furnace()),
        SimBody::at(v(40.0, 40.0, 0.0)),
        player_at(v(52.0, 52.0, 2.5)),
    )
    .narrating();
    let heard = Arc::clone(&h.narrated);

    h.walk.follow(TARGET, 3.0);
    h.run_until(
        move |_, _| {
            heard
                .lock()
                .unwrap()
                .iter()
                .filter(|l| l.contains("holding where the character stands"))
                .count()
                >= 2
        },
        30.0,
    );

    assert!(h.walk.is_busy());
    in_range(
        h.body.borrow().flat().distance(Vec2::new(40.0, 40.0)),
        0.0,
        0.5,
    );
    assert!(h.narrated().iter().any(|l| {
        l.contains("no route reaches the player's floor")
            && l.contains("holding where the character stands")
    }));
}

#[test]
fn the_player_moving_the_character_ends_a_follow() {
    let mut h = following(player_at(v(40.0, 70.0, 0.0)));
    h.walk.follow(TARGET, 3.0);
    h.run_until(keeping_behind, 120.0);

    h.body.borrow_mut().player_movement_input_frames += 1;
    h.tick();

    assert_eq!(h.report().state, WalkState::Interrupted);
    assert!(!h.walk.is_busy());
}

#[test]
fn standing_on_an_object_with_no_collision_finds_no_route() {
    let mut h = harness(&[(TARGET, v(52.0, 52.0, 0.0))]);

    h.walk.stand_on(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::NoRoute, "{}", report.reason);
    assert!(report.reason.contains("no collision"), "{}", report.reason);
}

/// How near a creature's middle the body comes when it stops against it.
fn reach() -> f32 {
    0.8 + NavBody::player(0.6, 1.5).radius
}

fn walls(polygons: &[[Vec3; 4]]) -> Vec<Vec<Vec3>> {
    polygons.iter().map(|p| p.to_vec()).collect()
}

/// A dead-end passage 1.6 m wide and 3 m tall: walls at x = 39.2 and
/// x = 40.8 from y = 30 to y = 60, closed at y = 30.
fn passage() -> Vec<Vec<Vec3>> {
    walls(&[
        [
            v(39.2, 30.0, 0.0),
            v(39.2, 60.0, 0.0),
            v(39.2, 60.0, 3.0),
            v(39.2, 30.0, 3.0),
        ],
        [
            v(40.8, 30.0, 0.0),
            v(40.8, 60.0, 0.0),
            v(40.8, 60.0, 3.0),
            v(40.8, 30.0, 3.0),
        ],
        [
            v(39.2, 30.0, 0.0),
            v(40.8, 30.0, 0.0),
            v(40.8, 30.0, 3.0),
            v(39.2, 30.0, 3.0),
        ],
    ])
}

/// A corridor 4 m wide and 3 m tall, walls at x = 38 and x = 42 from
/// y = 30 to y = 60.
fn corridor() -> Vec<Vec<Vec3>> {
    walls(&[
        [
            v(38.0, 30.0, 0.0),
            v(38.0, 60.0, 0.0),
            v(38.0, 60.0, 3.0),
            v(38.0, 30.0, 3.0),
        ],
        [
            v(42.0, 30.0, 0.0),
            v(42.0, 60.0, 0.0),
            v(42.0, 60.0, 3.0),
            v(42.0, 30.0, 3.0),
        ],
    ])
}

fn settled(report: &WalkReport, _: &SimBody) -> bool {
    !matches!(
        report.state,
        WalkState::Planning | WalkState::Walking | WalkState::Waiting
    )
}

/// A walk that keeps stopping tries a different way off each time: none
/// at the first stop, then a step back, a roomier spot, a sidestep each
/// way, and a hop forward. Narration says where it stuck, facing which
/// way, on which leg, and how near the walls were.
#[test]
fn a_walk_that_keeps_stopping_tries_a_different_way_off_each_time_and_says_where_it_stuck() {
    let mut body = SimBody::at(v(40.0, 40.0, 0.0));
    body.stuck = true;
    let mut h = Harness::new(
        flat_world(1),
        body,
        goals_with(&[(TARGET, v(40.0, 150.0, 0.0))]),
    )
    .narrating();

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Blocked, "{}", report.reason);
    let heard = h.narrated();
    assert!(heard.iter().any(
        |l| l.contains("stopped making progress at (40.0, 40.0, 0.0)")
            && l.contains("heading")
            && l.contains("leg 1 of")
            && l.contains("nearest wall")
    ));
    let tried: Vec<&String> = heard
        .iter()
        .filter(|l| l.contains("planning again ("))
        .collect();
    assert_eq!(tried.len(), MAXIMUM_REPLANS as usize, "{tried:#?}");
    assert!(!tried[0].contains(" first"), "{}", tried[0]);
    assert!(tried[1].contains("stepping back 1 m first"), "{}", tried[1]);
    assert!(
        tried[2].contains("finding no roomier spot nearby first"),
        "{}",
        tried[2]
    );
    assert!(tried[3].contains("sidestepping 1 m to the"), "{}", tried[3]);
    assert!(
        tried[3].contains("the more open side first"),
        "{}",
        tried[3]
    );
    assert!(tried[4].contains("sidestepping 1 m to the"), "{}", tried[4]);
    assert!(tried[5].contains("hopping forward first"), "{}", tried[5]);
    let body = h.body.borrow();
    assert!(body.travel_directions.contains(&MoveDirection::Backward));
    assert!(body.travel_directions.contains(&MoveDirection::StrafeLeft));
    assert!(body.travel_directions.contains(&MoveDirection::StrafeRight));
    assert_eq!(body.jumps, 1);
}

/// A walk stuck against the wall of a narrow corridor moves to the middle
/// of it.
#[test]
fn a_walk_stuck_against_a_corridor_wall_moves_to_the_middle_of_it() {
    let mut body = SimBody::at(v(38.7, 40.0, 0.0));
    body.stuck = true;
    let mut h = Harness::new(
        cell_world(&corridor()),
        body,
        goals_with(&[(TARGET, v(40.0, 56.0, 0.0))]),
    )
    .narrating();
    let heard = Arc::clone(&h.narrated);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.run_until(
        move |_, _| {
            heard
                .lock()
                .unwrap()
                .iter()
                .any(|l| l.contains("roomiest spot nearby at"))
        },
        60.0,
    );

    let narrated = h.narrated();
    let moving: Vec<&String> = narrated
        .iter()
        .filter(|l| l.contains("roomiest spot nearby at"))
        .collect();
    let [moving] = moving[..] else {
        panic!("one move to a roomier spot: {moving:?}");
    };
    const AT: &str = "roomiest spot nearby at (";
    let x: f32 = moving[moving.find(AT).unwrap() + AT.len()..]
        .split(',')
        .next()
        .unwrap()
        .trim()
        .parse()
        .unwrap();
    in_range(x, 39.0, 41.0);
}

/// A walk stuck in a passage so narrow that keeping out of where it stuck
/// leaves no route plans through that spot again and goes on down its
/// ladder of ways off, rather than ending at the first stop.
#[test]
fn a_walk_stuck_in_a_passage_with_no_other_way_keeps_trying_different_ways_off() {
    let mut body = SimBody::at(v(40.0, 34.0, 0.0));
    body.stuck = true;
    let mut h = Harness::new(
        cell_world(&passage()),
        body,
        goals_with(&[(TARGET, v(40.0, 56.0, 0.0))]),
    )
    .narrating();

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(200.0);

    assert_eq!(report.state, WalkState::Blocked, "{}", report.reason);
    let heard = h.narrated();
    assert!(
        heard
            .iter()
            .any(|l| l.contains("no route keeps out of where the character stuck"))
    );
    assert!(heard.iter().any(|l| l.contains("stepping back 1 m first")));
    assert!(heard.iter().any(|l| l.contains("hopping forward first")));
    assert_eq!(
        heard
            .iter()
            .filter(|l| l.contains("planning again ("))
            .count(),
        MAXIMUM_REPLANS as usize
    );
}

/// A follow stuck in such a passage keeps going round the ways off for as
/// long as it stays stuck, starting over without starting the ways off
/// over: only its very first stop tries nothing.
#[test]
fn a_follow_stuck_in_a_passage_with_no_other_way_keeps_trying_different_ways_off() {
    let mut body = SimBody::at(v(40.0, 34.0, 0.0));
    body.stuck = true;
    let mut goals = goals_with(&[(TARGET, v(40.0, 56.0, 0.0))]);
    goals.players.insert(TARGET);
    let mut h = Harness::new(cell_world(&passage()), body, goals).narrating();
    let heard = Arc::clone(&h.narrated);

    h.walk.follow(TARGET, 3.0);
    h.run_until(
        move |_, _| {
            heard
                .lock()
                .unwrap()
                .iter()
                .filter(|l| l.contains("hopping forward first"))
                .count()
                >= 3
        },
        600.0,
    );

    assert!(h.walk.is_busy());
    let narrated = h.narrated();
    let stops: Vec<&String> = narrated
        .iter()
        .filter(|l| l.contains("planning again ("))
        .collect();
    assert!(
        stops.len() > 2 * MAXIMUM_REPLANS as usize,
        "stopped {} times",
        stops.len()
    );
    assert_eq!(stops.iter().filter(|l| !l.contains(" first")).count(), 1);
    assert!(stops.iter().any(|l| l.contains("stepping back 1 m first")));
    assert!(stops.iter().any(|l| l.contains("sidestepping 1 m to the")));
    assert!(h.body.borrow().jumps >= 3);
}

#[test]
fn a_walk_runs_through_a_doorway_without_stopping_where_its_arcs_keep_to_the_floor() {
    let turning = RouteTurning {
        run_speed: 4.0,
        run_turn_degrees_per_second: 180.0,
        walk_speed: 0.0,
        walk_turn_degrees_per_second: 0.0,
    };
    let mut said = Vec::new();
    let mut stops = Vec::new();
    let mut cutting_path = Vec::new();
    for with_turning in [false, true] {
        let mut body = SimBody::at(v(4.0, 4.0, -30.0));
        if with_turning {
            body.turning = Some(turning);
        }
        let mut h = Harness::new(
            room_with_doorways(&[10.0]),
            body,
            goals_with(&[(TARGET, v(4.0, 16.0, -30.0))]),
        );
        h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
        assert_eq!(h.run_until_settled(120.0).state, WalkState::Arrived);
        said = h.said();
        stops.push(h.body.borrow().travel_stops);
        if with_turning {
            cutting_path = h.body.borrow().path.clone();
        }
    }

    assert!(
        stops[0] > 1,
        "turning in place, the walk stopped {} times",
        stops[0]
    );
    assert_eq!(stops[1], 1);
    assert!(
        said.iter()
            .any(|l| l.contains("turned in place at 0") && !l.contains("ran around 0 corners"))
    );
    for step in cutting_path.windows(2) {
        let (from, to) = (step[0], step[1]);
        if (from.y < 10.0) == (to.y < 10.0) {
            continue;
        }
        let crossing = from.x + (to.x - from.x) * ((10.0 - from.y) / (to.y - from.y));
        in_range(
            crossing,
            9.0 + ac_nav::grid::BRUSH_MARGIN,
            11.0 - ac_nav::grid::BRUSH_MARGIN,
        );
    }
}

#[test]
fn a_walk_plans_around_the_spot_where_it_was_blocked() {
    let mut body = SimBody::at(v(40.0, 40.0, 0.0));
    body.obstacle = Some((Vec2::new(40.0, 60.0), 0.3));
    let mut goals = goals_with(&[(TARGET, v(40.0, 80.0, 0.0))]);
    goals.blocker = Some(NavBlocker {
        object_id: OTHER,
        name: "Barrel".to_owned(),
        ..NavBlocker::default()
    });
    let mut h = Harness::new(flat_world(1), body, goals);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    in_range(report.replans as f32, 1.0, MAXIMUM_REPLANS as f32);
    assert_eq!(report.blocked_by_object_id, 0);
    in_range(
        h.body.borrow().flat().distance(Vec2::new(40.0, 80.0)),
        0.0,
        DEFAULT_ARRIVAL_METERS + 0.75,
    );
}

#[test]
fn a_walk_blocked_beside_an_object_plans_around_all_of_it_afterward() {
    let mut body = SimBody::at(v(40.0, 40.0, 0.0));
    body.obstacle = Some((Vec2::new(40.0, 60.0), 1.5));
    let mut goals = goals_with(&[(TARGET, v(40.0, 80.0, 0.0))]);
    goals.blocker = Some(NavBlocker {
        object_id: OTHER,
        name: "Ore Deposit".to_owned(),
        position: v(40.0, 60.0, 0.0),
        radius: 1.5,
        ..NavBlocker::default()
    });
    let mut h = Harness::new(flat_world(1), body, goals);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    assert_eq!(report.replans, 1);
    let route = h.walk.route().expect("a route");
    let deposit = Vec2::new(40.0, 60.0);
    for end in route.legs.iter().skip(1) {
        assert!(
            end.truncate().distance(deposit) >= 1.9,
            "a leg ends inside the deposit"
        );
    }
    for leg in 2..route.legs.len() {
        let start = route.legs[leg - 1].truncate();
        let along = route.legs[leg].truncate() - start;
        let t = if along.length_squared() > 0.0 {
            ((deposit - start).dot(along) / along.length_squared()).clamp(0.0, 1.0)
        } else {
            0.0
        };
        assert!(
            deposit.distance(start + along * t) >= 1.5,
            "leg {leg} passes through the deposit"
        );
    }
}

#[test]
fn a_walk_blocked_beside_an_object_with_no_other_way_ends_blocked_naming_it_at_once() {
    let mut body = SimBody::at(v(5.0, 5.0, -30.0));
    body.obstacle = Some((Vec2::new(5.0, 10.0), 0.8));
    let mut goals = goals_with(&[(TARGET, v(5.0, 15.0, -30.0))]);
    goals.blocker = Some(NavBlocker {
        object_id: OTHER,
        name: "Ore Deposit".to_owned(),
        position: v(5.0, 10.0, -30.0),
        radius: 0.8,
        ..NavBlocker::default()
    });
    let mut h = Harness::new(room_with_doorways(&[5.0]), body, goals);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Blocked, "{}", report.reason);
    assert_eq!(report.replans, 1);
    assert_eq!(report.blocked_by_object_id, OTHER);
    assert!(report.reason.contains("Ore Deposit"), "{}", report.reason);
    assert!(
        report
            .reason
            .contains("stands in the way, and no other way around it was found"),
        "{}",
        report.reason
    );
}

#[test]
fn a_walk_plans_around_a_creature_in_its_way_and_keeps_clear_of_it() {
    let creature = Vec2::new(40.0, 70.0);
    let mut body = SimBody::at(v(40.0, 40.0, 0.0));
    body.obstacle = Some((creature, reach()));
    let mut goals = goals_with(&[(TARGET, v(40.0, 100.0, 0.0))]);
    goals.crowd_now = Some(Box::new(move || {
        vec![NavAvoidance::new(creature.extend(0.0), 0.8)]
    }));
    let mut h = Harness::new(flat_world(1), body, goals);
    let nearest = Cell::new(f32::INFINITY);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until(
        |report, body| {
            nearest.set(nearest.get().min(body.flat().distance(creature)));
            settled(report, body)
        },
        120.0,
    );

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    assert_eq!(report.replans, 0);
    assert!(
        nearest.get() >= reach() - 0.05,
        "the walk came within {:.2} m of the creature",
        nearest.get()
    );
}

#[test]
fn a_walk_steps_around_a_creature_that_walks_onto_its_route_without_stopping() {
    let creature = Vec2::new(40.0, 80.0);
    let there = Rc::new(Cell::new(false));
    let mut body = SimBody::at(v(40.0, 40.0, 0.0));
    body.obstacle = Some((creature, reach()));
    let present = Rc::clone(&there);
    body.obstacle_active = Some(Rc::new(move || present.get()));
    let mut goals = goals_with(&[(TARGET, v(40.0, 120.0, 0.0))]);
    let present = Rc::clone(&there);
    goals.crowd_now = Some(Box::new(move || {
        if present.get() {
            vec![NavAvoidance::new(creature.extend(0.0), 0.8)]
        } else {
            Vec::new()
        }
    }));
    let mut h = Harness::new(flat_world(1), body, goals);
    let nearest = Cell::new(f32::INFINITY);
    let longest_still = Cell::new(0);
    let planning = Cell::new(false);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.run_until(
        |report, body| report.state == WalkState::Walking && body.position.y > 60.0,
        120.0,
    );
    there.set(true);
    let report = h.run_until(
        |report, body| {
            nearest.set(nearest.get().min(body.flat().distance(creature)));
            longest_still.set(longest_still.get().max(body.still_frames));
            planning.set(planning.get() || report.state == WalkState::Planning);
            settled(report, body)
        },
        120.0,
    );

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    assert_eq!(report.replans, 0);
    assert!(!planning.get(), "the walk stopped to plan again");
    assert!(
        nearest.get() >= reach() - 0.05,
        "the walk came within {:.2} m of the creature",
        nearest.get()
    );
    assert!(
        longest_still.get() <= 10,
        "the walk stood still for {} frames",
        longest_still.get()
    );
}

#[test]
fn a_walk_stopped_by_a_creature_waits_for_it_to_move_aside_and_goes_on() {
    let moved_aside = Rc::new(Cell::new(false));
    let mut body = SimBody::at(v(5.0, 5.0, -30.0));
    body.obstacle = Some((Vec2::new(5.0, 10.0), reach()));
    let aside = Rc::clone(&moved_aside);
    body.obstacle_active = Some(Rc::new(move || !aside.get()));
    let mut goals = goals_with(&[(TARGET, v(5.0, 15.0, -30.0))]);
    goals.blocker = Some(NavBlocker {
        object_id: OTHER,
        name: "Drudge Skulker".to_owned(),
        position: v(5.0, 10.0, -30.0),
        radius: 0.8,
        moves: true,
        ..NavBlocker::default()
    });
    let aside = Rc::clone(&moved_aside);
    goals.crowd_now = Some(Box::new(move || {
        if aside.get() {
            Vec::new()
        } else {
            vec![NavAvoidance::new(v(5.0, 10.0, -30.0), 0.8)]
        }
    }));
    let mut h = Harness::new(room_with_doorways(&[5.0]), body, goals);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let waiting = h.run_until(|r, _| r.reason.contains("waiting for it to move"), 120.0);
    moved_aside.set(true);
    let report = h.run_until_settled(120.0);

    assert!(
        waiting.reason.contains("Drudge Skulker"),
        "{}",
        waiting.reason
    );
    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    assert_eq!(report.replans, 0);
}

fn skulker(hostile: bool) -> Harness {
    let mut body = SimBody::at(v(5.0, 8.0, -30.0));
    body.stuck = true;
    let mut goals = goals_with(&[(TARGET, v(5.0, 15.0, -30.0))]);
    goals.blocker = Some(NavBlocker {
        object_id: OTHER,
        name: "Drudge Skulker".to_owned(),
        position: v(5.0, 10.0, -30.0),
        radius: 0.8,
        moves: true,
        hostile,
        ..NavBlocker::default()
    });
    goals.crowd_now = Some(Box::new(|| {
        vec![NavAvoidance::new(v(5.0, 10.0, -30.0), 0.8)]
    }));
    Harness::new(room_with_doorways(&[5.0]), body, goals)
}

#[test]
fn a_walk_that_a_creature_never_lets_by_ends_blocked_naming_it() {
    let mut h = skulker(false);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Blocked, "{}", report.reason);
    assert_eq!(report.blocked_by_object_id, OTHER);
    assert!(
        report.reason.contains("Drudge Skulker"),
        "{}",
        report.reason
    );
}

#[test]
fn a_walk_stopped_by_a_hostile_monster_plans_around_it_at_once_instead_of_waiting_for_it_to_move() {
    let mut h = skulker(true);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let first = h.run_until(
        |r, _| r.reason.contains("waiting for it to move") || r.reason.contains("planning again"),
        120.0,
    );
    let report = h.run_until_settled(120.0);

    assert!(first.reason.contains("planning again"), "{}", first.reason);
    assert_eq!(report.state, WalkState::Blocked, "{}", report.reason);
    assert_eq!(report.blocked_by_object_id, OTHER);
    assert!(
        report.reason.contains("a hostile monster, Drudge Skulker"),
        "{}",
        report.reason
    );
}

const ROOM: u32 = 0xA9B4_0100;

fn in_the_dungeon(goals: Goals, corridor_end: f32) -> Harness {
    let mut body = SimBody::at(v(20.0, 20.0, -30.0));
    body.cell_id = ROOM;
    Harness::new(dungeon_world(ROOM, corridor_end), body, goals).sealed(|cell| cell == ROOM)
}

// ── Path previews (as in OpenAC) ──────────────────────────────────────────

#[test]
fn a_preview_inside_a_dungeon_is_an_ordered_cell_aware_path_at_floor_height() {
    // PreviewInsideDungeonReturnsOrderedCellAwareElevatedPath.
    let mut h = in_the_dungeon(goals_with(&[(TARGET, v(245.0, 32.0, -30.0))]), 230.0);
    let plan = h.preview(walk::PreviewTarget::Object(TARGET), 2.5);
    assert_eq!(h.report().sequence, 0, "no walk was started");
    assert_eq!(plan.status, walk::PlanStatus::Routed, "{}", plan.reason);
    assert!(plan.path.len() >= 2);
    assert_eq!(plan.path[0].cell_id, ROOM);
    assert_eq!(plan.path.last().unwrap().cell_id, ROOM + 2);
    for point in &plan.path {
        in_range(point.global.z, -30.1, -29.9);
    }
    assert!(plan.path[0].global.x < plan.path.last().unwrap().global.x);
    assert!(plan.length_meters > 200.0);
}

#[test]
fn a_preview_of_an_unreachable_floor_has_no_route() {
    // PreviewOfUnreachableFloorReturnsNoRoute.
    let mut h = Harness::new(
        raised_slab_world(),
        SimBody::at(v(40.0, 40.0, 0.0)),
        Goals::default(),
    );
    let plan = h.preview(
        walk::PreviewTarget::Place {
            cell_id: 0xA9B4_0001,
            local: v(52.0, 52.0, 1.0),
        },
        2.5,
    );
    assert_eq!(plan.status, walk::PlanStatus::NoRoute);
    assert!(plan.path.is_empty());
    assert_eq!(h.report().sequence, 0);
}

#[test]
fn a_preview_does_not_replace_a_walk_under_way() {
    // PreviewDoesNotReplaceAnActiveWalk.
    let mut h = harness(&[(TARGET, v(60.0, 75.0, 0.0)), (OTHER, v(75.0, 75.0, 0.0))]);
    let sequence = h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h.tick();
    assert!(h.walk.is_busy());
    let plan = h.preview(walk::PreviewTarget::Object(OTHER), 2.5);
    assert_eq!(plan.status, walk::PlanStatus::Routed, "{}", plan.reason);
    assert!(h.walk.is_busy());
    assert_eq!(h.report().sequence, sequence);
    assert_eq!(h.report().object_id, TARGET);
}

#[test]
fn a_preview_without_a_standing_character_is_unavailable() {
    // NavigationWalkController.Preview: a character in the air (or in
    // portal space) has no start.
    let mut h = harness(&[(TARGET, v(60.0, 75.0, 0.0))]);
    h.body.borrow_mut().airborne = true;
    let plan = h.preview(walk::PreviewTarget::Object(TARGET), 2.5);
    assert_eq!(plan.status, walk::PlanStatus::Unavailable);
    let plan = h.preview(walk::PreviewTarget::Object(OTHER), 2.5);
    assert_eq!(plan.status, walk::PlanStatus::NoRoute, "an unknown object");
}

#[test]
fn inside_a_sealed_dungeon_one_grid_covers_every_cell_and_serves_every_walk_there() {
    let mut h = in_the_dungeon(
        goals_with(&[
            (TARGET, v(60.0, 32.0, -30.0)),
            (OTHER, v(245.0, 32.0, -30.0)),
        ]),
        230.0,
    );

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    assert_eq!(h.run_until_settled(120.0).state, WalkState::Arrived);
    let first = Arc::clone(h.walk.grid().expect("a grid"));
    h.walk.walk_to(OTHER, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    assert!(Arc::ptr_eq(&first, h.walk.grid().expect("the same grid")));
    assert!(
        first.contains(v(10.0, 10.0, -30.0), 0.0) && first.contains(v(250.0, 50.0, -30.0), 0.0)
    );
}

#[test]
fn a_dungeon_wider_than_any_outdoor_region_gets_one_grid_without_terrain() {
    let mut h = in_the_dungeon(goals_with(&[(TARGET, v(645.0, 32.0, -30.0))]), 630.0);

    h.walk.route_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Planned, "{}", report.reason);
    assert_eq!(report.reason, "a route was found");
    let grid = h.walk.grid().expect("a grid");
    assert!(grid.size() > 2.0 * MAXIMUM_REGION);
    assert_eq!(grid.landblock_ids(), [0xA9B4_FFFF]);
    assert!(grid.find_node(v(100.0, 150.0, 0.0), 2.0, 2.0) < 0);
}

#[test]
fn a_grid_shown_inside_a_sealed_dungeon_covers_the_whole_dungeon_and_serves_its_walks() {
    let mut h = in_the_dungeon(goals_with(&[(TARGET, v(245.0, 32.0, -30.0))]), 230.0);
    h.walk.show_grid = true;

    for _ in 0..600 {
        if h.walk.grid().is_some() {
            break;
        }
        h.tick();
    }
    let shown = Arc::clone(h.walk.grid().expect("a grid shown"));
    h.walk.route_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert!(
        shown.contains(v(10.0, 10.0, -30.0), 0.0) && shown.contains(v(250.0, 50.0, -30.0), 0.0)
    );
    assert_eq!(report.state, WalkState::Planned, "{}", report.reason);
    assert!(Arc::ptr_eq(&shown, h.walk.grid().expect("the same grid")));
}

#[test]
fn a_goal_far_outside_the_sealed_dungeon_is_reported_without_building_a_grid() {
    let mut h = in_the_dungeon(goals_with(&[(TARGET, v(5000.0, 32.0, -30.0))]), 230.0);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::NoRoute, "{}", report.reason);
    assert!(
        report.reason.contains("outside this dungeon"),
        "{}",
        report.reason
    );
    assert!(h.walk.grid().is_none());
}

fn room_route(doorways: &[f32], goals: Goals) -> Harness {
    let mut h = Harness::new(
        room_with_doorways(doorways),
        SimBody::at(v(5.0, 5.0, -30.0)),
        goals,
    );
    h.walk.route_to(TARGET, DEFAULT_ARRIVAL_METERS);
    h
}

fn room_goals() -> Goals {
    goals_with(&[(TARGET, v(5.0, 15.0, -30.0))])
}

#[test]
fn a_walk_plans_around_an_object_the_server_placed_when_another_way_arrives() {
    let mut goals = room_goals();
    goals
        .obstacles
        .push(NavAvoidance::new(v(5.0, 10.0, -30.0), 0.8));
    let mut h = room_route(&[5.0, 15.0], goals);

    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Planned, "{}", report.reason);
    let route = h.walk.route().expect("a route");
    assert!(
        route
            .path
            .iter()
            .any(|p| p.x > 13.0 && (p.y - 10.0).abs() < 0.5)
    );
}

#[test]
fn a_walk_plans_through_an_object_the_server_placed_when_no_other_way_arrives() {
    let mut goals = room_goals();
    goals
        .obstacles
        .push(NavAvoidance::new(v(5.0, 10.0, -30.0), 0.8));
    let mut h = room_route(&[5.0], goals);

    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Planned, "{}", report.reason);
    let route = h.walk.route().expect("a route");
    assert!(
        route
            .path
            .iter()
            .any(|p| (p.x - 5.0).abs() < 1.0 && (p.y - 10.0).abs() < 0.5)
    );
}

fn assert_keeps_clear_of(route: &ac_nav::NavRoute, portal: NavAvoidance) {
    let centre = portal.centre.truncate();
    for pair in route.path.windows(2) {
        let (from, to) = (pair[0].truncate(), pair[1].truncate());
        let along = to - from;
        let t = if along.length_squared() > 1e-6 {
            ((centre - from).dot(along) / along.length_squared()).clamp(0.0, 1.0)
        } else {
            0.0
        };
        assert!(
            centre.distance(from + along * t) > ac_nav::sources::PORTAL_LEAST_RADIUS,
            "the route passes within {} m of the portal between {from} and {to}",
            ac_nav::sources::PORTAL_LEAST_RADIUS
        );
    }
}

/// A portal collides with nothing, and a walk that brushed one was sent
/// somewhere else. A route goes around a portal where another way arrives.
#[test]
fn a_walk_plans_around_a_portal_when_another_way_arrives() {
    let portal = NavAvoidance::new(v(5.0, 10.0, -30.0), 0.8);
    let mut goals = room_goals();
    goals.portals.push(portal);
    let mut h = room_route(&[5.0, 15.0], goals);

    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Planned, "{}", report.reason);
    let route = h.walk.route().expect("a route");
    assert!(
        route
            .path
            .iter()
            .any(|p| p.x > 13.0 && (p.y - 10.0).abs() < 0.5)
    );
    assert_keeps_clear_of(route, portal);
}

/// Unlike an object the server placed, a portal is never walked through
/// because no other way arrives: going through it ends the walk somewhere
/// else entirely.
#[test]
fn a_walk_never_plans_through_a_portal_even_when_no_other_way_arrives() {
    let portal = NavAvoidance::new(v(5.0, 10.0, -30.0), 0.8);
    let mut goals = room_goals();
    goals.portals.push(portal);
    let mut h = room_route(&[5.0], goals);

    h.run_until_settled(120.0);

    if let Some(route) = h.walk.route() {
        assert_keeps_clear_of(route, portal);
    }
}

/// A walk to the portal it is going to use still reaches it: it ends where
/// the same walk ends with no portal there at all.
#[test]
fn a_walk_to_a_portal_still_reaches_it() {
    let end_of_walk = |portal: Option<NavAvoidance>| {
        let mut goals = room_goals();
        goals.portals.extend(portal);
        let mut h = room_route(&[5.0], goals);
        assert_eq!(h.run_until_settled(120.0).state, WalkState::Planned);
        *h.walk
            .route()
            .expect("a route")
            .path
            .last()
            .expect("a path")
    };
    let with_portal = end_of_walk(Some(NavAvoidance::new(v(5.5, 16.0, -30.0), 0.8)));
    let without = end_of_walk(None);

    assert!(
        with_portal.distance(without) < 0.5,
        "with the portal the walk ends at {with_portal}, without it at {without}"
    );
}

#[test]
fn a_walk_that_stops_beside_a_closed_door_it_did_not_see_ahead_opens_it() {
    let mut body = SimBody::at(v(40.0, 40.0, 0.0));
    body.obstacle = Some((Vec2::new(40.0, 60.0), 0.3));
    let mut doors = FakeDoors::new(door(Vec3::ZERO, 0.0), Vec2::new(40.0, 60.0), true);
    doors.seen_ahead = false;
    let mut goals = goals_with(&[(TARGET, v(40.0, 80.0, 0.0))]);
    goals.blocker = Some(NavBlocker {
        object_id: DOOR,
        name: "Door".to_owned(),
        is_closed_door: true,
        ..NavBlocker::default()
    });
    let mut h = Harness::new(flat_world(1), body, goals).with_doors(doors);

    h.walk.walk_to(TARGET, DEFAULT_ARRIVAL_METERS);
    let report = h.run_until_settled(120.0);

    assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
    assert_eq!(h.doors.as_ref().unwrap().uses, 1);
    assert_eq!(report.replans, 0);
    assert!(h.body.borrow().position.y > 60.0);
}

fn register_drifter(physics: &mut PhysicsEngine, id: u32, at: Vec3) {
    let (shadows, world) = physics.shadows_and_world();
    shadows
        .register(
            world,
            id,
            &ac_physics::SingleShape {
                gfx_obj_id: 0,
                radius: 1.0,
                collision_type: ac_physics::ShadowCollisionType::Cylinder,
                cyl_height: 1.0,
                scale: 1.0,
            },
            drifter_site(at),
            0,
            ac_physics::EntityCollisionFlags::default(),
            false,
            None,
        )
        .expect("the drifter registers");
}

fn drifter_site(at: Vec3) -> ac_physics::RegistrationSite {
    ac_physics::RegistrationSite {
        world_pos: at,
        rotation: glam::Quat::IDENTITY,
        world_offset_x: 0.0,
        world_offset_y: 0.0,
        landblock_id: 0xA9B4_FFFF,
        seed_cell_id: 0xA9B4_0001,
    }
}

fn move_drifter(physics: &mut PhysicsEngine, id: u32, at: Vec3) {
    let (shadows, world) = physics.shadows_and_world();
    shadows
        .update_position(world, id, drifter_site(at))
        .expect("the drifter moves");
}

fn grids_built(said: &[String]) -> usize {
    said.iter().filter(|l| l.starts_with("Navmesh:")).count()
}

/// A walk builds its grid again over the objects standing here where it
/// needs them and the grid has none of them, once for each stretch it
/// walks. Where nothing reaches the goal and the objects here keep
/// changing, the answer must still be that nothing reaches it.
#[test]
fn a_walk_that_cannot_arrive_stops_building_grids_though_objects_keep_changing() {
    const DRIFTER: u32 = 0x8000_2AB2;
    let mut h = Harness::new(
        flat_world(1),
        SimBody::at(v(40.0, 40.0, 0.0)),
        Goals::default(),
    );
    h.walk.walk_to_place(0xA9B4_0001, v(45.0, 40.0, 0.0), 1.0);
    assert_eq!(h.run_until_settled(120.0).state, WalkState::Arrived);

    register_drifter(&mut h.physics, DRIFTER, v(50.0, 40.0, 0.0));
    h.goals.standing.insert(DRIFTER);

    let built = grids_built(&h.said());
    h.walk.walk_to_place(0xA9B4_0001, v(60.0, 40.0, 4.0), 1.0);
    let mut report = h.report();
    let mut along = 0.0f32;
    for _ in 0..3000 {
        move_drifter(&mut h.physics, DRIFTER, v(50.0 + along % 8.0, 40.0, 0.0));
        along += 0.5;
        h.tick();
        report = h.report();
        if settled(&report, &h.body.borrow()) {
            break;
        }
    }

    assert_eq!(report.state, WalkState::NoRoute, "{}", report.reason);
    in_range((grids_built(&h.said()) - built) as f32, 0.0, 2.0);
}

/// What changes the ground is a thing the server places and leaves; a thing
/// crossing the region is not that, and a grid must not be built for it.
#[test]
fn a_thing_crossing_the_region_builds_no_grid_and_one_that_stays_builds_one() {
    const DRIFTER: u32 = 0x8000_2AB1;
    let mut h = Harness::new(
        flat_world(1),
        SimBody::at(v(40.0, 40.0, 0.0)),
        Goals::default(),
    );
    h.walk.show_grid = true;
    let tick_until_said = |h: &mut Harness, lines: usize| {
        for _ in 0..3000 {
            h.tick();
            if h.said().len() >= lines {
                return h.said().len() == lines;
            }
        }
        false
    };
    assert!(
        tick_until_said(&mut h, 1),
        "no grid was built to show around the character"
    );

    register_drifter(&mut h.physics, DRIFTER, v(50.0, 40.0, 0.0));
    h.goals.standing.insert(DRIFTER);
    let mut along = 0.0f32;
    for _ in 0..60 {
        move_drifter(&mut h.physics, DRIFTER, v(50.0 + along % 10.0, 40.0, 0.0));
        along += 0.5;
        h.tick();
    }
    assert_eq!(h.said().len(), 1, "{:?}", h.said());

    assert!(
        tick_until_said(&mut h, 2),
        "no grid was built once the thing stayed where it was"
    );
    assert!(
        h.said()[1].contains("built because the objects in it changed"),
        "{}",
        h.said()[1]
    );
}

// ── the walk corpus ─────────────────────────────────────────────────────

/// Walks a fixed set of routes through real dungeons and towns from the
/// installed game files, start to goal, with the walk controller and a
/// simulated character, and compares what happened with the record kept
/// beside this file (`walk_corpus.txt`; OpenAC's own record,
/// `tests/AcDream.App.Tests/Navigation/NavigationWalkCorpus.txt`, is kept as
/// `walk_corpus_openac.txt`, and the lines where the two differ are
/// listed there): big and small dungeons, a closed door that opens, a
/// locked door on the only way and one with another way, a walk from
/// outdoors into a building, and walks from one building out and into
/// another. Each walk runs twice, cutting corners and turning in place at
/// them, and the character must never pass through a wall. The collision
/// is the runtime's own (`ac_physics::content::publish_landblock`, with
/// procedural scenery), as OpenAC's corpus walks the client's and the
/// headless host's. After a change meant to alter the walks, record them
/// again with `OPENAC_UPDATE_WALK_CORPUS=1`.
mod corpus {
    use super::*;

    const FRAME_SECONDS: f32 = 1.0 / 30.0;
    const MAXIMUM_SECONDS: f64 = 900.0;
    const GOAL: u32 = 0x5000_0001;
    const DOOR_ID: u32 = 0x7A00_0001;
    const DOOR_RADIUS: f32 = 0.8;
    const RECORD: &str = "walk_corpus.txt";
    /// A character that runs 10 m/s and walks 3.12 m/s, and turns 1.5
    /// radians a second at a walk and half again as fast standing or
    /// running.
    const RUN_SPEED: f32 = 10.0;
    const WALK_SPEED: f32 = 3.12;
    const RUN_TURN: f32 = 2.25 * (180.0 / std::f32::consts::PI);
    const WALK_TURN: f32 = 1.5 * (180.0 / std::f32::consts::PI);

    const HOLTBURG: &[u32] = &[0xA9B4_FFFF, 0xAAB4_FFFF, 0xA9B3_FFFF, 0xAAB3_FFFF];
    /// The 3x3 landblocks around Holtburg, as many as a session keeps
    /// loaded around a character.
    const AROUND_HOLTBURG: &[u32] = &[
        0xA9B4_FFFF,
        0xA8B3_FFFF,
        0xA9B3_FFFF,
        0xAAB3_FFFF,
        0xA8B4_FFFF,
        0xAAB4_FFFF,
        0xA8B5_FFFF,
        0xA9B5_FFFF,
        0xAAB5_FFFF,
    ];

    struct Case {
        name: &'static str,
        landblocks: &'static [u32],
        start_cell: u32,
        start: Vec3,
        goal: Vec3,
        dungeon: bool,
        /// A door across a doorway on the way: where it stands, and whether
        /// it is locked (else it opens when used).
        door: Option<(Vec3, bool)>,
    }

    const fn case(
        name: &'static str,
        landblocks: &'static [u32],
        start_cell: u32,
        start: Vec3,
        goal: Vec3,
        dungeon: bool,
    ) -> Case {
        Case {
            name,
            landblocks,
            start_cell,
            start,
            goal,
            dungeon,
            door: None,
        }
    }

    fn cases() -> Vec<Case> {
        let nan = f32::NAN;
        vec![
            case(
                "big dungeon 0x0019, a north corridor to a far hall",
                &[0x0019_FFFF],
                0x0019_0215,
                v(90.0, -890.0, 12.005),
                v(80.0, -120.0, 6.005),
                true,
            ),
            case(
                "big dungeon 0x200F, across the mines",
                &[0x200F_FFFF],
                0x200F_0480,
                v(80.0, -807.0, -59.595),
                v(430.0, -477.0, -59.595),
                true,
            ),
            case(
                "big dungeon 0x039E, down 42 m",
                &[0x039E_FFFF],
                0x039E_02A8,
                v(70.0, -810.0, 0.005),
                v(30.0, -240.0, -41.995),
                true,
            ),
            case(
                "small dungeon 0x019E, the entrance to an upper room",
                &[0x019E_FFFF],
                0x019E_0100,
                v(0.38, -0.13, 0.0),
                v(20.38, -39.63, 6.0),
                true,
            ),
            case(
                "small dungeon 0x0156, the bottom level to the top",
                &[0x0156_FFFF],
                0x0156_0100,
                v(0.13, -50.13, -18.0),
                v(39.88, -2.63, 0.0),
                true,
            ),
            case(
                "small dungeon 0x0156, corridors to a six-way room",
                &[0x0156_FFFF],
                0x0156_0139,
                v(40.32, -26.23, -12.0),
                v(59.88, -40.13, -6.0),
                true,
            ),
            Case {
                door: Some((v(51.38, -27.63, -12.0), false)),
                ..case(
                    "dungeon 0x0156, a closed door on the route that opens",
                    &[0x0156_FFFF],
                    0x0156_0100,
                    v(0.13, -50.13, -18.0),
                    v(46.38, -29.38, -12.0),
                    true,
                )
            },
            Case {
                door: Some((v(51.38, -27.63, -12.0), true)),
                ..case(
                    "dungeon 0x0156, a locked door on the only way",
                    &[0x0156_FFFF],
                    0x0156_0100,
                    v(0.13, -50.13, -18.0),
                    v(59.88, -40.13, -6.0),
                    true,
                )
            },
            case(
                "dungeon 0x01F8, across the mite maze",
                &[0x01F8_FFFF],
                0x01F8_0100,
                v(109.88, -40.13, -6.0),
                v(70.13, -123.13, 0.0),
                true,
            ),
            Case {
                door: Some((v(111.38, -68.63, 0.0), true)),
                ..case(
                    "dungeon 0x01F8, a locked door with another way",
                    &[0x01F8_FFFF],
                    0x01F8_0100,
                    v(109.88, -40.13, -6.0),
                    v(110.63, -72.63, 0.0),
                    true,
                )
            },
            case(
                "Holtburg, outdoors into a building",
                HOLTBURG,
                0xA9B4_0029,
                v(139.2, 21.12, 94.0),
                v(79.63, 37.38, 94.0),
                false,
            ),
            case(
                "Holtburg, out of one building and into another",
                HOLTBURG,
                0xA9B4_016A,
                v(79.63, 37.38, 94.0),
                v(161.88, 7.63, 94.0),
                false,
            ),
            case(
                "Holtburg, beside the Contract Broker to Renald the Elder",
                HOLTBURG,
                0xA9B4_0162,
                v(110.4, 35.52, 94.0),
                v(139.2, 18.24, 94.0),
                false,
            ),
            case(
                "Yaraq, the archmage's room out and up to the healer's deck",
                &[0x7D64_FFFF],
                0x7D64_012E,
                v(83.96, 90.86, 15.205),
                v(85.92, 138.564, 15.605),
                false,
            ),
            case(
                "around Holtburg, west to east across three landblocks in stages",
                AROUND_HOLTBURG,
                0xA8B4_0024,
                v(-100.0, 100.0, nan),
                v(290.0, 100.0, nan),
                false,
            ),
        ]
    }

    fn dats() -> ac_dat::DatCollection {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(std::path::PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        ac_dat::DatCollection::open(dir).expect("open the retail dats")
    }

    /// The landblocks published as the runtime publishes them, around the
    /// first, whose south-west corner is the origin.
    fn publish(dats: &ac_dat::DatCollection, landblocks: &[u32]) -> PhysicsEngine {
        let region: ac_dat::objects::Region = dats
            .get(0x1300_0000)
            .expect("read the region")
            .expect("the region");
        let heights = region.land_defs.land_height_table;
        let mut engine = PhysicsEngine::new();
        engine.attach_world(ac_physics::CollisionWorld::new());
        for &id in landblocks {
            ac_physics::content::publish_landblock(
                &mut engine,
                dats,
                &heights,
                id,
                ac_physics::content::landblock_origin(landblocks[0], id),
            )
            .expect("the landblock publishes")
            .expect("the landblock exists");
        }
        engine
    }

    /// The height of the floor in a point's column within a step of it, or
    /// NaN where there is none.
    fn floor_under(grid: &ac_nav::NavGrid, point: Vec3) -> f32 {
        let (first, count) = grid.nodes_in_column(
            ((point.x - grid.origin_x()) / grid.cell_size()).floor() as i32,
            ((point.y - grid.origin_y()) / grid.cell_size()).floor() as i32,
        );
        let body = grid.body();
        let mut nearest = body.step_up_height.max(body.step_down_height) + 0.1;
        let mut floor = f32::NAN;
        for node in first..first + count {
            let rise = (grid.position(node).z - point.z).abs();
            if rise < nearest {
                nearest = rise;
                floor = grid.position(node).z;
            }
        }
        floor
    }

    fn on_the_ground(engine: &PhysicsEngine, point: Vec3) -> Vec3 {
        if point.z.is_nan() {
            v(
                point.x,
                point.y,
                engine
                    .sample_terrain_z(point.x, point.y)
                    .expect("terrain under the point"),
            )
        } else {
            point
        }
    }

    /// One walk, as the record words it, and how often it passed through a
    /// wall.
    fn walk_once(walk: &Case, engine: PhysicsEngine, cut_corners: bool) -> (String, i32) {
        let start = on_the_ground(&engine, walk.start);
        let goal = on_the_ground(&engine, walk.goal);
        let mut body = SimBody::at(start);
        body.cell_id = walk.start_cell;
        body.run_speed = RUN_SPEED;
        body.walk_speed = WALK_SPEED;
        body.turn_speed = RUN_TURN;
        body.walk_turn_speed = WALK_TURN;
        body.turning = cut_corners.then_some(RouteTurning {
            run_speed: RUN_SPEED,
            run_turn_degrees_per_second: RUN_TURN,
            walk_speed: 0.0,
            walk_turn_degrees_per_second: 0.0,
        });
        if let Some((at, _)) = walk.door {
            body.obstacle = Some((at.truncate(), DOOR_RADIUS));
        }
        let mut h = Harness::new(engine, body, goals_with(&[(GOAL, goal)]));
        if walk.dungeon {
            let landblock = walk.start_cell >> 16;
            h = h.sealed(move |cell| cell >> 16 == landblock && cell & 0xFFFF >= 0x100);
        }
        if let Some((at, locked)) = walk.door {
            let mut doors = FakeDoors::new(
                NavDoor {
                    object_id: DOOR_ID,
                    name: "Door".to_owned(),
                    position: at,
                    radius: DOOR_RADIUS,
                },
                at.truncate(),
                !locked,
            );
            doors.locked_when_appraised = Some(locked);
            h = h.with_doors(doors);
        }

        h.walk.walk_to(GOAL, DEFAULT_ARRIVAL_METERS);
        let mut off_floor = 0;
        let mut through_walls = 0;
        let mut report = h.report();
        let mut ticks = 0;
        while h.body.borrow().seconds < MAXIMUM_SECONDS && ticks < 400_000 {
            ticks += 1;
            h.tick();
            report = h.report();
            if !matches!(
                report.state,
                WalkState::Planning | WalkState::Walking | WalkState::Waiting
            ) {
                break;
            }
            if report.state == WalkState::Planning || h.walk.is_searching() {
                continue;
            }
            let before = h.body.borrow().position;
            h.integrate(FRAME_SECONDS);
            let Some(grid) = h.walk.grid().cloned() else {
                continue;
            };
            let after = h.body.borrow().position;
            if after == before {
                continue;
            }
            let floor = floor_under(&grid, after);
            if floor.is_nan() {
                off_floor += 1;
                continue;
            }
            let placed = v(after.x, after.y, floor);
            h.body.borrow_mut().position = placed;
            if !grid.is_open_line(before, placed) {
                through_walls += 1;
            }
        }

        let said = h.said();
        let route = said.iter().find(|l| l.starts_with("Route: ")).map_or_else(
            || "no route".to_owned(),
            |planned| {
                planned["Route: ".len()..]
                    .split(", ")
                    .take(2)
                    .collect::<Vec<_>>()
                    .join(", ")
            },
        );
        let corners = said
            .iter()
            .rev()
            .find(|l| l.contains(": ran around "))
            .map_or_else(
                || "no corners".to_owned(),
                |counted| counted[counted.find(": ran around ").unwrap() + 2..].to_owned(),
            );
        let ending = if report.state == WalkState::Arrived {
            String::new()
        } else {
            format!(" ({})", report.reason)
        };
        let door_text = h.doors.as_ref().map_or_else(String::new, |doors| {
            format!(
                ", door used {} and appraised {} times",
                doors.uses, doors.appraisals
            )
        });
        let stages = said
            .iter()
            .filter(|l| l.contains(" walked; planning the next toward the goal"))
            .count();
        let stage_text = if stages == 0 {
            String::new()
        } else {
            format!(", {stages} stages walked before the last")
        };
        let body = h.body.borrow();
        let text = format!(
            "{:?}{ending}, {} m left, replans {}; route {route}; {} s, {} stops, {} s walking; {corners}{door_text}{stage_text}; {off_floor} frames off the floor, {through_walls} through a wall",
            report.state,
            ac_nav::order::fixed(report.remaining_meters, 1),
            report.replans,
            ac_nav::order::fixed(body.seconds as f32, 1),
            body.travel_stops,
            ac_nav::order::fixed(body.walking_seconds as f32, 1),
        );
        (text, through_walls)
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_corpus_is_walked_as_recorded() {
        let dats = dats();
        let mut lines = Vec::new();
        let mut through_walls = Vec::new();
        for walk in cases() {
            let (cutting, cut_walls) = walk_once(&walk, publish(&dats, walk.landblocks), true);
            let (standing, stand_walls) = walk_once(&walk, publish(&dats, walk.landblocks), false);
            println!(
                "{}\n  cutting corners:  {cutting}\n  turning in place: {standing}",
                walk.name
            );
            lines.push(walk.name.to_owned());
            lines.push(format!("  cutting corners:  {cutting}"));
            lines.push(format!("  turning in place: {standing}"));
            if cut_walls > 0 || stand_walls > 0 {
                through_walls.push(walk.name);
            }
        }

        let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("tests")
            .join(RECORD);
        if std::env::var("OPENAC_UPDATE_WALK_CORPUS").as_deref() == Ok("1") {
            std::fs::write(&path, lines.join("\n") + "\n").expect("record the corpus");
        }
        let recorded = std::fs::read_to_string(&path).expect("the corpus record");
        let expected: Vec<&str> = recorded.trim_end_matches('\n').split('\n').collect();
        let mut differences = String::new();
        for index in 0..expected.len().max(lines.len()) {
            let want = expected.get(index).copied().unwrap_or("(nothing)");
            let got = lines.get(index).map_or("(nothing)", String::as_str);
            if want.trim_end_matches('\r') != got {
                differences.push_str(&format!(
                    "line {}\n  recorded: {want}\n  walked:   {got}\n",
                    index + 1
                ));
            }
        }
        assert!(
            differences.is_empty(),
            "The walks differ from {RECORD}. If the change is meant, record them again with OPENAC_UPDATE_WALK_CORPUS=1.\n{differences}"
        );
        assert!(
            through_walls.is_empty(),
            "The character passed through a wall walking: {}",
            through_walls.join("; ")
        );
    }
}

/// A place inside a dungeon cell is often given by the middle of the cell's
/// bounds, which in a tall room is metres above its floor; such a place
/// stands on the floor under it. Ported from OpenAC PR #222
/// `tests/AcDream.App.Tests/Navigation/DungeonPlaceFloorTests.cs` (the cases
/// through the client's own goal source are in `ac-runtime`'s navigation
/// tests).
mod dungeon_place_floor {
    use super::*;
    use ac_nav::grid::DEFAULT_CELL_SIZE;

    const LOW_ROOM: u32 = 0xA9B4_0100;
    const TALL_ROOM: u32 = 0xA9B4_0101;
    const STACKED_LOWER: u32 = 0xA9B4_0102;
    const STACKED_UPPER: u32 = 0xA9B4_0103;
    const FLOOR: f32 = -30.0;
    const PLATFORM_TOP: f32 = FLOOR + 2.0;
    const PLATFORM_RADIUS: f32 = 3.0;

    /// The middle of the 5 m room's bounds, 2.5 m over its floor, as a
    /// dungeon map gives it.
    const TALL_MIDDLE: Vec3 = Vec3::new(30.0, 10.0, -27.5);
    /// The middle of the floorless upper cell's bounds, 7.5 m over the floor
    /// of the cell below.
    const UPPER_MIDDLE: Vec3 = Vec3::new(50.0, 10.0, -22.5);

    fn cell(
        cell_id: u32,
        x0: f32,
        x1: f32,
        floor: Option<f32>,
        low: f32,
        top: f32,
        roofed: bool,
    ) -> CellSurface {
        let mut vertices = HashMap::new();
        let mut polygons = Vec::new();
        let mut add = |corners: [Vec3; 4]| {
            let mut ids = Vec::new();
            for corner in corners {
                let id = vertices.len() as u16;
                ids.push(id);
                vertices.insert(id, corner);
            }
            polygons.push(ids);
        };
        if let Some(z) = floor {
            add([v(x0, 0.0, z), v(x1, 0.0, z), v(x1, 20.0, z), v(x0, 20.0, z)]);
        }
        if roofed {
            add([
                v(x0, 0.0, top),
                v(x0, 20.0, top),
                v(x1, 20.0, top),
                v(x1, 0.0, top),
            ]);
        }
        add([
            v(x0, 0.0, low),
            v(x1, 0.0, low),
            v(x1, 0.0, top),
            v(x0, 0.0, top),
        ]);
        add([
            v(x0, 20.0, low),
            v(x1, 20.0, low),
            v(x1, 20.0, top),
            v(x0, 20.0, top),
        ]);
        CellSurface::new(cell_id, &vertices, &polygons)
    }

    /// Three rooms in a row 30 m down: a low room 3 m high, a room 5 m high,
    /// and a room 10 m high authored as a cell on top of a cell, the upper
    /// one with walls and a ceiling but no floor.
    fn dungeon_with_tall_rooms() -> PhysicsEngine {
        let mut physics = PhysicsEngine::new();
        physics.add_landblock(
            0xA9B4_FFFF,
            TerrainSurface::new(&[0; 81], &[0.0; 256], 0, 0, None),
            vec![
                cell(LOW_ROOM, 0.0, 20.0, Some(FLOOR), FLOOR, -27.0, true),
                cell(TALL_ROOM, 20.0, 40.0, Some(FLOOR), FLOOR, -25.0, true),
                cell(STACKED_LOWER, 40.0, 60.0, Some(FLOOR), FLOOR, -25.0, false),
                cell(STACKED_UPPER, 40.0, 60.0, None, -25.0, -20.0, true),
            ],
            Vec::new(),
            0.0,
            0.0,
        );
        physics
    }

    /// The 5 m room with a platform 2 m high and 6 m across in its middle,
    /// which the landblock holds.
    fn dungeon_with_a_platform() -> PhysicsEngine {
        let mut physics = dungeon_with_tall_rooms();
        let (shadows, world) = physics.shadows_and_world();
        shadows
            .register(
                world,
                0x7A9B_4001,
                &ac_physics::SingleShape {
                    gfx_obj_id: 0,
                    radius: PLATFORM_RADIUS,
                    collision_type: ac_physics::ShadowCollisionType::Cylinder,
                    cyl_height: 2.0,
                    scale: 1.0,
                },
                ac_physics::RegistrationSite {
                    world_pos: v(30.0, 10.0, FLOOR),
                    rotation: glam::Quat::IDENTITY,
                    world_offset_x: 0.0,
                    world_offset_y: 0.0,
                    landblock_id: 0xA9B4_FFFF,
                    seed_cell_id: TALL_ROOM,
                },
                0,
                Default::default(),
                true,
                None,
            )
            .expect("the platform registers");
        physics
    }

    fn floors() -> Goals {
        Goals {
            floors: Some(dungeon_with_tall_rooms()),
            ..Goals::default()
        }
    }

    fn harness_in_the_low_room(goals: Goals) -> Harness {
        let mut body = SimBody::at(v(10.0, 10.0, FLOOR));
        body.cell_id = LOW_ROOM;
        Harness::new(dungeon_with_tall_rooms(), body, goals).sealed(|_| true)
    }

    fn preview(goals: Goals, cell_id: u32, local: Vec3) -> walk::NavPlan {
        harness_in_the_low_room(goals).preview(walk::PreviewTarget::Place { cell_id, local }, 2.5)
    }

    fn placed(world: &PhysicsEngine, cell_id: u32, point: Vec3) -> Vec3 {
        on_the_cell_floor(world, cell_id, point, None)
    }

    fn to_the_tall_room() -> walk::PreviewTarget {
        walk::PreviewTarget::Place {
            cell_id: TALL_ROOM,
            local: TALL_MIDDLE,
        }
    }

    fn builds(h: &Harness) -> usize {
        h.said()
            .iter()
            .filter(|line| line.starts_with("Navmesh:") && line.contains("built because"))
            .count()
    }

    /// A box the server placed in the tall room, which a body stands on.
    fn place_a_crate(h: &mut Harness, id: u32) {
        let (shadows, world) = h.physics.shadows_and_world();
        shadows
            .register(
                world,
                id,
                &ac_physics::SingleShape {
                    gfx_obj_id: 0,
                    radius: 0.5,
                    collision_type: ac_physics::ShadowCollisionType::Cylinder,
                    cyl_height: 0.5,
                    scale: 1.0,
                },
                ac_physics::RegistrationSite {
                    world_pos: v(36.0, 16.0, FLOOR),
                    rotation: glam::Quat::IDENTITY,
                    world_offset_x: 0.0,
                    world_offset_y: 0.0,
                    landblock_id: 0xA9B4_FFFF,
                    seed_cell_id: TALL_ROOM,
                },
                0,
                Default::default(),
                false,
                None,
            )
            .expect("the crate registers");
        h.goals.standing.insert(id);
    }

    /// The grid a preview builds over a sealed dungeon is kept, so the next
    /// preview there searches it and builds none.
    #[test]
    fn a_second_preview_in_a_dungeon_searches_the_grid_the_first_built() {
        let mut h = harness_in_the_low_room(floors());
        let first = h.preview(to_the_tall_room(), 2.5);
        assert_eq!(first.status, walk::PlanStatus::Routed, "{}", first.reason);
        assert!(!first.grid_reused);
        h.tick();
        let second = h.preview(
            walk::PreviewTarget::Place {
                cell_id: STACKED_UPPER,
                local: UPPER_MIDDLE,
            },
            2.5,
        );
        assert_eq!(second.status, walk::PlanStatus::Routed, "{}", second.reason);
        assert!(second.grid_reused);
        assert_eq!(builds(&h), 1, "{:?}", h.said());
        assert!(first.took_ms >= 0.0 && second.took_ms >= 0.0);
    }

    /// A preview asked while a grid over the dungeon is being built waits
    /// for that build instead of starting its own.
    #[test]
    fn a_preview_asked_during_a_build_waits_for_it() {
        let mut h = harness_in_the_low_room(floors());
        let first = h.preview(to_the_tall_room(), 2.5);
        let second = h.preview(to_the_tall_room(), 2.5);
        assert!(!first.grid_reused);
        assert!(
            second.grid_reused,
            "the first preview's build was under way"
        );
        assert_eq!(second.path, first.path);
        h.tick();
        assert_eq!(builds(&h), 1, "{:?}", h.said());
    }

    /// On workers, a second preview asked while the first's build runs
    /// waits for that build on its own worker, and both answer.
    #[test]
    fn previews_on_workers_share_one_build() {
        let mut h = harness_in_the_low_room(floors()).threaded();
        let mut handle = BodyHandle(Rc::clone(&h.body));
        let world = WalkWorld {
            physics: &h.physics,
            body: &mut handle,
            goals: &h.goals,
            doors: None,
            paused_by: None,
            is_sealed_dungeon: Some(&|_| true),
        };
        let first = h.walk.preview_path(&world, to_the_tall_room(), 2.5);
        let second = h.walk.preview_path(&world, to_the_tall_room(), 2.5);
        let (first, second) = (first.take(), second.take());
        assert_eq!(first.status, walk::PlanStatus::Routed, "{}", first.reason);
        assert_eq!(second.status, walk::PlanStatus::Routed, "{}", second.reason);
        assert!(!first.grid_reused);
        assert!(second.grid_reused);
        h.tick();
        assert_eq!(builds(&h), 1, "{:?}", h.said());
    }

    /// A walk after a preview walks over the grid the preview built.
    #[test]
    fn a_walk_after_a_preview_builds_no_grid_of_its_own() {
        let mut h = harness_in_the_low_room(floors());
        let plan = h.preview(to_the_tall_room(), 2.5);
        assert_eq!(plan.status, walk::PlanStatus::Routed, "{}", plan.reason);
        h.tick();
        h.walk
            .walk_to_place(TALL_ROOM, TALL_MIDDLE, DEFAULT_ARRIVAL_METERS);
        let report = h.run_until_settled(60.0);
        assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
        assert_eq!(builds(&h), 1, "{:?}", h.said());
    }

    /// An object a body stands on placed after the grid was built is not in
    /// it, so the next preview builds the grid again.
    #[test]
    fn a_preview_builds_again_once_an_object_to_stand_on_is_placed() {
        let mut h = harness_in_the_low_room(floors());
        let _ = h.preview(to_the_tall_room(), 2.5);
        h.tick();
        place_a_crate(&mut h, 0x7A9B_4002);
        let after = h.preview(to_the_tall_room(), 2.5);
        assert_eq!(after.status, walk::PlanStatus::Routed, "{}", after.reason);
        assert!(!after.grid_reused);
        h.tick();
        assert_eq!(builds(&h), 2, "{:?}", h.said());
        let again = h.preview(to_the_tall_room(), 2.5);
        assert!(again.grid_reused);
    }

    /// Why the fix is needed: a goal left in mid-air, higher over the floor
    /// than a walk can arrive at, has no route at all, though the room is
    /// open to walk into.
    #[test]
    fn a_place_left_in_mid_air_over_a_tall_rooms_floor_has_no_route() {
        // APlaceLeftInMidAirOverATallRoomsFloorHasNoRoute.
        let plan = preview(Goals::default(), TALL_ROOM, TALL_MIDDLE);
        assert_eq!(plan.status, walk::PlanStatus::NoRoute);
    }

    #[test]
    fn a_place_at_the_middle_of_a_tall_room_routes_to_its_floor() {
        // APlaceAtTheMiddleOfATallRoomRoutesToItsFloor.
        let plan = preview(floors(), TALL_ROOM, TALL_MIDDLE);
        assert_eq!(plan.status, walk::PlanStatus::Routed, "{}", plan.reason);
        in_range(plan.path.last().unwrap().global.z, FLOOR - 0.1, FLOOR + 0.1);
    }

    #[test]
    fn a_place_in_a_floorless_upper_cell_routes_to_the_floor_of_the_cell_below() {
        // APlaceInAFloorlessUpperCellRoutesToTheFloorOfTheCellBelow.
        let world = dungeon_with_tall_rooms();
        let plan = preview(floors(), STACKED_UPPER, UPPER_MIDDLE);
        assert_eq!(
            placed(&world, STACKED_UPPER, UPPER_MIDDLE),
            v(50.0, 10.0, FLOOR)
        );
        assert_eq!(plan.status, walk::PlanStatus::Routed, "{}", plan.reason);
        assert_eq!(plan.path.last().unwrap().cell_id, STACKED_LOWER);
    }

    #[test]
    fn a_place_in_a_low_room_is_unchanged() {
        // APlaceInALowRoomIsUnchanged.
        let world = dungeon_with_tall_rooms();
        let on_the_floor = v(15.0, 12.0, FLOOR);
        let middle = v(15.0, 12.0, -28.5);
        let plan = preview(floors(), LOW_ROOM, middle);
        assert_eq!(placed(&world, LOW_ROOM, on_the_floor), on_the_floor);
        assert_eq!(placed(&world, LOW_ROOM, middle), on_the_floor);
        assert_eq!(plan.status, walk::PlanStatus::Routed, "{}", plan.reason);
    }

    #[test]
    fn a_place_given_a_little_under_its_floor_stays_on_that_floor() {
        // APlaceGivenALittleUnderItsFloorStaysOnThatFloor.
        let world = dungeon_with_tall_rooms();
        assert_eq!(
            placed(&world, TALL_ROOM, v(30.0, 10.0, FLOOR - 0.2)).z,
            FLOOR
        );
    }

    #[test]
    fn an_outdoor_place_is_unchanged() {
        // AnOutdoorPlaceIsUnchanged(0xA9B40001), (0xA9B4FFFF), (0).
        let world = dungeon_with_tall_rooms();
        let place = v(30.0, 10.0, -27.5);
        for cell_id in [0xA9B4_0001, 0xA9B4_FFFF, 0] {
            assert_eq!(placed(&world, cell_id, place), place, "0x{cell_id:08X}");
        }
    }

    #[test]
    fn a_place_with_no_floor_under_it_is_unchanged() {
        // APlaceWithNoFloorUnderItIsUnchanged.
        let world = dungeon_with_tall_rooms();
        let outside = v(100.0, 100.0, -27.5);
        let below_every_floor = v(30.0, 10.0, -40.0);
        assert_eq!(placed(&world, TALL_ROOM, outside), outside);
        assert_eq!(
            placed(&world, TALL_ROOM, below_every_floor),
            below_every_floor
        );
    }

    /// The walk to a place stands its goal where the preview does.
    #[test]
    fn a_walk_to_a_place_at_the_middle_of_a_tall_room_arrives() {
        // AWalkToAPlaceAtTheMiddleOfATallRoomArrives.
        let mut h = harness_in_the_low_room(floors());
        h.walk.walk_to_place(TALL_ROOM, TALL_MIDDLE, 2.5);
        let report = h.run_until_settled(120.0);
        assert_eq!(report.state, WalkState::Arrived, "{}", report.reason);
        in_range(
            h.body.borrow().flat().distance(Vec2::new(30.0, 10.0)),
            0.0,
            2.6,
        );
    }

    /// A waypoint recorded at a platform's edge stays on the platform, as
    /// far out as the route grid still holds the platform's top: half a grid
    /// column beyond its radius.
    #[test]
    fn a_place_at_a_platforms_edge_stays_on_the_platform() {
        // APlaceAtAPlatformsEdgeStaysOnThePlatform.
        let world = dungeon_with_a_platform();
        let edge = 30.0 + PLATFORM_RADIUS;
        let slack = DEFAULT_CELL_SIZE * 0.5;
        let at_the_edge = v(edge + slack * 0.8, 10.0, PLATFORM_TOP);
        let past_the_edge = v(edge + slack * 1.5, 10.0, PLATFORM_TOP);
        assert_eq!(placed(&world, TALL_ROOM, at_the_edge), at_the_edge);
        assert_eq!(
            placed(&world, TALL_ROOM, past_the_edge),
            past_the_edge.with_z(FLOOR)
        );
    }

    /// The underside of a ceiling, an arch or anything hanging faces down,
    /// and nothing stands on it: a place just under the 5 m room's ceiling
    /// drops to the floor, never up onto the ceiling.
    #[test]
    fn a_place_just_under_a_ceiling_drops_to_the_floor_not_onto_the_ceiling() {
        // APlaceJustUnderACeilingDropsToTheFloorNotOntoTheCeiling.
        let world = dungeon_with_tall_rooms();
        let under_the_ceiling = v(30.0, 10.0, -25.3);
        assert_eq!(
            placed(&world, TALL_ROOM, under_the_ceiling),
            under_the_ceiling.with_z(FLOOR)
        );
    }
}
