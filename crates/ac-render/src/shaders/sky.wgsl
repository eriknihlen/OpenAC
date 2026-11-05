// Sky objects (dome, clouds, sun, moons, stars, weather layers), drawn
// around the camera with depth testing off: a port of OpenAC's sky.vert and
// sky.frag without its enhanced night sky (not the game's).

struct SkyParams {
    model: mat4x4<f32>,
    // The sky projection times the view without its translation.
    view_proj: mat4x4<f32>,
    // xyz ambient colour, w the surface's self-lighting.
    ambient_emissive: vec4<f32>,
    // xyz sun colour, w the diffuse factor.
    sun_color_diffuse: vec4<f32>,
    // xyz unit vector toward the sun, w fade (0 visible, 1 gone).
    sun_dir_transparency: vec4<f32>,
    // xy texture scroll, z 1 when fogged, w the surface's opacity.
    scroll_fog_opacity: vec4<f32>,
};

struct Light {
    pos_and_kind: vec4<f32>,
    dir_and_range: vec4<f32>,
    color_and_intensity: vec4<f32>,
    cone_angle_etc: vec4<f32>,
};

struct SceneLighting {
    lights: array<Light, 8>,
    cell_ambient: vec4<f32>,
    // x fog start, y fog end, z lightning flash, w fog mode.
    fog_params: vec4<f32>,
    fog_color: vec4<f32>,
    camera_and_time: vec4<f32>,
};

struct Material {
    alpha_cutoff: f32,
    opacity: f32,
    _pad0: f32,
    _pad1: f32,
};

@group(0) @binding(0) var<uniform> sky: SkyParams;
@group(0) @binding(1) var<uniform> scene: SceneLighting;
@group(0) @binding(2) var<storage, read> clips: array<ClipRegion>;
@group(1) @binding(0) var surface_tex: texture_2d<f32>;
@group(1) @binding(1) var surface_sampler: sampler;
@group(1) @binding(2) var<uniform> material: Material;

struct VertexIn {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) uv: vec2<f32>,
};

struct VertexOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) tint: vec3<f32>,
    @location(2) fog_factor: f32,
};

@vertex
fn vs_main(in: VertexIn) -> VertexOut {
    var out: VertexOut;
    out.uv = in.uv + sky.scroll_fog_opacity.xy;
    let world = sky.model * vec4<f32>(in.position, 1.0);
    out.clip = sky.view_proj * world;
    let n = normalize((sky.model * vec4<f32>(in.normal, 0.0)).xyz);
    let diff = max(dot(n, sky.sun_dir_transparency.xyz), 0.0);
    let lit = vec3<f32>(sky.ambient_emissive.w) + sky.ambient_emissive.xyz
        + sky.sun_color_diffuse.xyz * sky.sun_color_diffuse.w * diff;
    out.tint = clamp(lit, vec3<f32>(0.0), vec3<f32>(1.0));
    let dist = length(world.xyz);
    let span = max(scene.fog_params.y - scene.fog_params.x, 1e-3);
    out.fog_factor = clamp((scene.fog_params.y - dist) / span, 0.0, 1.0);
    return out;
}

@fragment
fn fs_main(in: VertexOut) -> @location(0) vec4<f32> {
    let sampled = textureSample(surface_tex, surface_sampler, in.uv);
    var rgb = sampled.rgb * in.tint;
    if (sky.scroll_fog_opacity.z > 0.5 && i32(scene.fog_params.w) != 0) {
        rgb = mix(scene.fog_color.rgb, rgb, in.fog_factor);
    }
    let flash = scene.fog_params.z;
    rgb = rgb + flash * vec3<f32>(1.5, 1.5, 1.8);
    let cap = mix(1.0, 3.0, clamp(flash, 0.0, 1.0));
    rgb = min(rgb, vec3<f32>(cap));
    let a = sampled.a * (1.0 - sky.sun_dir_transparency.w) * sky.scroll_fog_opacity.w;
    // Drawn whole through a portal view, as OpenAC's sky.
    if (a < 0.01) {
        discard;
    }
    return vec4<f32>(rgb, a);
}
