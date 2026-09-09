#version 430 core

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vTexCoord;
layout(location = 2) in flat uint vTextureIndex;
layout(location = 3) in flat uint vTextureLayer;
layout(location = 4) in flat uint vTint;

layout(location = 0) out vec4 FragColor;

void main() {
    vec4 tint = vec4(
        float((vTint >> 24) & 0xFFu) / 255.0,
        float((vTint >> 16) & 0xFFu) / 255.0,
        float((vTint >> 8) & 0xFFu) / 255.0,
        float(vTint & 0xFFu) / 255.0);

    vec4 albedo = tint;
    if (uLightingMode == 0) {
        albedo = texture(
            ACDREAM_TEXTURE(vTextureIndex),
            vec3(vTexCoord, float(vTextureLayer))) * tint;
    }

    // A fixed key light so the verification scene reads as three-dimensional in
    // a screenshot. uLightingMode 1 keeps geometry flat, which is what the
    // line pass wants.
    if (uLightingMode == 0) {
        vec3 light = normalize(vec3(0.4, -0.6, 0.7));
        float lambert = 0.35 + (0.65 * max(dot(normalize(vNormal), light), 0.0));
        albedo.rgb *= lambert;
    }

    if (albedo.a < 0.004) {
        discard;
    }

    FragColor = albedo;
}
