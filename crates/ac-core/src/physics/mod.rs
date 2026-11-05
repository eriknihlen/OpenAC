//! Physics rules.

mod frame_validation;
mod land_defs;
mod state_flags;
mod terrain_surface;
mod timestamps;

pub use frame_validation::{is_valid_frame, is_valid_position};
pub use land_defs::{
    BLOCK_LENGTH, CELL_LENGTH, LAND_LENGTH, adjust_to_outside, blockid_to_lcoord, get_block_offset,
    get_outside_lcoord, gid_to_lcoord, in_bounds, inbound_valid_cell_id, lcoord_to_gid,
};
pub use state_flags::{PhysicsState, TransientState};
pub use terrain_surface::{
    CELL_SIZE, CELLS_PER_SIDE, HEIGHTMAP_LEN, HEIGHTMAP_SIDE, TerrainSurface,
    TerrainSurfacePolygon, WaterType, compute_outdoor_cell_id, compute_outdoor_cell_low_id,
    sample_normal_z_from_heightmap, sample_z_from_heightmap,
};
pub use timestamps::{
    CreateDisposition, PhysicsTimestampGate, PhysicsTimestamps, PositionDisposition, is_newer,
};
