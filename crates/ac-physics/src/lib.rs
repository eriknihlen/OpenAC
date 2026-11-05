//! Collision geometry, the movement sweep, object shadows and the physics
//! body.
//!
//! Units are metres, seconds and radians. Positions inside a landblock run
//! 0..192 on x and y; z is up. A *plane* is the set of points `p` with
//! `normal · p + d = 0`; its signed distance to a point is positive on the
//! side the normal points to (the "front").
//!
//! What lives here:
//!
//! - Collision queries (slice 1): [`primitives`], [`polygon`],
//!   [`PhysicsBsp`] and [`CellBsp`], [`BspTransition`],
//!   [`TerrainSurface`], [`CellSurface`], [`PortalPlane`], [`CellArray`].
//! - The collision world: [`CollisionWorld`] holds interior cells, building
//!   shells and model collision; [`cell_transit`] works out which cells a
//!   body reaches and which holds it.
//! - Object shadows: [`ShadowObjectRegistry`] lists each object's collision
//!   parts in every cell its shape reaches; [`shadow_builder`] builds those
//!   parts from dat setups and placed models.
//! - The movement sweep: [`Transition`] moves a body's spheres through the
//!   world, colliding, stepping and sliding.
//! - [`PhysicsEngine`]: the resident landblocks, the world and the shadows,
//!   with the two entry points [`PhysicsEngine::resolve_with_transition`]
//!   and [`PhysicsEngine::set_position`].
//! - [`PhysicsBody`] and its integration, [`obj_update`] (committing a
//!   move), [`settle`], [`projectile`], [`InterpolationManager`],
//!   [`quantum`] (the per-object clock and activity gate),
//!   [`state_transition`], [`placement_room`] (room to set a body down
//!   ahead of a standing one).
//!
//! - [`movement`]: the motion interpreter (turning motion commands into
//!   velocity and animation), the move-to, sticky, constraint and target
//!   managers, the local player's movement controller and the remote
//!   object's motion driver, all driving the body through the pieces above.

mod body;
mod bsp;
mod bsp_collisions;
mod bsp_query;
#[cfg(test)]
mod bsp_tests;
mod cell_array;
mod cell_surface;
pub mod cell_transit;
pub mod collision_flags;
pub mod content;
pub mod dat;
mod engine;
pub mod frame_math;
mod geometry;
pub mod globals;
mod interpolation;
pub mod movement;
pub mod obj_update;
pub mod object_range;
pub mod placement_room;
pub mod polygon;
mod portal;
pub mod primitives;
pub mod projectile;
pub mod quantum;
pub mod scenery;
mod set_position;
pub mod settle;
mod shadow;
pub mod shadow_builder;
mod shadow_registry;
mod slot_map;
mod stack;
mod state;
pub mod state_transition;
mod terrain;
mod transition;
mod world;

#[cfg(test)]
mod tests;

pub use body::{CellPosition, PhysicsBody};
pub use bsp::{BspError, CellBsp, CellBspNode, PhysicsBsp, PhysicsBspNode};
pub use bsp_collisions::{BspFrame, BspTransition, LocalSpheres, WalkState};
pub use bsp_query::{BOX_PLANE_EPSILON, PlaneSide, PolyHit, WalkableHit, classify_box, which_side};
pub use cell_array::CellArray;
pub use cell_surface::{CellSurface, point_in_triangle_xy};
pub use collision_flags::EntityCollisionFlags;
pub use engine::{
    CellCollisionHook, LandblockPhysics, PhysicsEngine, PositionRandom, ResolveRequest,
    ResolveResult, TerrainWalkableSample, accept_no_slide_placement, is_walkable_contact,
};
pub use geometry::{Plane, Sphere};
pub use interpolation::{InterpolationManager, InterpolationRecoveryTarget};
pub use polygon::ResolvedPolygon;
pub use portal::{PortalInfo, PortalPlane};
pub use set_position::{
    PhysicsPlacementClass, PhysicsResidenceDisposition, PhysicsSetPositionCollisionReport,
    PhysicsSetPositionError, PhysicsSetPositionFlags, PhysicsSetPositionRequest,
    PhysicsSetPositionResult,
};
pub use shadow::{
    RetailCellArrayRoute, RetailPartEntry, ShadowCollisionType, ShadowEntry, ShadowPartBox,
    ShadowPartGeometry, ShadowShape,
};
pub use shadow_registry::{
    PhysicsShadowCommitAction, RegistrationRecord, RegistrationSite, ShadowObjectRegistry,
    SingleShape,
};
pub use state::{InsertType, ObjectInfoState, TransitionState};
pub use terrain::{TerrainSurface, TerrainSurfacePolygon};
pub use transition::{
    CollisionInfo, CollisionSphere, ContactPlane, HouseRestrictions, ObjectInfo,
    ObjectRestrictions, RestrictionObject, SpherePath, Transition, TransitionCellCollisionPhase,
    bsp_only_dispatch, get_placement_compass_offset, get_step_down_probe_plan,
};
pub use world::{
    BldPortalInfo, BuildingPhysics, CellPhysics, CollisionWorld, GfxObjPhysics, Placement,
    SetupPhysics, VisualBounds,
};

pub use ac_core::physics::{PhysicsState, TransientState};
pub use glam::{Quat, Vec3};
