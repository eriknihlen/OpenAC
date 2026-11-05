// The atmospheric post passes (a port of OpenAC's atmospheric_sun_occlusion,
// atmospheric_sun_rays, atmospheric_volumetric, atmospheric_bloom_downsample,
// atmospheric_bloom_blur and atmospheric_filmic shaders): one full-screen
// triangle each. Texture coordinates run 0..1 left to right and top to
// bottom. The world target holds the game's display values; the passes
// decode them to light (gamma 2.2), add bloom, sun rays and light shafts,
// tone-map, and encode back.

struct PassParams {
    p0: vec4<f32>,
    p1: vec4<f32>,
    p2: vec4<f32>,
    p3: vec4<f32>,
};

@group(0) @binding(0) var<uniform> frame: AtmosphericFrame;
@group(0) @binding(1) var<uniform> pass_params: PassParams;
@group(0) @binding(2) var linear_sampler: sampler;

@group(1) @binding(0) var texture_a: texture_2d<f32>;
@group(1) @binding(1) var texture_b: texture_2d<f32>;
@group(1) @binding(2) var texture_c: texture_2d<f32>;
@group(1) @binding(3) var texture_d: texture_2d<f32>;

// The shafts' shadow maps.
@group(2) @binding(0) var<uniform> shadow: ShadowBlock;
@group(2) @binding(1) var shadow_map: texture_depth_2d_array;
@group(2) @binding(2) var shadow_sampler: sampler;

