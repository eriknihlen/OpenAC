//! What a walk reads and moves, over the runtime's owners: the local
//! player's body and its scripted moves, the objects the client knows and
//! their collision, and the doors it opens.

use std::collections::{HashMap, HashSet};

use ac_core::combat::{CombatObject, is_hostile_monster};
use ac_core::items::{ClientObject, PublicWeenieFlags};
use ac_core::physics::PhysicsState;
use ac_core::properties::{PropertyBool, PropertyFloat};
use ac_motion::move_to_math::heading_from_yaw;
use ac_motion::{RUN_TURN_FACTOR, WALK_ANIM_SPEED};
use ac_nav::driver::RouteTurning;
use ac_nav::geometry::{self, NavBody, NavSurfaces};
use ac_nav::leaps::{NavLeapAbility, NavLeapPhysics};
use ac_nav::motor::{MoveChannel, MovePace, MoveRequest};
use ac_nav::route::NavAvoidance;
use ac_nav::sources::{
    NavObjectKind, PORTAL_LEAST_RADIUS, except_the_one_the_walk_ends, on_the_cell_floor,
    on_the_ground, place_offset, portal_footprint, stands_anywhere,
};
use ac_nav::walk::{
    NavBlocker, NavDoor, NavDoors, NavGoalSource, SAFE_DROP_METERS, WalkBody, WalkBodySample,
};
use ac_physics::movement::PlayerMovementController;
use ac_physics::{PhysicsEngine, ShadowEntry};
use glam::{Vec2, Vec3};

use crate::movement::input::ScriptedInput;
use crate::movement::residency::Residency;
use crate::world::{World, WorldObject};

/// How fast a character turns at a walk, 1.5 radians a second as its
/// motion table sets; at a run it turns half again as fast.
const WALK_TURN_DEGREES_PER_SECOND: f32 = 1.5 * (180.0 / std::f32::consts::PI);
/// How far a character goes on after it lets go of a steady walk while still
/// holding the pace it moved at, as a scripted move lets go; measured on the
/// human motion table, the same at every run skill.
pub(super) const WALK_STOP_METERS: f32 = 0.41;
/// The same for a run.
pub(super) const RUN_STOP_METERS: f32 = 0.79;
/// A door more than this far above or below the character is not on its
/// way, metres.
const DOOR_HEIGHT_REACH: f32 = 3.0;
/// How near an object's middle the client uses it where the character
/// stands, where the server sends no range of its own, metres.
const DEFAULT_USE_REACH: f32 = 0.6;

/// The local player's body, sampled from its movement controller and moved
/// with scripted moves.
pub(super) struct Body<'a> {
    pub controller: Option<&'a PlayerMovementController>,
    pub input: &'a mut ScriptedInput,
    pub auto_run: &'a mut bool,
    pub in_portal_space: bool,
}

impl WalkBody for Body<'_> {
    fn try_sample(&self) -> Option<WalkBodySample> {
        let controller = self.controller.filter(|c| c.cell_id() != 0)?;
        let jump_height = controller.full_jump_height();
        let body = controller.body();
        Some(WalkBodySample {
            position: controller.position(),
            heading_degrees: heading_from_yaw(controller.yaw()),
            body: NavBody::player(controller.step_up_height, controller.step_down_height),
            moves: self.input.snapshot(),
            in_portal_space: self.in_portal_space,
            cell_id: controller.cell_id(),
            leaps: (jump_height > 0.0).then(|| NavLeapAbility {
                walk_speed: WALK_ANIM_SPEED,
                run_speed: controller.run_speed(),
                full_jump_height: jump_height,
                maximum_drop: SAFE_DROP_METERS,
                physics: NavLeapPhysics {
                    step_seconds: controller.physics_step_seconds(),
                    elasticity: body.elasticity,
                    friction: body.friction,
                },
            }),
            airborne: controller.is_airborne(),
            turning: Some(RouteTurning {
                run_speed: controller.run_speed(),
                run_turn_degrees_per_second: WALK_TURN_DEGREES_PER_SECOND * RUN_TURN_FACTOR,
                walk_speed: WALK_ANIM_SPEED,
                walk_turn_degrees_per_second: WALK_TURN_DEGREES_PER_SECOND,
            }),
            walk_stop_meters: WALK_STOP_METERS,
            run_stop_meters: RUN_STOP_METERS,
            player_movement_input_frames: self.input.player_movement_input_frames(),
        })
    }

    fn begin_move(&mut self, request: MoveRequest) -> bool {
        if self.controller.is_none() || !self.input.scripted_mut().begin(request) {
            return false;
        }
        *self.auto_run = false;
        true
    }

    fn stop_move(&mut self, channel: MoveChannel) -> bool {
        self.input.scripted_mut().stop_channel(channel)
    }

    fn begin_jump(&mut self, power: f32, leave_at: Option<MovePace>) -> bool {
        self.controller.is_some() && self.input.scripted_mut().begin_jump(power, leave_at)
    }
}

