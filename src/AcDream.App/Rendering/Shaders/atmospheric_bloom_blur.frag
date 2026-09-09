#version 430 core

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 oColor;

#include "atmospheric_common.glsl"

void main()
{
    vec2 stepUv = uPackParams0.xy;
    vec3 value = ACDREAM_SAMPLE_2D(uTextureIndexA, vUv).rgb * 0.227027;
    value += ACDREAM_SAMPLE_2D(uTextureIndexA, vUv + stepUv * 1.384615).rgb * 0.316216;
    value += ACDREAM_SAMPLE_2D(uTextureIndexA, vUv - stepUv * 1.384615).rgb * 0.316216;
    value += ACDREAM_SAMPLE_2D(uTextureIndexA, vUv + stepUv * 3.230769).rgb * 0.070270;
    value += ACDREAM_SAMPLE_2D(uTextureIndexA, vUv - stepUv * 3.230769).rgb * 0.070270;
    oColor = vec4(value, 1.0);
}
