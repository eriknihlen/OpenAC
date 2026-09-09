#version 430 core

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTex;

layout(std140, ACDREAM_UBO_SET binding = 4) uniform SkyParams {
    mat4  uModel;
    mat4  uSkyView;
    mat4  uSkyProjection;

    // Per-frame lighting (from SkyKeyframe):
    vec3  uAmbientColor;
    float uEmissive;      // per-submesh Surface.Luminosity
    vec3  uSunColor;
    float uDiffuseFactor;
    vec3  uSunDir;        // unit vector FROM surface TO sun
    float uTransparency;  // keyframe transparency: 0 visible, 1 transparent
    vec2  uUvScroll;
    float uApplyFog;      // 1 for foggable layers; raw-additive keeps fog off
    float uSurfOpacity;   // final surface opacity multiplier from the CPU
};

struct Light {
    vec4 posAndKind;
    vec4 dirAndRange;
    vec4 colorAndIntensity;
    vec4 coneAngleEtc;
};
layout(std140, ACDREAM_UBO_SET binding = 1) uniform SceneLighting {
    Light uLights[8];
    vec4  uCellAmbient;
    vec4  uFogParams;         // x=fogStart, y=fogEnd, z=flash, w=fogMode
    vec4  uFogColor;
    vec4  uCameraAndTime;
};


out vec2 vTex;
out vec3 vTint;
out float vFogFactor;
out vec3 vDir;

void main() {
    vTex = aTex + uUvScroll;
    vDir = aPos;
    gl_Position = uSkyProjection * uSkyView * uModel * vec4(aPos, 1.0);

    vec3 worldNormal = normalize(mat3(uModel) * aNormal);

    float diff = max(dot(worldNormal, uSunDir), 0.0);
    vec3 lit = vec3(uEmissive)      // material.Emissive
             + uAmbientColor        // material.Ambient(1) × light.Ambient
             + (uSunColor * uDiffuseFactor) * diff;
    vTint = clamp(lit, 0.0, 1.0);

    vec4  worldPos = uModel * vec4(aPos, 1.0);
    float dist = length(worldPos.xyz);
    float fogStart = uFogParams.x;
    float fogEnd   = uFogParams.y;
    float span     = max(fogEnd - fogStart, 1e-3);
    vFogFactor = clamp((fogEnd - dist) / span, 0.0, 1.0);
}
