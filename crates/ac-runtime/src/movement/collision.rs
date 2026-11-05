//! The collision of the objects the server places: doors, signs, vendors,
//! creatures and other players stand in the shadow lists as the window's
//! client registers them, so the player's body meets them, a closed door
//! stops it, and a walk finds them where they stand.
//!
//! An object is registered once it has a position in a resident landblock
//! and a setup: the collision of its parts (posed as its motion table's
//! default state, else the setup's resting pose, with the part swaps it was
//! sent) or its setup's cylinders and spheres, at its scale, with its
//! physics state and the collision flags its description gives. A body the
//! runtime moves (a creature, a player) has its rows follow it each frame;
//! anything else stands where its entry into the world placed it, and moves
//! when the server places it. Its state follows the
//! server's (a door that opens turns ethereal), and it leaves the lists
//! when it leaves the world or its landblock stops being resident.

use std::collections::HashMap;

use ac_core::items::{ClientObject, ItemType};
use ac_physics::content::{
    ContentError, ServerObjectShape, register_server_object, server_object_flags,
};
use ac_physics::{PhysicsEngine, Quat, RegistrationSite, Vec3};

use super::content::WorldContent;
use super::remote::wire_orientation;
use super::residency::Residency;
use crate::world::World;

/// The objects registered, by their client id: the server id each belongs
/// to, and where a body the runtime moves last had its rows put.
#[derive(Debug, Default)]
pub(super) struct ObjectCollision {
    registered: HashMap<u32, Registered>,
}

#[derive(Debug, Clone, Copy)]
struct Registered {
    guid: u32,
    followed: Option<(Vec3, Quat, u32)>,
    suspended: bool,
}