/// What the rules need to know of an object the client knows.
fn kind_of(item: &ClientObject) -> NavObjectKind {
    NavObjectKind {
        item_type: item.item_type.bits(),
        public_weenie_flags: item.public_weenie_bitfield.map_or(0, |f| f.bits()),
        pet_owner_id: item.pet_owner_id,
    }
}

fn has_flag(item: &ClientObject, flag: PublicWeenieFlags) -> bool {
    item.public_weenie_bitfield
        .is_some_and(|flags| flags.intersects(flag))
}

fn is_ethereal(state: u32) -> bool {
    state & PhysicsState::ETHEREAL.bits() != 0
}

fn combat_object(item: &ClientObject) -> CombatObject {
    CombatObject {
        object_id: item.object_id(),
        item_type: item.item_type,
        public_weenie_bitfield: item.public_weenie_bitfield.map(|f| f.bits()),
        pet_owner_id: item.pet_owner_id,
    }
}

/// Objects as the physics world and the object table know them: found by
/// their collision, else by the position the server last gave them.
pub(super) struct Goals<'a> {
    pub engine: &'a PhysicsEngine,
    pub residency: &'a Residency,
    pub world: &'a World,
    pub controller: Option<&'a PlayerMovementController>,
    /// Where each body the runtime moves stands, by client id.
    pub bodies: &'a HashMap<u32, Vec3>,
}

impl Goals<'_> {
    fn player(&self) -> u32 {
        self.world.player_guid().unwrap_or(0)
    }

    /// The server object an entry of the collision lists belongs to.
    fn object_of(&self, entry: &ShadowEntry) -> Option<&WorldObject> {
        self.world.object_by_local_id(entry.entity_id)
    }

    /// Where an object the client knows stands in the physics world: its
    /// body, else the position the server gave it.
    pub(super) fn place(&self, object: &WorldObject) -> Option<Vec3> {
        if let Some(at) = self.bodies.get(&object.key().local_id) {
            return Some(*at);
        }
        let placed = object.accepted_position()?;
        let origin = self.residency.origin_of(placed.cell_id)?;
        Some(origin + Vec3::new(placed.origin.x, placed.origin.y, placed.origin.z))
    }

    fn footprint(&self, entry: &ShadowEntry) -> NavAvoidance {
        geometry::footprint_of(entry, self.engine.world())
    }

    fn here(&self) -> Option<(Vec3, u32, Vec3)> {
        let controller = self.controller?;
        let cell = controller.current_cell_position();
        Some((controller.position(), cell.obj_cell_id, cell.frame.origin))
    }
}

