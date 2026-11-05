//! Entering the world: every new top-level object other than the
//! character (a creature, an item on the ground, a door, a missile) is put
//! down by the create's placement, which may slide it aside, so it never
//! starts inside another body or a wall (two bodies inside each other never
//! sweep apart). The placement's result is where the object's body starts,
//! or, for an object nothing moves, where it rests: the world sees it there
//! and its collision stands there. An object whose placement finds no clear
//! spot does not enter the world: it stays known but gets no body, is not
//! drawn, and its later updates do not move it, for the rest of its
//! incarnation.

use ac_physics::PhysicsSetPositionResult;

use super::route::{CreatePositionRouteRequest, CreateResidenceKind, classify_create};
use super::{
    Movement, PhysicsState, Position, PositionAuthority, PositionDisposition, PositionEntityKind,
    World, WorldObject, remote,
};

/// How an object's entry into the world stands.
#[derive(Debug, Clone, PartialEq)]
pub(super) enum Entry {
    /// Its placement put it here (a body takes this as its start).
    Placed(PhysicsSetPositionResult),
    /// It entered, and the server has since put it at a position of its
    /// own: a body starts there.
    AtServer,
    /// Its placement found no clear spot: it is not in the world.
    Refused,
    /// Not yet: held, contained, outside the resident world, or its cell is
    /// not ready.
    Waiting,
}

/// An object without a body of its own that entered the world.
#[derive(Debug, Clone)]
pub(super) struct Resting {
    /// The object.
    pub guid: u32,
    /// Where its placement put it; `None` once the server put it elsewhere.
    pub placed: Option<PhysicsSetPositionResult>,
}

/// The create's route for `object`'s position: a top-level object is set
/// down with placement | slide.
pub(super) fn create_route(object: &WorldObject, is_player: bool) -> super::route::PositionRoute {
    let stamps = object.timestamps();
    let physics = object.physics();
    classify_create(&CreatePositionRouteRequest {
        authority: PositionAuthority {
            entity: object.key(),
            previous_teleport_sequence: stamps.teleport,
            accepted_teleport_sequence: stamps.teleport,
            timestamp_disposition: PositionDisposition::Apply,
        },
        entity_kind: if is_player {
            PositionEntityKind::LocalPlayer
        } else if physics.state & PhysicsState::MISSILE.bits() != 0 {
            PositionEntityKind::Projectile
        } else {
            PositionEntityKind::Remote
        },
        residence: if physics.parent.is_some() {
            CreateResidenceKind::Parented
        } else if physics.position.is_some() {
            CreateResidenceKind::TopLevel
        } else {
            CreateResidenceKind::PickedUp
        },
        accepted_position: physics.position,
        physics_state: physics.state,
    })
}

impl Movement {
    /// Whether `local_id`'s entry found no clear spot.
    pub(super) fn entry_refused(&self, local_id: u32) -> bool {
        self.refused.contains(&local_id)
    }

    /// Whether the object entered the world: it has a body, rests where it
    /// entered, or was refused.
    fn entered(&self, local_id: u32) -> bool {
        self.remotes.contains_key(&local_id)
            || self.projectiles.contains_key(&local_id)
            || self.ordinaries.contains_key(&local_id)
            || self.resting.contains_key(&local_id)
            || self.refused.contains(&local_id)
    }

    /// The entry of `guid` (not the character): what an earlier entry left,
    /// else its create's placement now. A first entry whose placement finds
    /// no spot refuses the object's entry for this incarnation; a later one
    /// enters at the server's position.
    pub(super) fn entry_placement(&mut self, world: &mut World, guid: u32) -> Entry {
        let Some(object) = world.object(guid) else {
            return Entry::Waiting;
        };
        let local_id = object.key().local_id;
        if let Some(rest) = self.resting.get(&local_id) {
            return rest.placed.clone().map_or(Entry::AtServer, Entry::Placed);
        }
        if self.refused.contains(&local_id) || object.entry_refused() {
            return Entry::Refused;
        }
        let Some(accepted) = object.accepted_position().copied() else {
            return Entry::Waiting;
        };
        if object.parent().is_some() || !self.residency.is_resident(accepted.cell_id) {
            return Entry::Waiting;
        }
        let route = create_route(object, false);
        if !route.performs_set_position() {
            return Entry::Waiting;
        }
        let Some((request, ..)) = self.placement_request(
            world,
            guid,
            local_id,
            &accepted,
            remote::wire_orientation(&accepted),
            route.set_position_flags.0,
            0,
        ) else {
            return Entry::Waiting;
        };
        let placed = self.engine.set_position(&request, None);
        if !placed.is_successful() {
            if self.first_entered.contains(&local_id) {
                // Only the create's first entry refuses an object: entering
                // again (its landblock back, the frame centred anew) where
                // nothing is clear, it stands where the server put it.
                return Entry::AtServer;
            }
            self.refused.insert(local_id);
            world.refuse_entry(guid);
            return Entry::Refused;
        }
        if placed.is_committed() {
            self.first_entered.insert(local_id);
            Entry::Placed(placed)
        } else {
            Entry::Waiting
        }
    }

