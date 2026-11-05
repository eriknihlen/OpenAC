// The shadow receiver (a port of OpenAC's directional_shadow_receiver.glsl).
// Needs `shadow` (ShadowBlock), `shadow_map` (texture_depth_2d_array) and
// `shadow_sampler` (sampler) declared by the including shader.

fn acdream_shadow_cascade(distance: f32) -> i32 {
    let count = i32(shadow.texture_and_flags.y);
    for (var i = 0; i < 4; i = i + 1) {
        if (i >= count) {
            break;
        }
        if (distance <= shadow.split_far_meters[i]) {
            return i;
        }
    }
    return -1;
}

fn acdream_shadow_bias_scale(cascade: i32) -> f32 {
    let far_cascade = max(i32(shadow.texture_and_flags.y) - 1, 0);
    let m = shadow.world_to_clip[cascade];
    let f = shadow.world_to_clip[far_cascade];
    let cascade_density = 0.5 * (length(vec3<f32>(m[0][0], m[1][0], m[2][0]))
        + length(vec3<f32>(m[0][1], m[1][1], m[2][1])));
    let far_density = 0.5 * (length(vec3<f32>(f[0][0], f[1][0], f[2][0]))
        + length(vec3<f32>(f[0][1], f[1][1], f[2][1])));
    return clamp(far_density / max(cascade_density, 1e-7), 0.0, 1.0);
}

// The four map texels around `uv` compared with the receiver's depth and
// blended by position: a lit share between 0 and 1.
fn acdream_shadow_bilinear_compare(cascade: i32, uv: vec2<f32>, receiver_depth: f32) -> f32 {
    let resolution = max(f32(shadow.texture_and_flags.z), 1.0);
    let texel_position = uv * resolution - vec2<f32>(0.5);
    let blend = fract(texel_position);
    let gathered = textureGather(shadow_map, shadow_sampler, uv, cascade);
    let compared = step(vec4<f32>(receiver_depth), gathered);
    let lower = mix(compared.w, compared.z, blend.x);
    let upper = mix(compared.x, compared.y, blend.x);
    return mix(lower, upper, blend.y);
}

fn acdream_shadow_cascade_pcf(
    cascade: i32,
    receiver_world: vec3<f32>,
    world_normal: vec3<f32>,
    surface_to_light: vec3<f32>,
) -> f32 {
    let ndl = clamp(dot(world_normal, surface_to_light), 0.0, 1.0);
    let cascade_bias = max(shadow.bias_meters.xyz * acdream_shadow_bias_scale(cascade), vec3<f32>(0.001));
    let depth_bias = cascade_bias.x + cascade_bias.y * (1.0 - ndl);
    let biased = receiver_world + world_normal * cascade_bias.z + surface_to_light * depth_bias;
    let clip = shadow.world_to_clip[cascade] * vec4<f32>(biased, 1.0);
    let ndc = clip.xyz / max(abs(clip.w), 1e-7);
    let uv = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    let receiver_depth = ndc.z;
    if (uv.x <= 0.0 || uv.x >= 1.0 || uv.y <= 0.0 || uv.y >= 1.0
        || receiver_depth <= 0.0 || receiver_depth >= 1.0) {
        return 1.0;
    }
    let radius = clamp(i32((shadow.texture_and_flags.w >> 8u) & 0xFu), 0, 2);
    let texel = 1.0 / max(f32(shadow.texture_and_flags.z), 1.0);
    let softness = max(shadow.control.y, 1.0);
    if (radius == 0) {
        return acdream_shadow_bilinear_compare(cascade, uv, receiver_depth);
    }
    var lit = 0.0;
    var weight_sum = 0.0;
    if (radius == 1) {
        for (var y = 0; y < 2; y = y + 1) {
            for (var x = 0; x < 2; x = x + 1) {
                let offset = (vec2<f32>(f32(x), f32(y)) - vec2<f32>(0.5)) * softness * texel;
                lit = lit + acdream_shadow_bilinear_compare(cascade, uv + offset, receiver_depth);
                weight_sum = weight_sum + 1.0;
            }
        }
    } else {
        for (var y = -1; y <= 1; y = y + 1) {
            for (var x = -1; x <= 1; x = x + 1) {
                let weight = f32(2 - abs(x)) * f32(2 - abs(y));
                let offset = vec2<f32>(f32(x), f32(y)) * 1.5 * softness * texel;
                lit = lit + acdream_shadow_bilinear_compare(cascade, uv + offset, receiver_depth) * weight;
                weight_sum = weight_sum + weight;
            }
        }
    }
    return lit / max(weight_sum, 1.0);
}

// How much of the light reaches a receiver: 1 lit, 0 shadowed, faded by
// the strength and toward the reach.
fn acdream_directional_shadow_visibility(
    receiver_world: vec3<f32>,
    world_normal: vec3<f32>,
    camera_world: vec3<f32>,
    surface_to_light: vec3<f32>,
) -> f32 {
    if ((shadow.texture_and_flags.w & 1u) == 0u) {
        return 1.0;
    }
    let distance = length(receiver_world - camera_world);
    if (distance > shadow.control.z) {
        return 1.0;
    }
    let cascade = acdream_shadow_cascade(distance);
    if (cascade < 0) {
        return 1.0;
    }
    var visibility = acdream_shadow_cascade_pcf(cascade, receiver_world, world_normal, surface_to_light);
    let count = i32(shadow.texture_and_flags.y);
    if (cascade + 1 < count) {
        let split = shadow.split_far_meters[cascade];
        let width = max(shadow.control.w, 1e-4);
        let blend = smoothstep(max(0.0, split - width), split, distance);
        if (blend > 0.0) {
            let next = acdream_shadow_cascade_pcf(cascade + 1, receiver_world, world_normal, surface_to_light);
            visibility = mix(visibility, next, blend);
        }
    }
    let reach_fade = 1.0 - smoothstep(
        max(0.0, shadow.control.z - max(shadow.control.w, 1e-4)),
        shadow.control.z,
        distance,
    );
    let weight = clamp(shadow.control.x, 0.0, 1.0) * reach_fade;
    return mix(1.0, visibility, weight);
}
