//! What the movement sweep knows about the object it moves.

use std::collections::{HashMap, HashSet};

use ac_core::physics::PhysicsState;

use crate::collision_flags::EntityCollisionFlags;
use crate::globals::{DEFAULT_STEP_HEIGHT, FLOOR_Z, LANDING_Z};
use crate::state::{ObjectInfoState, TransitionState};

/// Who may enter a house.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct HouseRestrictions {
    /// Anyone may enter.
    pub open_to_public: bool,
    /// Members of this monarch's allegiance may enter (0 for none).
    pub allegiance_monarch_id: u32,
    /// Guests by id.
    pub guests: HashSet<u32>,
}

impl HouseRestrictions {
    /// True when the mover (with its monarch) may enter.
    #[must_use]
    pub fn is_allowed_in(&self, mover_id: u32, mover_monarch_id: u32) -> bool {
        if self.open_to_public {
            return true;
        }
        if self.allegiance_monarch_id != 0 && mover_monarch_id == self.allegiance_monarch_id {
            return true;
        }
        mover_id != 0 && self.guests.contains(&mover_id)
    }
}

/// The object guarding a restricted cell (a house), as the client knows it.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct RestrictionObject {
    /// The house owner, when known.
    pub house_owner_id: Option<u32>,
    /// The house's entry list, when known.
    pub restrictions: Option<HouseRestrictions>,
}

/// The client objects entry checks consult: restriction objects by id and
/// objects' monarchs.
#[derive(Debug, Clone, Default)]
pub struct ObjectRestrictions {
    objects: HashMap<u32, RestrictionObject>,
    monarchs: HashMap<u32, u32>,
}

impl ObjectRestrictions {
    /// An empty table.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Sets (or replaces) a restriction object.
    pub fn set_object(&mut self, id: u32, object: RestrictionObject) {
        self.objects.insert(id, object);
    }

    /// Removes a restriction object.
    pub fn remove_object(&mut self, id: u32) {
        self.objects.remove(&id);
    }

    /// Sets an object's monarch (0 removes it).
    pub fn set_monarch(&mut self, id: u32, monarch_id: u32) {
        if monarch_id == 0 {
            self.monarchs.remove(&id);
        } else {
            self.monarchs.insert(id, monarch_id);
        }
    }

    /// A restriction object.
    #[must_use]
    pub fn object(&self, id: u32) -> Option<&RestrictionObject> {
        self.objects.get(&id)
    }

    /// An object's monarch, 0 when unknown.
    #[must_use]
    pub fn monarch_of(&self, id: u32) -> u32 {
        self.monarchs.get(&id).copied().unwrap_or(0)
    }
}

/// The moving object, as the sweep sees it.
#[derive(Debug, Clone, PartialEq)]
pub struct ObjectInfo {
    /// State bits (contact, walkable, viewer, clipped, player, ...).
    pub state: ObjectInfoState,
    /// How high it may step up, metres.
    pub step_up_height: f32,
    /// How far it may step down, metres.
    pub step_down_height: f32,
    /// It passes through non-static objects.
    pub ethereal: bool,
    /// It steps down onto ground below it (missiles do not).
    pub step_down: bool,
    /// Its scale.
    pub scale: f32,
    /// Its physics state bits.
    pub mover_physics_state: PhysicsState,
    /// The designated projectile target, 0 for none.
    pub target_id: u32,
    /// Its own id (its own shadow rows are skipped), 0 for none.
    pub self_entity_id: u32,
    /// Gravity acts on it.
    pub mover_has_gravity: bool,
    /// The sweep asked for its velocity to be killed.
    pub velocity_killed: bool,
}

impl Default for ObjectInfo {
    fn default() -> Self {
        Self {
            state: ObjectInfoState(ObjectInfoState::NONE),
            step_up_height: DEFAULT_STEP_HEIGHT,
            step_down_height: 0.04,
            ethereal: false,
            step_down: true,
            scale: 1.0,
            mover_physics_state: PhysicsState::NONE,
            target_id: 0,
            self_entity_id: 0,
            mover_has_gravity: false,
            velocity_killed: false,
        }
    }
}

impl ObjectInfo {
    /// Touching something.
    #[must_use]
    pub fn contact(&self) -> bool {
        self.state.contains(ObjectInfoState::CONTACT)
    }

