// Particle quads (a port of OpenAC's particle.vert/.frag): a unit quad
// from -0.5 to 0.5 scaled by the particle's two spans around its centre,
// texture coordinates 0..1 along the spans. The colour is the texel times
// the particle's colour; without a texture a soft round sprite stands in.
// Not lit, not fogged; pixels under 0.02 alpha are discarded.

struct Globals {
    view_proj: mat4x4<f32>,
    camera_pos: vec4<f32>,
};

@group(0) @binding(0) var<uniform> globals: Globals;
@group(0) @binding(3) var<storage, read> clips: array<ClipRegion>;
@group(1) @binding(0) var particle_tex: texture_2d<f32>;
@group(1) @binding(1) var particle_sampler: sampler;

struct VertexIn {
    @builtin(vertex_index) vertex: u32,
    // xyz centre; w 1 with a texture, 0 for the soft sprite.
    @location(0) center: vec4<f32>,
    @location(1) axis_x: vec4<f32>,
    @location(2) axis_y: vec4<f32>,
    @location(3) color: vec4<f32>,
};

struct VertexOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) @interpolate(flat) textured: f32,
};

@vertex
fn vs_main(in: VertexIn) -> VertexOut {
    // Two triangles: corners 0 1 2 and 0 2 3 of the unit quad.
    var corners = array<vec2<f32>, 6>(
        vec2<f32>(-0.5, -0.5),
        vec2<f32>(0.5, -0.5),
        vec2<f32>(0.5, 0.5),
        vec2<f32>(-0.5, -0.5),
        vec2<f32>(0.5, 0.5),
        vec2<f32>(-0.5, 0.5),
    );
    let q = corners[in.vertex % 6u];
    let world = in.center.xyz + in.axis_x.xyz * q.x + in.axis_y.xyz * q.y;
    var out: VertexOut;
    out.clip = globals.view_proj * vec4<f32>(world, 1.0);
    out.uv = q + vec2<f32>(0.5, 0.5);
    out.color = in.color;
    out.textured = in.center.w;
    return out;
}

@fragment
fn fs_main(in: VertexOut) -> @location(0) vec4<f32> {
    // Sampled in uniform control flow; the soft sprite is chosen after.
    let sampled = textureSample(particle_tex, particle_sampler, in.uv);
    // smoothstep(1.0, 0.4, r), written out (the edges are reversed).
    let r = length(in.uv - vec2<f32>(0.5, 0.5)) * 2.0;
    let t = clamp((r - 1.0) / (0.4 - 1.0), 0.0, 1.0);
    let soft = vec4<f32>(1.0, 1.0, 1.0, t * t * (3.0 - 2.0 * t));
    let texel = select(soft, sampled, in.textured > 0.5);
    let color = texel * in.color;
    if (color.a < 0.02) {
        discard;
    }
    return color;
}
