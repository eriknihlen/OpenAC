#version 430 core

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 oColor;

#include "atmospheric_common.glsl"
#include "directional_shadow_common.glsl"

vec3 reconstructWorld(vec2 uv, float depth)
{
    vec4 clip = vec4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    vec4 world = uAtmosphereInverseViewProjection * clip;
    return world.xyz / max(abs(world.w), 1e-6);
}

float directionalVisibility(vec3 worldPosition)
{
    // Volumetric shafts are a sun effect. A moon-oriented shadow map remains
    // valid for world receivers but must not occlude the authored sun ray.
    if (uint(round(uShadowLightDirectionAndSource.w)) != 1u)
        return 1.0;
    uint cascadeCount = clamp(uShadowTextureAndFlags.y, 1u, 4u);
    vec3 surfaceToSun = normalize(uShadowLightDirectionAndSource.xyz);
    vec3 biasedPosition = worldPosition
        + surfaceToSun * max(uShadowBiasMeters.x, 0.0);
    for (uint cascade = 0u; cascade < cascadeCount; ++cascade) {
        vec4 clip = uShadowWorldToClip[cascade] * vec4(biasedPosition, 1.0);
        vec3 ndc = clip.xyz / max(abs(clip.w), 1e-6);
        vec2 uv = vec2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
        if (all(greaterThanEqual(uv, vec2(0.0)))
            && all(lessThanEqual(uv, vec2(1.0)))
            && ndc.z >= 0.0 && ndc.z <= 1.0) {
            float stored = ACDREAM_SAMPLE_ARRAY(
                uShadowTextureAndFlags.x,
                vec3(uv, float(cascade))).r;
            float visible = ndc.z <= stored ? 1.0 : 0.0;
            return mix(1.0, visible, clamp(uShadowControl.x, 0.0, 1.0));
        }
    }
    return 1.0;
}

void main()
{
    float sceneDepth = ACDREAM_SAMPLE_2D(uTextureIndexA, vUv).r;
    if (sceneDepth >= 0.999999 || uPackParams0.w <= 0.0) {
        oColor = vec4(0.0);
        return;
    }

    vec3 nearWorld = reconstructWorld(vUv, 0.0);
    vec3 sceneWorld = reconstructWorld(vUv, sceneDepth);
    int steps = clamp(int(uPackParams0.z + 0.5), 1, 64);
    float lit = 0.0;
    float ign = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
    for (int step = 0; step < 64; ++step) {
        if (step >= steps)
            break;
        float t = (float(step) + ign) / float(steps);
        lit += directionalVisibility(mix(nearWorld, sceneWorld, t));
    }

    float integrated = lit / float(steps);
    float extinction = 1.0 - exp(-uPackParams0.x * length(sceneWorld - nearWorld));
    vec3 color = uAtmosphereSunColor.rgb
        * (integrated * extinction * uPackParams0.y);
    oColor = vec4(max(color, vec3(0.0)), 1.0);
}