impl NavGoalSource for Goals<'_> {
    fn try_locate(&self, object_id: u32) -> Option<Vec3> {
        let known = self.world.object(object_id);
        if let Some(at) = known.and_then(|o| self.bodies.get(&o.key().local_id)) {
            return Some(*at);
        }
        if let Some(entry) = self.engine.shadows.capture_entries_for(object_id).first() {
            return Some(entry.position);
        }
        known.and_then(|o| self.place(o))
    }

    fn try_find_blocker(&self, position: Vec3, radius: f32) -> Option<NavBlocker> {
        let player = self.player();
        let mut nearest = radius;
        let mut found: Option<(&WorldObject, NavAvoidance)> = None;
        for entry in self.engine.shadows.capture_entries() {
            let Some(object) = self.object_of(&entry) else {
                continue;
            };
            if object.guid() == player || !stands_anywhere(object.physics().state) {
                continue;
            }
            let footprint = self.footprint(&entry);
            let edge = footprint.centre.truncate().distance(position.truncate()) - footprint.radius;
            if edge < nearest {
                nearest = edge;
                found = Some((object, footprint));
            }
        }
        let (object, footprint) = found?;
        let object_id = object.guid();
        let item = self.world.item(object_id);
        let door = item.is_some_and(|i| has_flag(i, PublicWeenieFlags::DOOR));
        let moves = item.is_some_and(|i| kind_of(i).moves());
        let player_item = self.world.item(player).map(combat_object);
        Some(NavBlocker {
            object_id,
            name: item
                .map(|i| i.name.clone())
                .or_else(|| self.world.name(object_id).map(str::to_owned))
                .unwrap_or_else(|| format!("0x{object_id:08X}")),
            is_closed_door: door && !is_ethereal(object.physics().state),
            position: footprint.centre,
            radius: footprint.radius,
            moves,
            hostile: moves
                && is_hostile_monster(
                    player,
                    player_item.as_ref(),
                    item.map(combat_object).as_ref(),
                ),
        })
    }

    fn find_obstacles(
        &self,
        around: Vec3,
        radius: f32,
        goal_object_id: u32,
        in_grid: Option<&HashSet<u32>>,
    ) -> Vec<NavAvoidance> {
        let player = self.player();
        let mut obstacles = Vec::new();
        for entry in self
            .engine
            .shadows
            .capture_entries_near(around.truncate(), radius)
        {
            if in_grid.is_some_and(|ids| ids.contains(&entry.entity_id)) || is_ethereal(entry.state)
            {
                continue;
            }
            let Some(object) = self.object_of(&entry) else {
                continue;
            };
            let state = object.physics().state;
            if object.guid() == player
                || object.guid() == goal_object_id
                || is_ethereal(state)
                || !stands_anywhere(state)
                || !self
                    .world
                    .item(object.guid())
                    .is_some_and(|i| kind_of(i).stands_in_the_way())
            {
                continue;
            }
            obstacles.push(self.footprint(&entry));
        }
        obstacles
    }

    fn stands_still(&self, entity_id: u32) -> bool {
        let Some(object) = self.world.object_by_local_id(entity_id) else {
            return false;
        };
        let state = object.physics().state;
        object.guid() != self.player()
            && !is_ethereal(state)
            && stands_anywhere(state)
            && self
                .world
                .item(object.guid())
                .is_some_and(|i| kind_of(i).can_be_stood_on())
    }

    fn find_crowd(&self, around: Vec3, radius: f32, goal_object_id: u32) -> Vec<NavAvoidance> {
        let player = self.player();
        let mut crowd: Vec<(u32, NavAvoidance)> = Vec::new();
        for entry in self
            .engine
            .shadows
            .capture_entries_near(around.truncate(), radius)
        {
            if is_ethereal(entry.state) {
                continue;
            }
            let Some(object) = self.object_of(&entry) else {
                continue;
            };
            let guid = object.guid();
            if guid == player
                || guid == goal_object_id
                || is_ethereal(object.physics().state)
                || !self.world.item(guid).is_some_and(|i| kind_of(i).moves())
            {
                continue;
            }
            let footprint = self.footprint(&entry);
            match crowd.iter_mut().find(|(id, _)| *id == guid) {
                Some((_, kept)) if footprint.radius > kept.radius => *kept = footprint,
                Some(_) => {}
                None => crowd.push((guid, footprint)),
            }
        }
        crowd.into_iter().map(|(_, footprint)| footprint).collect()
    }

    fn find_portals(
        &self,
        around: Vec3,
        radius: f32,
        goal_object_id: u32,
        goal: Vec3,
    ) -> Vec<NavAvoidance> {
        let flat = around.truncate();
        let mut portals = Vec::new();
        let mut found = HashSet::new();
        for entry in self.engine.shadows.capture_entries_near(flat, radius) {
            let Some(object) = self.object_of(&entry) else {
                continue;
            };
            let guid = object.guid();
            if guid == goal_object_id
                || !self
                    .world
                    .item(guid)
                    .is_some_and(|i| kind_of(i).is_portal())
            {
                continue;
            }
            portals.push(portal_footprint(self.footprint(&entry)));
            found.insert(guid);
        }
        // A portal need not collide at all; every portal the client knows of
        // is kept out of all the same, by where it stands.
        for object in self.world.objects() {
            let guid = object.guid();
            if guid == goal_object_id
                || found.contains(&guid)
                || !self
                    .world
                    .item(guid)
                    .is_some_and(|i| kind_of(i).is_portal())
            {
                continue;
            }
            let Some(at) = self.place(object) else {
                continue;
            };
            if at.truncate().distance(flat) > radius + PORTAL_LEAST_RADIUS {
                continue;
            }
            portals.push(portal_footprint(NavAvoidance::new(at, 0.0)));
        }
        except_the_one_the_walk_ends(&portals, goal)
    }

    /// A place with no height is first given the terrain's height there (or
    /// the character's, where no terrain is loaded); a place in an interior
    /// cell is then stood on a floor as [`on_the_cell_floor`] says, so in a
    /// dungeon a place with no height ends on a floor under that height too.
    fn try_locate_place(&self, cell_id: u32, local: Vec3) -> Option<Vec3> {
        let (position, here_cell, here_local) = self.here()?;
        let stands_on = |id: u32| self.stands_still(id);
        Some(on_the_cell_floor(
            self.engine,
            cell_id,
            on_the_ground(
                self.engine,
                position + place_offset(cell_id, local, here_cell, here_local),
                position.z,
            ),
            Some(&stands_on),
        ))
    }

    fn try_global_of(&self, world: Vec3) -> Option<Vec3> {
        // OpenAC `RuntimeNavigationGoalSource.TryGlobalOf`: measured from the
        // character, whose own place is known in its landblock.
        let (position, here_cell, here_local) = self.here()?;
        Some(world - position - place_offset(0, Vec3::ZERO, here_cell, here_local))
    }

    fn try_get_surfaces(&self, object_id: u32) -> Option<NavSurfaces> {
        let entity_id = self
            .world
            .object(object_id)
            .map_or(object_id, |o| o.key().local_id);
        geometry::surfaces_of(self.engine, entity_id)
    }

    fn is_player(&self, object_id: u32) -> bool {
        self.world
            .item(object_id)
            .is_some_and(|i| has_flag(i, PublicWeenieFlags::PLAYER))
    }

    fn try_find_portal(&self, near: Vec3, radius: f32) -> Option<(u32, Vec3)> {
        let flat: Vec2 = near.truncate();
        let mut nearest = radius;
        let mut found = None;
        for object in self.world.objects() {
            let guid = object.guid();
            if !self
                .world
                .item(guid)
                .is_some_and(|i| kind_of(i).is_portal())
            {
                continue;
            }
            let Some(at) = self.place(object) else {
                continue;
            };
            let away = at.truncate().distance(flat);
            if away <= nearest {
                nearest = away;
                found = Some((guid, at));
            }
        }
        found
    }
}

