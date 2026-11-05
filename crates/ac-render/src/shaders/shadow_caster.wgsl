// The shadow maps' casters (a port of OpenAC's directional_shadow_terrain,
// directional_shadow_world_opaque and directional_shadow_world_cutout
// shaders): depth only, seen from the light through one cascade's box.
// Terrain and solid sub-meshes write depth; clip-mapped ones (leaves,
// fences) discard where their texture is clear. Procedural scenery sways
// as it does in the world.

struct Cascade {
    world_to_clip: mat4x4<f32>,
    // Seconds, wind mean, wind gust, wind direction (radians).
    clock_wind: vec4<f32>,
    // Lean, branch, flutter, canopy height (metres).
    wind_amplitude: vec4<f32>,
};

@group(0) @binding(0) var<uniform> cascade: Cascade;

@group(1) @binding(0) var surface_tex: texture_2d<f32>;
@group(1) @binding(1) var surface_sampler: sampler;

struct TerrainIn {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) packed0: vec4<u32>,
    @location(3) packed1: vec4<u32>,
    @location(4) packed2: vec4<u32>,
    @location(5) packed3: vec4<u32>,
    @location(6) block_offset: vec3<f32>,
};

@vertex
fn vs_terrain(in: TerrainIn) -> @builtin(position) vec4<f32> {
    return cascade.world_to_clip * vec4<f32>(in.position + in.block_offset, 1.0);
}

struct MeshIn {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) uv: vec2<f32>,
    @location(3) m0: vec4<f32>,
    @location(4) m1: vec4<f32>,
    @location(5) m2: vec4<f32>,
    @location(6) m3: vec4<f32>,
    @location(7) lights0: vec4<i32>,
    @location(8) lights1: vec4<i32>,
    // Bit 1: procedural scenery with leaves.
    @location(9) indoor: u32,
    @location(11) opacity: f32,
    @location(12) detail: u32,
    @location(13) selection: vec2<f32>,
};

struct MeshOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

// 2 for the leaves pipeline, 4 for the solid one.
override WIND_KIND: u32 = 0u;

@vertex
fn vs_mesh(in: MeshIn) -> MeshOut {
    let model = mat4x4<f32>(in.m0, in.m1, in.m2, in.m3);
    let world = model * vec4<f32>(in.position, 1.0);
    let flags = select(0u, WIND_KIND, (in.indoor & 2u) != 0u);
    let moved = acdream_foliage_displace(
        world.xyz,
        in.m3.xyz,
        flags,
        cascade.clock_wind,
        cascade.wind_amplitude,
    );
    var out: MeshOut;
    out.clip = cascade.world_to_clip * vec4<f32>(moved, world.w);
    out.uv = in.uv;
    return out;
}

@fragment
fn fs_cutout(in: MeshOut) {
    let texel = textureSample(surface_tex, surface_sampler, in.uv);
    if (texel.a < 0.05) {
        discard;
    }
}
