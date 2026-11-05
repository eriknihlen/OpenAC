//! Outdoor terrain rules: how a landblock's 9 x 9 height and terrain-type
//! grid becomes triangles, and which textures blend on each cell. Pure
//! data in, pure data out; the renderer uploads the result.

mod blending;
mod mesh;
mod slot_allocator;
mod split;
mod tiling;

pub use blending::{
    AlphaMap, Overlay, RoadCodes, RoadOverlay, SurfaceInfo, TerrainBlendingContext, build_surface,
    extract_terrain_codes, fill_cell_data, find_road_alpha, find_terrain_alpha, get_pal_code,
    get_road_codes, pseudo_random_index, rotate_terrain_code,
};
pub use mesh::{
    LANDBLOCK_SIZE, LandblockMeshData, LandblockTerrain, TerrainVertex, VERTICES_PER_CELL,
    VERTICES_PER_LANDBLOCK, build_landblock_mesh,
};
pub use slot_allocator::{Allocation, SlotError, TerrainSlotAllocator};
pub use split::{CellSplitDirection, calculate_split_direction};
pub use tiling::{
    TERRAIN_LAYER_CAPACITY, TILING_UNIFORM_BUFFER_BYTES, TILING_UNIFORM_ELEMENT_STRIDE_BYTES,
    TilingLayerOutOfRange, build_tiling_table,
};
