#version 430 core

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 oColor;

#include "atmospheric_common.glsl"

void main()
{
    vec3 scene = acdreamDecodeDisplay(ACDREAM_SAMPLE_2D(uTextureIndexA, vUv).rgb)
        + acdreamDecodeDisplay(ACDREAM_SAMPLE_2D(uTextureIndexB, vUv).rgb);
    if (uPackParams0.w > 0.5)
        scene += acdreamDecodeDisplay(ACDREAM_SAMPLE_2D(uTextureIndexC, vUv).rgb);
    float brightness = dot(scene, vec3(0.2126, 0.7152, 0.0722));
    float threshold = uPackParams0.y;
    float knee = max(uPackParams0.z, 0.0001);
    float soft = clamp((brightness - threshold + knee) / (2.0 * knee), 0.0, 1.0);
    soft = soft * soft;
    float contribution = max(brightness - threshold, 0.0) + soft * knee;
    contribution /= max(brightness, 0.0001);
    vec3 bloom = scene * contribution * uPackParams0.x;
    oColor = vec4(bloom, 1.0);
}