/// Why an object's collision could not be registered or moved.
#[derive(Debug, thiserror::Error)]
pub(super) enum CollisionError {
    /// A dat read, a malformed collision tree or a flood while registering.
    #[error(transparent)]
    Content(#[from] ContentError),
    /// The rows could not be flooded into the cells around the object.
    #[error("shadow flood: {0}")]
    Flood(#[from] ac_physics::cell_transit::CellTransitError),
}

impl ObjectCollision {
    /// Whether an object (by client id) is registered.
    #[cfg(test)]
    #[must_use]
    pub(super) fn is_registered(&self, local_id: u32) -> bool {
        self.registered.contains_key(&local_id)
    }

    /// Everything was dropped with the engine's contents (a new frame
    /// centre, the character leaving the world).
    pub(super) fn forget_all(&mut self) {
        self.registered.clear();
    }

    /// Takes an object out of the lists.
    pub(super) fn deregister(&mut self, engine: &mut PhysicsEngine, local_id: u32) {
        if self.registered.remove(&local_id).is_some() {
            engine.shadows.deregister(local_id);
        }
    }

    /// The server changed an object's physics state.
    pub(super) fn update_state(&self, engine: &mut PhysicsEngine, local_id: u32, state: u32) {
        if self.registered.contains_key(&local_id) {
            engine.shadows.update_physics_state(local_id, state);
        }
    }

    /// An object's public bitfield changed (a player-killer status update):
    /// its rows take the player, PK, PK-lite, impenetrable and bypass bits
    /// from it, keeping the rest.
    pub(super) fn update_pwd_bitfield(&self, engine: &mut PhysicsEngine, local_id: u32, bits: u32) {
        if self.registered.contains_key(&local_id) {
            engine.shadows.update_pwd_bitfield_flags(local_id, bits);
        }
    }

    /// A body the runtime moves stands somewhere new: its rows follow it.
    ///
    /// # Errors
    /// The rows could not be flooded from the new position (they stay where
    /// they were).
    pub(super) fn follow(
        &mut self,
        engine: &mut PhysicsEngine,
        residency: &Residency,
        local_id: u32,
        position: Vec3,
        orientation: Quat,
        cell_id: u32,
    ) -> Result<(), CollisionError> {
        if cell_id == 0 {
            return Ok(());
        }
        let Some(entry) = self.registered.get_mut(&local_id) else {
            return Ok(());
        };
        let at = (position, orientation, cell_id);
        if entry.followed == Some(at) {
            return Ok(());
        }
        entry.followed = Some(at);
        let Some(origin) = residency.origin_of(cell_id) else {
            return Ok(());
        };
        let site = RegistrationSite {
            world_pos: position,
            rotation: orientation,
            world_offset_x: origin.x,
            world_offset_y: origin.y,
            landblock_id: cell_id,
            seed_cell_id: cell_id,
        };
        let (shadows, world) = engine.shadows_and_world();
        shadows.update_position(world, local_id, site)?;
        Ok(())
    }

    /// The local player's rows follow its body: again only once it moved
    /// more than a centimetre, turned or changed cell. With no cell its
    /// rows leave every cell (kept registered, they come back where it
    /// stands once it has a cell again).
    ///
    /// Hiding does not take the player out of collision: a hidden player
    /// still stands in its cell and still blocks. The server hides the
    /// player for the whole portal-space stay, and an object created at the
    /// destination in that time has to be placed against the player, not on
    /// top of it.
    ///
    /// # Errors
    /// The rows could not be flooded from the new position.
    pub(super) fn follow_player(
        &mut self,
        engine: &mut PhysicsEngine,
        residency: &Residency,
        local_id: u32,
        position: Vec3,
        orientation: Quat,
        cell_id: u32,
    ) -> Result<(), CollisionError> {
        let Some(entry) = self.registered.get_mut(&local_id) else {
            return Ok(());
        };
        if cell_id == 0 {
            entry.followed = None;
            if !entry.suspended {
                entry.suspended = true;
                engine.shadows.suspend(local_id);
            }
            return Ok(());
        }
        entry.suspended = false;
        if let Some((last, last_orientation, last_cell)) = entry.followed
            && last_cell == cell_id
            && last.distance_squared(position) <= 1e-4
            && last_orientation.dot(orientation).abs() >= 0.99999
        {
            return Ok(());
        }
        entry.followed = None;
        self.follow(engine, residency, local_id, position, orientation, cell_id)
    }

    /// Registers every object that should collide and is not registered,
    /// and drops every registered one whose landblock is no longer resident
    /// or that is gone. Returns what could not be registered.
    pub(super) fn sync(
        &mut self,
        engine: &mut PhysicsEngine,
        content: &WorldContent,
        residency: &Residency,
        world: &World,
    ) -> Vec<(u32, CollisionError)> {
        let gone: Vec<u32> = self
            .registered
            .iter()
            .filter(|(local, entry)| {
                world
                    .object(entry.guid)
                    .and_then(|o| o.accepted_position())
                    .is_none_or(|p| !residency.is_resident(p.cell_id))
                    || world
                        .object(entry.guid)
                        .is_none_or(|o| o.key().local_id != **local)
            })
            .map(|(local, _)| *local)
            .collect();
        for local in gone {
            self.deregister(engine, local);
        }
        let guids: Vec<u32> = world
            .objects()
            .map(crate::world::WorldObject::guid)
            .collect();
        let mut failed = Vec::new();
        for guid in guids {
            let local = world.object(guid).map_or(0, |o| o.key().local_id);
            if !self.registered.contains_key(&local)
                && let Err(error) = self.register(engine, content, residency, world, guid)
            {
                failed.push((guid, error));
            }
        }
        failed
    }

    /// Registers (or registers again, at its accepted position) one server
    /// object (the local player too: other bodies meet it). Nothing for an
    /// object held by another, one with
    /// no position in a resident landblock, or one with no collision.
    ///
    /// # Errors
    /// A dat read, a malformed collision tree, or a flood that failed; the
    /// object is left unregistered.
    pub(super) fn register(
        &mut self,
        engine: &mut PhysicsEngine,
        content: &WorldContent,
        residency: &Residency,
        world: &World,
        guid: u32,
    ) -> Result<(), CollisionError> {
        let Some(object) = world.object(guid) else {
            return Ok(());
        };
        let local_id = object.key().local_id;
        self.deregister(engine, local_id);
        if object.parent().is_some() {
            return Ok(());
        }
        let physics = object.physics();
        let (Some(position), Some(setup_id)) = (object.accepted_position(), physics.setup_id)
        else {
            return Ok(());
        };
        let Some(origin) = residency
            .is_resident(position.cell_id)
            .then(|| residency.origin_of(position.cell_id))
            .flatten()
        else {
            return Ok(());
        };
        let shape = server_object_shape(object, setup_id, world.item(guid));
        let site = RegistrationSite {
            world_pos: Vec3::new(position.origin.x, position.origin.y, position.origin.z) + origin,
            rotation: wire_orientation(position),
            world_offset_x: origin.x,
            world_offset_y: origin.y,
            landblock_id: position.cell_id,
            seed_cell_id: position.cell_id,
        };
        if !register_server_object(
            engine,
            content.dats(),
            local_id,
            &shape,
            physics.state,
            site,
        )? {
            return Ok(());
        }
        self.registered.insert(
            local_id,
            Registered {
                guid,
                followed: None,
                suspended: false,
            },
        );
        Ok(())
    }
}

/// What an object (with setup `setup_id`) collides as, from its physics
/// description and its public description `item`.
fn server_object_shape(
    object: &crate::world::WorldObject,
    setup_id: u32,
    item: Option<&ClientObject>,
) -> ServerObjectShape {
    let physics = object.physics();
    ServerObjectShape {
        setup_id,
        scale: physics.scale.unwrap_or(1.0),
        part_changes: object
            .model()
            .anim_part_changes
            .iter()
            .map(|c| (usize::from(c.part_index), c.new_model_id))
            .collect(),
        motion_table_id: physics.motion_table_id,
        placement: crate::world::rest_placement(physics),
        flags: server_object_flags(
            item.and_then(|i| i.public_weenie_bitfield)
                .map(|b| b.bits()),
            item.is_some_and(|i| i.item_type == ItemType::CREATURE),
        ),
    }
}
