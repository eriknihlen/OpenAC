#version 430 core

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 oColor;

#include "atmospheric_common.glsl"

vec3 acesFitted(vec3 value)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((value * (a * value + b)) / (value * (c * value + d) + e), 0.0, 1.0);
}

vec3 sampleBloom(vec2 uv)
{
    return ACDREAM_SAMPLE_2D(uTextureIndexB, uv).rgb;
}

vec3 lowFusedScene(vec2 uv)
{
    vec3 scene = acdreamDecodeDisplay(ACDREAM_SAMPLE_2D(uTextureIndexA, uv).rgb)
        + acdreamDecodeDisplay(ACDREAM_SAMPLE_2D(uTextureIndexB, uv).rgb);
    if (uPackParams2.w > 0.5)
        scene += acdreamDecodeDisplay(ACDREAM_SAMPLE_2D(uTextureIndexC, uv).rgb);
    return scene;
}

vec3 lowFusedBloomExtract(vec3 scene)
{
    float brightness = dot(scene, vec3(0.2126, 0.7152, 0.0722));
    float threshold = uPackParams2.y;
    float knee = max(uPackParams2.z, 0.0001);
    float soft = clamp((brightness - threshold + knee) / (2.0 * knee), 0.0, 1.0);
    soft = soft * soft;
    float contribution = max(brightness - threshold, 0.0) + soft * knee;
    contribution /= max(brightness, 0.0001);
    return scene * contribution * uPackParams2.x;
}

vec3 lowFusedBloom(vec3 centerScene)
{
    const float offsets[5] = float[5](
        -3.230769, -1.384615, 0.0, 1.384615, 3.230769);
    const float weights[5] = float[5](
        0.070270, 0.316216, 0.227027, 0.316216, 0.070270);
    vec3 bloom = vec3(0.0);
    for (int y = 0; y < 5; ++y) {
        for (int x = 0; x < 5; ++x) {
            vec3 scene = x == 2 && y == 2
                ? centerScene
                : lowFusedScene(vUv + vec2(offsets[x], offsets[y]) * uPackParams3.xy);
            bloom += lowFusedBloomExtract(scene) * (weights[x] * weights[y]);
        }
    }
    return bloom;
}

void main()
{
    vec3 hdr;
    if (uPackParams1.z > 0.5) {
        vec3 scene = lowFusedScene(vUv);
        hdr = scene + lowFusedBloom(scene);
    }
    else {
        hdr = acdreamDecodeDisplay(ACDREAM_SAMPLE_2D(uTextureIndexA, vUv).rgb)
            + sampleBloom(vUv)
            + acdreamDecodeDisplay(ACDREAM_SAMPLE_2D(uTextureIndexC, vUv).rgb);
        if (uPackParams1.y > 0.5)
            hdr += acdreamDecodeDisplay(ACDREAM_SAMPLE_2D(uTextureIndexD, vUv).rgb);
    }
    vec3 exposed = max(hdr * uPackParams0.x, vec3(0.0));
    vec3 linearClamped = clamp(exposed, 0.0, 1.0);
    vec3 color = mix(linearClamped, acesFitted(exposed), clamp(uPackParams1.x, 0.0, 1.0));

    float luminance = dot(color, vec3(0.2126, 0.7152, 0.0722));
    color = mix(vec3(luminance), color, uPackParams0.y);
    const float LinearMidGrey = 0.18;
    color = (color - LinearMidGrey) * uPackParams0.z + LinearMidGrey;

    vec2 centered = vUv * 2.0 - 1.0;
    float vignette = smoothstep(1.25, 0.25, dot(centered, centered));
    color *= mix(1.0, vignette, clamp(uPackParams0.w, 0.0, 1.0));
    oColor = vec4(acdreamEncodeDisplay(clamp(color, 0.0, 1.0)), 1.0);
}
