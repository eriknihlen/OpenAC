#version 430 core

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 oColor;

#include "atmospheric_common.glsl"

float lowFusedMaskTexel(ivec2 coordinate, vec2 maskSize)
{
    ivec2 maximum = ivec2(maskSize) - ivec2(1);
    ivec2 clampedCoordinate = clamp(coordinate, ivec2(0), maximum);
    vec2 depthUv = (vec2(clampedCoordinate) + vec2(0.5)) / maskSize;
    float depth = ACDREAM_SAMPLE_2D(uTextureIndexA, depthUv).r;
    float unobstructedSky = smoothstep(0.9975, 0.99995, depth);
    float enabled = uAtmosphereSunScreen.z * uAtmosphereWeather.w;
    // Match the removed RGBA8_UNORM mask attachment before filtering it.
    return round(clamp(unobstructedSky * enabled, 0.0, 1.0) * 255.0) / 255.0;
}

float sampleLowFusedMask(vec2 uv)
{
    vec2 maskSize = uPackParams1.yz;
    vec2 texel = uv * maskSize - vec2(0.5);
    ivec2 lower = ivec2(floor(texel));
    vec2 fraction = fract(texel);
    float topLeft = lowFusedMaskTexel(lower, maskSize);
    float topRight = lowFusedMaskTexel(lower + ivec2(1, 0), maskSize);
    float bottomLeft = lowFusedMaskTexel(lower + ivec2(0, 1), maskSize);
    float bottomRight = lowFusedMaskTexel(lower + ivec2(1, 1), maskSize);
    return mix(
        mix(topLeft, topRight, fraction.x),
        mix(bottomLeft, bottomRight, fraction.x),
        fraction.y);
}

void main()
{
    const int SampleCount = 48;
    float decay = uPackParams0.x;
    float weight = uPackParams0.y;
    float density = uPackParams0.z;
    vec2 delta = (vUv - uAtmosphereSunScreen.xy) * (density / float(SampleCount));
    vec2 sampleUv = vUv;
    float illumination = 1.0;
    float sum = 0.0;
    for (int i = 0; i < SampleCount; ++i) {
        sampleUv -= delta;
        if (any(lessThan(sampleUv, vec2(0.0))) || any(greaterThan(sampleUv, vec2(1.0))))
            break;
        float mask = uPackParams1.x > 0.5
            ? sampleLowFusedMask(sampleUv)
            : ACDREAM_SAMPLE_2D(uTextureIndexA, sampleUv).r;
        sum += mask * illumination;
        illumination *= decay;
    }
    vec3 rays = uAtmosphereSunColor.rgb * (sum * weight / float(SampleCount));
    oColor = vec4(rays, 1.0);
}