    /// Standing on something walkable.
    #[must_use]
    pub fn on_walkable(&self) -> bool {
        self.state.contains(ObjectInfoState::ON_WALKABLE)
    }

    /// The camera.
    #[must_use]
    pub fn is_viewer(&self) -> bool {
        self.state.contains(ObjectInfoState::IS_VIEWER)
    }

    /// A player.
    #[must_use]
    pub fn is_player(&self) -> bool {
        self.state.contains(ObjectInfoState::IS_PLAYER)
    }

    /// Slides along edges rather than falling off them.
    #[must_use]
    pub fn edge_slide(&self) -> bool {
        self.state.contains(ObjectInfoState::EDGE_SLIDE)
    }

    /// Stops at what it touches.
    #[must_use]
    pub fn path_clipped(&self) -> bool {
        self.state.contains(ObjectInfoState::PATH_CLIPPED)
    }

    /// Turns freely.
    #[must_use]
    pub fn free_rotate(&self) -> bool {
        self.state.contains(ObjectInfoState::FREE_ROTATE)
    }

    /// May enter restricted places.
    #[must_use]
    pub fn can_bypass_move_restrictions(&self) -> bool {
        self.state
            .contains(ObjectInfoState::CAN_BYPASS_MOVE_RESTRICTIONS)
    }

    /// True when the mover, a missile, ignores the target: missiles ignore
    /// each other; a missile ignores an ethereal object and, when it has a
    /// designated target, every creature but that target.
    #[must_use]
    pub fn missile_ignore(
        &self,
        target_entity_id: u32,
        target_physics_state: u32,
        target_flags: EntityCollisionFlags,
    ) -> bool {
        let target_state = PhysicsState(target_physics_state);
        if target_state.contains(PhysicsState::MISSILE) {
            return true;
        }
        if !self.mover_physics_state.contains(PhysicsState::MISSILE) {
            return false;
        }
        if target_entity_id == self.target_id {
            return false;
        }
        let has_weenie = target_flags.contains(EntityCollisionFlags::HAS_WEENIE);
        if target_state.contains(PhysicsState::ETHEREAL) && has_weenie {
            return true;
        }
        self.target_id != 0
            && has_weenie
            && target_flags.contains(EntityCollisionFlags::IS_CREATURE)
    }

    /// Whether a player may enter a cell guarded by `cell_restriction_obj`.
    /// Non-players, movers allowed everywhere and unguarded cells pass; an
    /// unknown guard blocks; an unowned house, the owner, or a house with no
    /// entry list admits; otherwise the entry list decides.
    #[must_use]
    pub fn check_entry_restrictions(
        &self,
        cell_restriction_obj: u32,
        objects: Option<&ObjectRestrictions>,
    ) -> TransitionState {
        if !self.is_player() {
            return TransitionState::Ok;
        }
        if self.can_bypass_move_restrictions() {
            return TransitionState::Ok;
        }
        if cell_restriction_obj == 0 {
            return TransitionState::Ok;
        }

        let Some(restriction_object) = objects.and_then(|o| o.object(cell_restriction_obj)) else {
            return TransitionState::Collided;
        };

        let mover_id = self.self_entity_id;
        let house_owner_id = restriction_object.house_owner_id.unwrap_or(0);
        if house_owner_id == 0 || house_owner_id == mover_id {
            return TransitionState::Ok;
        }

        let Some(restrictions) = &restriction_object.restrictions else {
            return TransitionState::Ok;
        };

        let mover_monarch_id = objects.map_or(0, |o| o.monarch_of(mover_id));
        if restrictions.is_allowed_in(mover_id, mover_monarch_id) {
            TransitionState::Ok
        } else {
            TransitionState::Collided
        }
    }

    /// The smallest up-component of a normal the object may stand on:
    /// walking slopes when on walkable ground, landing slopes otherwise.
    #[must_use]
    pub fn get_walkable_z(&self) -> f32 {
        if self.on_walkable() {
            FLOOR_Z
        } else {
            LANDING_Z
        }
    }

    /// Asks for the object's velocity to be killed.
    pub fn stop_velocity(&mut self) {
        self.velocity_killed = true;
    }
}