/// Doors as the client sees them: closed while their collision still
/// stops a body; used through the client's own use, which walks the
/// character into the door's use range first.
pub(super) struct Doors<'a> {
    pub goals: &'a Goals<'a>,
    /// The doors the walk asked to use this tick, for the session to use.
    pub uses: &'a mut Vec<u32>,
    /// Asks the server to appraise an object quietly; false when the client
    /// would not ask.
    pub appraise: &'a mut dyn FnMut(u32) -> bool,
}

impl Doors<'_> {
    fn is_door(&self, object_id: u32) -> bool {
        self.goals
            .world
            .item(object_id)
            .is_some_and(|i| has_flag(i, PublicWeenieFlags::DOOR))
    }
}

impl NavDoors for Doors<'_> {
    fn try_find_closed_door(&self, from: Vec3, to: Vec3, corridor: f32) -> Option<NavDoor> {
        let start = from.truncate();
        let along = to.truncate() - start;
        let length_squared = along.length_squared();
        let mut nearest: Option<(u32, NavAvoidance)> = None;
        let mut nearest_ahead = f32::INFINITY;
        for entry in self.goals.engine.shadows.capture_entries() {
            if (entry.position.z - from.z).abs() > DOOR_HEIGHT_REACH {
                continue;
            }
            let Some(object) = self.goals.object_of(&entry) else {
                continue;
            };
            if is_ethereal(object.physics().state) || !self.is_door(object.guid()) {
                continue;
            }
            let footprint = self.goals.footprint(&entry);
            let at = footprint.centre.truncate();
            let t = if length_squared > 1e-6 {
                (at - start).dot(along) / length_squared
            } else {
                0.0
            };
            if t < 0.0 {
                continue;
            }
            let t = t.min(1.0);
            if at.distance(start + along * t) > corridor {
                continue;
            }
            let ahead = t * length_squared.sqrt();
            if ahead < nearest_ahead {
                nearest_ahead = ahead;
                nearest = Some((object.guid(), footprint));
            }
        }
        let (object_id, footprint) = nearest?;
        Some(NavDoor {
            object_id,
            name: self
                .goals
                .world
                .item(object_id)
                .map_or_else(|| format!("0x{object_id:08X}"), |i| i.name.clone()),
            position: footprint.centre,
            radius: footprint.radius,
        })
    }

    fn is_open(&self, door_id: u32) -> bool {
        self.goals
            .world
            .object(door_id)
            .is_some_and(|o| is_ethereal(o.physics().state))
    }

    fn use_door(&mut self, door_id: u32) {
        self.uses.push(door_id);
    }

    fn use_reach(&self, door_id: u32) -> f32 {
        self.goals
            .world
            .item(door_id)
            .map_or(DEFAULT_USE_REACH, |i| {
                i.properties
                    .get_float(PropertyFloat::USE_RADIUS.0, f64::from(DEFAULT_USE_REACH))
                    as f32
            })
    }

    fn is_locked(&self, door_id: u32) -> Option<bool> {
        let item = self.goals.world.item(door_id)?;
        (item.last_appraisal_time_ms != 0).then(|| {
            item.properties
                .bools
                .get(&PropertyBool::LOCKED.0)
                .copied()
                .unwrap_or(false)
        })
    }

    fn appraise(&mut self, door_id: u32) -> bool {
        (self.appraise)(door_id)
    }
}