    /// `guid` (not the character) enters the world: a body when it has
    /// motion, flies as a missile or moves by a velocity, else it rests
    /// where its placement put it.
    pub(super) fn enter_object(&mut self, world: &mut World, guid: u32, now: std::time::Duration) {
        let Some(object) = world.object(guid) else {
            return;
        };
        if world.player_guid() == Some(guid) {
            return;
        }
        if object.movement().is_some() {
            self.arm_remote(world, guid);
            return;
        }
        let entry = self.entry_placement(world, guid);
        let placed = match &entry {
            Entry::Placed(placed) => Some(placed),
            Entry::AtServer => None,
            Entry::Refused | Entry::Waiting => return,
        };
        if self.bind_projectile_at(world, guid, now, placed)
            || self.bind_ordinary_at(world, guid, now, false, placed)
        {
            return;
        }
        let placed = placed.cloned();
        self.rest(world, guid, placed);
    }

    /// `guid` rests where its entry put it: the world sees it there and its
    /// collision stands there.
    fn rest(&mut self, world: &mut World, guid: u32, placed: Option<PhysicsSetPositionResult>) {
        let Some(local_id) = world.object(guid).map(|o| o.key().local_id) else {
            return;
        };
        if let Some(placed) = &placed
            && let Some(origin) = self.residency.origin_of(placed.cell_id)
        {
            world.set_live_position(guid, placed_wire(placed, origin));
            if let Err(error) = self.collision.follow(
                &mut self.engine,
                &self.residency,
                local_id,
                placed.position,
                placed.orientation,
                placed.cell_id,
            ) {
                self.refuse(&format!(
                    "an object's collision could not follow it: {error}"
                ));
            }
        }
        self.resting.insert(local_id, Resting { guid, placed });
    }

    /// A resting object's collision was registered again (at the server's
    /// position): it goes back to where the object rests.
    pub(super) fn follow_resting(&mut self, local_id: u32) {
        let Some(Some(placed)) = self.resting.get(&local_id).map(|r| r.placed.as_ref()) else {
            return;
        };
        let (at, orientation, cell_id) = (placed.position, placed.orientation, placed.cell_id);
        if let Err(error) = self.collision.follow(
            &mut self.engine,
            &self.residency,
            local_id,
            at,
            orientation,
            cell_id,
        ) {
            self.refuse(&format!(
                "an object's collision could not follow it: {error}"
            ));
        }
    }

    /// The server put a resting object at a position of its own: that
    /// position stands (its collision is registered there by the caller).
    pub(super) fn unrest(&mut self, world: &mut World, local_id: u32) {
        if let Some(rest) = self.resting.get_mut(&local_id)
            && rest.placed.take().is_some()
        {
            world.clear_live_position(rest.guid);
        }
    }

    /// Objects that have not entered the world and stand in it now (their
    /// create came before their landblock's collision, or the character's
    /// landblocks changed) enter it.
    pub(super) fn enter_waiting(&mut self, world: &mut World) {
        let player = world.player_guid();
        let waiting: Vec<u32> = world
            .objects()
            .filter(|o| Some(o.guid()) != player && o.parent().is_none())
            .filter(|o| !self.entered(o.key().local_id))
            .filter(|o| {
                o.accepted_position()
                    .is_some_and(|p| self.residency.is_resident(p.cell_id))
            })
            .map(WorldObject::guid)
            .collect();
        let now = self.last_frame.unwrap_or_default();
        for guid in waiting {
            self.enter_object(world, guid, now);
        }
    }

    /// Resting objects outside the resident world leave it (they enter
    /// again when their landblock comes back).
    pub(super) fn drop_resting_outside(&mut self, world: &mut World) {
        let gone: Vec<u32> = self
            .resting
            .iter()
            .filter(|(_, r)| {
                world
                    .object(r.guid)
                    .and_then(WorldObject::accepted_position)
                    .is_none_or(|p| !self.residency.is_resident(p.cell_id))
            })
            .map(|(local, _)| *local)
            .collect();
        for local in gone {
            if let Some(rest) = self.resting.remove(&local) {
                world.clear_live_position(rest.guid);
            }
        }
    }
}

/// A placement's result as the wire carries positions, given the corner of
/// its landblock.
pub(super) fn placed_wire(
    placed: &PhysicsSetPositionResult,
    landblock_origin: ac_physics::Vec3,
) -> Position {
    let o = placed.position - landblock_origin;
    let q = placed.orientation;
    Position {
        cell_id: placed.cell_id,
        origin: ac_proto::Vector3 {
            x: o.x,
            y: o.y,
            z: o.z,
        },
        orientation: ac_proto::Quaternion {
            w: q.w,
            x: q.x,
            y: q.y,
            z: q.z,
        },
    }
}
