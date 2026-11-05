//! Visual effects logic: particle emitters, physics scripts, and the rules
//! for what starts them. Pure logic, no GPU: every frame it hands the
//! renderer lists of particle quads and particle meshes to draw.
//!
//! - [`ParticleSystem`] simulates every live emitter (spawning, motion by
//!   particle type, size and opacity over life, the world-view gate).
//! - [`PhysicsScriptRunner`] runs physics scripts: per-object queues of
//!   timed hooks.
//! - [`ParticleHookSink`] turns particle hooks (create, blocking create,
//!   destroy, stop) into emitters attached to an object's root or part.
//! - [`EntityEffectController`] decides what plays for server messages
//!   (play script, play script by type and intensity, sound) and for the
//!   script-calling hooks, holding packets for objects not ready yet.
//! - [`SkyPesFrameController`] runs the sky objects' scripts (weather).
//! - [`VfxWorld`] puts them together; [`build_particle_frame`] produces
//!   the draw lists.

mod controller;
mod dats;
mod desc;
pub mod facing;
mod hook_sink;
mod instances;
mod particles;
mod poses;
mod profile;
mod random;
mod script;
mod script_table;
mod sky;
mod slot_map;
mod visibility;
mod world;

pub use controller::{
    EffectContext, EffectEvent, EffectOwnerQueries, EntityEffectController, LiveRootState,
};
pub use dats::VfxDats;
pub use desc::{
    DEFAULT_DEGRADE_DISTANCE, EmitterDesc, EmitterDescFailure, EmitterDescFailureKind,
    EmitterDescRegistry, EmitterFlags, EmitterKind, ParticleType, particle_degrade_distance,
};
pub use hook_sink::ParticleHookSink;
pub use instances::{
    BillboardInstance, MeshInstance, ParticleCamera, ParticleFrame, ParticleGeometryKind,
    ParticleGfxCache, ParticleGfxInfo, ParticleSubmission, ParticleSubmissionKind,
    build_particle_frame, classify_particle_geometry, particle_gfx_obj_id, particle_orientation,
    sort_submissions,
};
pub use particles::{
    EmitterHandle, Particle, ParticleEmitter, ParticleRenderPass, ParticleSystem,
    ParticleVisibilityPolicy, SpawnOptions, UnattachedEmitterCellScope,
};
pub use poses::{EffectPoseRegistry, EffectPoseSource, PoseError, root_matrix};
pub use profile::{EntityEffectProfile, NetworkEffectDescription};
pub use random::{ConstantRandom, DotNetRandom, RandomSource};
pub use script::{
    IMMEDIATE_CALL_PES_THRESHOLD_SECONDS, LoadedPhysicsScript, PhysicsScriptRunner, ScriptHookSink,
    ScriptLoadError, ScriptTimeError, load_physics_script, parse_physics_script,
};
pub use script_table::{
    PhysicsScriptTableResolver, TableLoadError, is_physics_script_did, is_physics_script_table_did,
    is_sound_table_did, select_script,
};
pub use sky::{SkyPesFrameController, SkyTargets, sky_owner_id};
pub use visibility::{
    EXTENDED_RANGE_MULTIPLIER, LandscapeVisibilityFrame, NotALandscapeCell,
    ParticleVisibilityController,
};
pub use world::{ActivationInfo, DuplicateStaticOwner, VfxEvent, VfxWorld};
