// The atmospheric rendering's shared shader pieces: the shadow block
// (OpenAC's directional_shadow_common.glsl), the atmospheric frame block
// and the foliage wind (OpenAC's atmospheric_common.glsl and
// foliage_wind.glsl). The receiver is shadow_receiver.wgsl.

struct ShadowBlock {
    // World to each cascade's map clip space.
    world_to_clip: array<mat4x4<f32>, 4>,
    split_far_meters: vec4<f32>,
    // Strength, softness, reach (metres), blend width (metres).
    control: vec4<f32>,
    // Constant, slope and normal bias, caster padding (metres).
    bias_meters: vec4<f32>,
    // Unused, cascade count, map size, flags (bit 0 on, bits 8..11 the
    // filter radius).
    texture_and_flags: vec4<u32>,
    // From the ground toward the light, and the source kind.
    light_direction_and_source: vec4<f32>,
};

struct AtmosphericFrame {
    // Sun uv, ray strength, elevation degrees.
    sun_screen: vec4<f32>,
    sun_color: vec4<f32>,
    // Width, height, 1 / width, 1 / height.
    viewport: vec4<f32>,
    // Kind, intensity, delta seconds, outdoor.
    weather: vec4<f32>,
    // Toward the sun, authored brightness.
    sun_direction: vec4<f32>,
    // Day group, its factor, shadow and shaft elevation factors.
    policy: vec4<f32>,
    inverse_view_projection: mat4x4<f32>,
    // Seconds, wind mean, wind gust, wind direction (radians).
    clock_wind: vec4<f32>,
    // Lean, branch, flutter, canopy height (metres).
    wind_amplitude: vec4<f32>,
};

fn acdream_foliage_displace(
    world_pos: vec3<f32>,
    instance_origin: vec3<f32>,
    batch_flags: u32,
    clock_wind: vec4<f32>,
    amp: vec4<f32>,
) -> vec3<f32> {
    if ((batch_flags & 0x6u) == 0u) {
        return world_pos;
    }
    let t = clock_wind.x;
    let mean = clock_wind.y;
    let gust = clock_wind.z;
    let dir_a = clock_wind.w;
    let dir = vec2<f32>(cos(dir_a), sin(dir_a));
    let perp = vec2<f32>(-dir.y, dir.x);
    let h = clamp((world_pos.z - instance_origin.z) / max(amp.w, 0.5), 0.0, 1.0);
    let k = h * h;
    let ph = dot(instance_origin.xy, vec2<f32>(0.137, 0.291));
    // Gusts: two slow waves beating against each other.
    let g = 0.5 + 0.5 * sin(0.05 * t + ph) + 0.25 * sin(0.13 * t + 1.7 * ph);
    let s = mean + gust * g;
    // The whole tree leans, by the square of the height.
    let lean = k * amp.x * s * (0.8 + 0.2 * sin(0.35 * t + ph));
    var d = dir * lean;
    if ((batch_flags & 0x2u) != 0u) {
        let branch = k * amp.y * s * sin(1.1 * t + ph + 2.0 * h);
        let relative_xy = world_pos.xy - instance_origin.xy;
        let vh = fract(sin(dot(relative_xy, vec2<f32>(12.9898, 78.233))) * 43758.5453);
        let flutter = h * amp.z * s * sin(6.0 * t + 7.0 * vh);
        d = d + dir * branch + perp * 0.35 * branch
            + vec2<f32>(cos(6.2832 * vh), sin(6.2832 * vh)) * flutter;
    }
    var p = world_pos;
    p = vec3<f32>(p.xy + d, p.z);
    // Bending shortens the height instead of stretching it.
    p.z = p.z - 0.5 * dot(d, d) / max(h * amp.w, 0.5);
    return p;
}
