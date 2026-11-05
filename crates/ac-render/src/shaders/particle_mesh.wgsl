// Particle models (a port of OpenAC's particle_mesh.vert/.frag): the
// model's triangles placed by the particle's matrix, each texel times the
// particle's colour. Not lit, not fogged; pixels under 0.02 alpha are
// discarded.

struct Globals {
    view_proj: mat4x4<f32>,
    camera_pos: vec4<f32>,
};

@group(0) @binding(0) var<uniform> globals: Globals;
@group(0) @binding(3) var<storage, read> clips: array<ClipRegion>;
@group(1) @binding(0) var surface_tex: texture_2d<f32>;
@group(1) @binding(1) var surface_sampler: sampler;

struct VertexIn {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) uv: vec2<f32>,
    @location(3) m0: vec4<f32>,
    @location(4) m1: vec4<f32>,
    @location(5) m2: vec4<f32>,
    @location(6) m3: vec4<f32>,
    @location(7) color: vec4<f32>,
};

struct VertexOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
};

@vertex
fn vs_main(in: VertexIn) -> VertexOut {
    let model = mat4x4<f32>(in.m0, in.m1, in.m2, in.m3);
    var out: VertexOut;
    out.clip = globals.view_proj * model * vec4<f32>(in.position, 1.0);
    out.uv = in.uv;
    out.color = in.color;
    return out;
}

@fragment
fn fs_main(in: VertexOut) -> @location(0) vec4<f32> {
    let color = textureSample(surface_tex, surface_sampler, in.uv) * in.color;
    if (color.a < 0.02) {
        discard;
    }
    return color;
}