struct Varyings {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_fullscreen(@builtin(vertex_index) id: u32) -> Varyings {
    let corner = vec2<f32>(f32((id << 1u) & 2u), f32(id & 2u));
    let p = corner * 2.0 - 1.0;
    var out: Varyings;
    out.clip = vec4<f32>(p, 0.0, 1.0);
    out.uv = vec2<f32>(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5);
    return out;
}

fn decode_display(c: vec3<f32>) -> vec3<f32> {
    return pow(max(c, vec3<f32>(0.0)), vec3<f32>(2.2));
}

fn encode_display(c: vec3<f32>) -> vec3<f32> {
    return pow(max(c, vec3<f32>(0.0)), vec3<f32>(1.0 / 2.2));
}

// texture_a holds the scene depth (one float per pixel, not filterable):
// read like a linear sampler would, from its four nearest texels.
fn depth_bilinear(uv: vec2<f32>) -> f32 {
    let size = vec2<i32>(textureDimensions(texture_a));
    let texel = uv * vec2<f32>(size) - vec2<f32>(0.5);
    let base = vec2<i32>(floor(texel));
    let f = fract(texel);
    let hi = size - vec2<i32>(1);
    let t00 = textureLoad(texture_a, clamp(base, vec2<i32>(0), hi), 0).r;
    let t10 = textureLoad(texture_a, clamp(base + vec2<i32>(1, 0), vec2<i32>(0), hi), 0).r;
    let t01 = textureLoad(texture_a, clamp(base + vec2<i32>(0, 1), vec2<i32>(0), hi), 0).r;
    let t11 = textureLoad(texture_a, clamp(base + vec2<i32>(1, 1), vec2<i32>(0), hi), 0).r;
    return mix(mix(t00, t10, f.x), mix(t01, t11, f.x), f.y);
}

// Where the sky shows (depth at the far plane), times the frame's ray
// strength and outdoor flag.
@fragment
fn fs_sun_occlusion(in: Varyings) -> @location(0) vec4<f32> {
    let depth = depth_bilinear(in.uv);
    let unobstructed_sky = smoothstep(0.9975, 0.99995, depth);
    let enabled = frame.sun_screen.z * frame.weather.w;
    return vec4<f32>(vec3<f32>(unobstructed_sky * enabled), 1.0);
}

// A radial blur of the sky mask toward the sun.
@fragment
fn fs_sun_rays(in: Varyings) -> @location(0) vec4<f32> {
    let sample_count = 48;
    let decay = pass_params.p0.x;
    let weight = pass_params.p0.y;
    let density = pass_params.p0.z;
    let delta = (in.uv - frame.sun_screen.xy) * (density / f32(sample_count));
    var sample_uv = in.uv;
    var illumination = 1.0;
    var sum = 0.0;
    for (var i = 0; i < sample_count; i = i + 1) {
        sample_uv = sample_uv - delta;
        if (any(sample_uv < vec2<f32>(0.0)) || any(sample_uv > vec2<f32>(1.0))) {
            break;
        }
        let mask = textureSampleLevel(texture_a, linear_sampler, sample_uv, 0.0).r;
        sum = sum + mask * illumination;
        illumination = illumination * decay;
    }
    let rays = frame.sun_color.rgb * (sum * weight / f32(sample_count));
    return vec4<f32>(rays, 1.0);
}

fn reconstruct_world(uv: vec2<f32>, depth: f32) -> vec3<f32> {
    let clip = vec4<f32>(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    let world = frame.inverse_view_projection * clip;
    return world.xyz / max(abs(world.w), 1e-6);
}

// Whether the sun reaches a point of the air (moon maps never block the
// sun's shafts).
fn directional_visibility(world_position: vec3<f32>) -> f32 {
    if (u32(round(shadow.light_direction_and_source.w)) != 1u) {
        return 1.0;
    }
    let cascade_count = clamp(shadow.texture_and_flags.y, 1u, 4u);
    let surface_to_sun = normalize(shadow.light_direction_and_source.xyz);
    let biased = world_position + surface_to_sun * max(shadow.bias_meters.x, 0.0);
    for (var cascade = 0u; cascade < 4u; cascade = cascade + 1u) {
        if (cascade >= cascade_count) {
            break;
        }
        let clip = shadow.world_to_clip[cascade] * vec4<f32>(biased, 1.0);
        let ndc = clip.xyz / max(abs(clip.w), 1e-6);
        let uv = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
        if (all(uv >= vec2<f32>(0.0)) && all(uv <= vec2<f32>(1.0)) && ndc.z >= 0.0 && ndc.z <= 1.0) {
            let stored = textureSampleLevel(shadow_map, shadow_sampler, uv, i32(cascade), 0);
            let visible = select(0.0, 1.0, ndc.z <= stored);
            return mix(1.0, visible, clamp(shadow.control.x, 0.0, 1.0));
        }
    }
    return 1.0;
}

// The light shafts: the share of each view ray's steps the sun reaches,
// lit by the sun's colour, thickening with the ray's length.
@fragment
fn fs_volumetric(in: Varyings) -> @location(0) vec4<f32> {
    let scene_depth = depth_bilinear(in.uv);
    if (scene_depth >= 0.999999 || pass_params.p0.w <= 0.0) {
        return vec4<f32>(0.0);
    }
    let near_world = reconstruct_world(in.uv, 0.0);
    let scene_world = reconstruct_world(in.uv, scene_depth);
    let steps = clamp(i32(pass_params.p0.z + 0.5), 1, 64);
    var lit = 0.0;
    // Interleaved gradient noise spreads the steps between pixels.
    let ign = fract(52.9829189 * fract(dot(in.clip.xy, vec2<f32>(0.06711056, 0.00583715))));
    for (var step = 0; step < 64; step = step + 1) {
        if (step >= steps) {
            break;
        }
        let t = (f32(step) + ign) / f32(steps);
        lit = lit + directional_visibility(mix(near_world, scene_world, t));
    }
    let integrated = lit / f32(steps);
    let extinction = 1.0 - exp(-pass_params.p0.x * length(scene_world - near_world));
    let color = frame.sun_color.rgb * (integrated * extinction * pass_params.p0.y);
    return vec4<f32>(max(color, vec3<f32>(0.0)), 1.0);
}

// The bright part of the scene, rays and shafts (with a soft knee), in
// light values, at the bloom's size.
@fragment
fn fs_bloom_downsample(in: Varyings) -> @location(0) vec4<f32> {
    var scene = decode_display(textureSampleLevel(texture_a, linear_sampler, in.uv, 0.0).rgb)
        + decode_display(textureSampleLevel(texture_b, linear_sampler, in.uv, 0.0).rgb);
    if (pass_params.p0.w > 0.5) {
        scene = scene + decode_display(textureSampleLevel(texture_c, linear_sampler, in.uv, 0.0).rgb);
    }
    let brightness = dot(scene, vec3<f32>(0.2126, 0.7152, 0.0722));
    let threshold = pass_params.p0.y;
    let knee = max(pass_params.p0.z, 0.0001);
    var soft = clamp((brightness - threshold + knee) / (2.0 * knee), 0.0, 1.0);
    soft = soft * soft;
    var contribution = max(brightness - threshold, 0.0) + soft * knee;
    contribution = contribution / max(brightness, 0.0001);
    return vec4<f32>(scene * contribution * pass_params.p0.x, 1.0);
}

// One direction of a nine-tap Gaussian (five linear samples).
@fragment
fn fs_bloom_blur(in: Varyings) -> @location(0) vec4<f32> {
    let step_uv = pass_params.p0.xy;
    var value = textureSampleLevel(texture_a, linear_sampler, in.uv, 0.0).rgb * 0.227027;
    value = value + textureSampleLevel(texture_a, linear_sampler, in.uv + step_uv * 1.384615, 0.0).rgb * 0.316216;
    value = value + textureSampleLevel(texture_a, linear_sampler, in.uv - step_uv * 1.384615, 0.0).rgb * 0.316216;
    value = value + textureSampleLevel(texture_a, linear_sampler, in.uv + step_uv * 3.230769, 0.0).rgb * 0.070270;
    value = value + textureSampleLevel(texture_a, linear_sampler, in.uv - step_uv * 3.230769, 0.0).rgb * 0.070270;
    return vec4<f32>(value, 1.0);
}

fn aces_fitted(v: vec3<f32>) -> vec3<f32> {
    let a = 2.51;
    let b = 0.03;
    let c = 2.43;
    let d = 0.59;
    let e = 0.14;
    return clamp((v * (a * v + b)) / (v * (c * v + d) + e), vec3<f32>(0.0), vec3<f32>(1.0));
}

// The composite into the window: world, bloom, rays and shafts in light
// values, exposure, the filmic curve, grade, vignette, display values.
@fragment
fn fs_filmic(in: Varyings) -> @location(0) vec4<f32> {
    var hdr = decode_display(textureSampleLevel(texture_a, linear_sampler, in.uv, 0.0).rgb)
        + textureSampleLevel(texture_b, linear_sampler, in.uv, 0.0).rgb
        + decode_display(textureSampleLevel(texture_c, linear_sampler, in.uv, 0.0).rgb);
    if (pass_params.p1.y > 0.5) {
        hdr = hdr + decode_display(textureSampleLevel(texture_d, linear_sampler, in.uv, 0.0).rgb);
    }
    let exposed = max(hdr * pass_params.p0.x, vec3<f32>(0.0));
    let linear_clamped = clamp(exposed, vec3<f32>(0.0), vec3<f32>(1.0));
    var color = mix(linear_clamped, aces_fitted(exposed), clamp(pass_params.p1.x, 0.0, 1.0));
    let luminance = dot(color, vec3<f32>(0.2126, 0.7152, 0.0722));
    color = mix(vec3<f32>(luminance), color, pass_params.p0.y);
    let linear_mid_grey = 0.18;
    color = (color - linear_mid_grey) * pass_params.p0.z + linear_mid_grey;
    let centered = in.uv * 2.0 - 1.0;
    let vignette = smoothstep(1.25, 0.25, dot(centered, centered));
    color = color * mix(1.0, vignette, clamp(pass_params.p0.w, 0.0, 1.0));
    return vec4<f32>(encode_display(clamp(color, vec3<f32>(0.0), vec3<f32>(1.0))), 1.0);
}
