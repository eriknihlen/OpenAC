//! Lights: the sun and ambient from the sky, model lights (torches, lamps),
//! the player's fill light, the per-frame choice of lights, the static
//! light bake for cell walls, and the GPU-ready layouts.

mod bake;
mod hook_sink;
mod info;
mod manager;
mod pack;
mod source;

pub use bake::{compute_vertex_color, point_contribution};
pub use hook_sink::{EntityPoseSource, LightingHookSink};
pub use info::{
    DYNAMIC_LIGHT_RANGE_FACTOR, LightInfoSource, LightOwnerPlacement, STATIC_LIGHT_RANGE_FACTOR,
    load_lights,
};
pub use manager::{
    INDOOR_AMBIENT_LEVEL, LightId, LightManager, MAX_ACTIVE_LIGHTS, MAX_DYNAMIC_POINT_LIGHTS,
    MAX_GLOBAL_LIGHTS, MAX_LIGHTS_PER_ENV_CELL, MAX_LIGHTS_PER_OBJECT, MAX_STATIC_POINT_LIGHTS,
    MissingRankingOrigin, VIEWER_LIGHT_FALLOFF, VIEWER_LIGHT_INTENSITY, VIEWER_LIGHT_OWNER_ID,
    select_for_cell, select_for_object,
};
pub use pack::{FLOATS_PER_LIGHT, SceneLightingUbo, UboLight, pack_global_lights};
pub use source::{CellAmbientState, LightKind, LightSource};
