#version 430 core

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 oColor;

#include "atmospheric_common.glsl"

void main()
{
    float depth = ACDREAM_SAMPLE_2D(uTextureIndexA, vUv).r;
    float unobstructedSky = smoothstep(0.9975, 0.99995, depth);
    float enabled = uAtmosphereSunScreen.z * uAtmosphereWeather.w;
    oColor = vec4(vec3(unobstructedSky * enabled), 1.0);
}
