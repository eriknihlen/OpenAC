// Portal polygons drawn into depth only: a doorway's far-depth punch (so a
// building's interior can be drawn into the opening), an exit portal's
// seal at its own depth, and the full-screen depth clear between an
// interior frame's landscape and its cells. Colour is not written.

struct Globals {
    view_proj: mat4x4<f32>,
    camera_pos: vec4<f32>,
};

@group(0) @binding(0) var<uniform> globals: Globals;
@group(0) @binding(3) var<storage, read> clips: array<ClipRegion>;

struct VertexIn {
    @location(0) position: vec3<f32>,
    @location(1) slot: u32,
    // Depth to force (0..1), or negative for the polygon's own.
    @location(2) depth: f32,
    // 1 when the position is already in clip space.
    @location(3) screen: u32,
};

struct VertexOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) @interpolate(flat) slot: u32,
};

@vertex
fn vs_main(in: VertexIn) -> VertexOut {
    var c = vec4<f32>(in.position.xy, 0.0, 1.0);
    if (in.screen == 0u) {
        c = globals.view_proj * vec4<f32>(in.position, 1.0);
    }
    if (in.depth >= 0.0) {
        c.z = in.depth * c.w;
    }
    var out: VertexOut;
    out.clip = c;
    out.slot = in.slot;
    return out;
}

@fragment
fn fs_main(in: VertexOut) -> @location(0) vec4<f32> {
    if (outside_clip(in.slot, in.clip.xy)) {
        discard;
    }
    return vec4<f32>(0.0);
}
