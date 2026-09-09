#version 430 core
#extension GL_ARB_bindless_texture : require

in vec3 vNormal;
in vec2 vTexCoord;
in vec3 vWorldPos;
in vec3 vAmbientLocalLit;
in vec3 vDirectionalLit;
in flat uint  vTextureIndex;
in flat uint  vTextureLayer;
in flat float vOpacityMultiplier;
in flat vec2  vSelectionLighting;   // x=luminosity, y=diffuse
in flat uint  vReceivesDirectionalShadow;
in flat float vSurfaceOpacity;
in flat uint  vBatchFlags;
in flat uint  vDetailCategory;

#include "directional_shadow_receiver.glsl"
#include "retail_detail_material.glsl"

uniform int uRenderPass;
uniform int uLightDebug;
uniform uint uTextureIndexA;
uniform float uParamA;
uniform float uParamB;

// SceneLighting UBO — IDENTICAL layout to mesh_instanced.frag binding=1.
struct Light {
    vec4 posAndKind;
    vec4 dirAndRange;
    vec4 colorAndIntensity;
    vec4 coneAngleEtc;
};
layout(std140, ACDREAM_UBO_SET binding = 1) uniform SceneLighting {
    Light uLights[8];
    vec4  uCellAmbient;
    vec4  uFogParams;
    vec4  uFogColor;
    vec4  uCameraAndTime;
};


vec3 applyFog(vec3 lit, vec3 worldPos) {
    int mode = int(uFogParams.w);
    if (mode == 0) return lit;
    float d = length(worldPos - uCameraAndTime.xyz);
    float fogStart = uFogParams.x;
    float fogEnd   = uFogParams.y;
    float span = max(1e-3, fogEnd - fogStart);
    float fog = clamp((d - fogStart) / span, 0.0, 1.0);
    return mix(lit, uFogColor.xyz, fog);
}

out vec4 FragColor;

bool isRetailClipReference(float value) {
    return abs(value - (100.0 / 255.0)) < 0.000001
        || abs(value - (200.0 / 255.0)) < 0.000001;
}

void main() {
    vec4 color = ACDREAM_SAMPLE_ARRAY(vTextureIndex, vec3(vTexCoord, float(vTextureLayer)));
    float alphaCutoff = isRetailClipReference(uParamB) ? uParamB : 0.05;

    vec3 surfaceToLight = normalize(uShadowLightDirectionAndSource.xyz);
    float directionalVisibility = vReceivesDirectionalShadow != 0u
        ? acdreamDirectionalShadowVisibility(
            vWorldPos,
            normalize(vNormal),
            uCameraAndTime.xyz,
            surfaceToLight)
        : 1.0;
    vec3 sceneLit = vAmbientLocalLit
        + vDirectionalLit * directionalVisibility;
    vec3 lit = vec3(vSelectionLighting.x)
        + vSelectionLighting.y * sceneLit;

    if (uLightDebug == 3) {
        if (color.a < alphaCutoff) discard;
        FragColor = vec4(min(lit, vec3(1.0)), 1.0);
        return;
    }

    // Lightning flash — additive scene bump (matches mesh_instanced.frag).
    lit += uFogParams.z * vec3(0.6, 0.6, 0.75);

    lit = min(lit, vec3(1.0));

    bool detailActive = uParamA != 0.0
        && vDetailCategory != 0u
        && (vBatchFlags & 1u) != 0u;
    vec3 rgb;
    float alpha;
    if (detailActive) {
        vec4 detail = ACDREAM_SAMPLE_ARRAY(
            uTextureIndexA,
            vec3(vTexCoord * uParamA, 0.0));
        RetailDetailMaterialResult material = acdreamRetailDetailMaterial(
            color.rgb,
            lit,
            detail,
            vSurfaceOpacity * vOpacityMultiplier);
        rgb = material.rgb;
        alpha = material.alpha;
    } else {
        rgb = color.rgb * lit;
        alpha = color.a * vOpacityMultiplier;
    }

    if (detailActive
            ? isRetailClipReference(uParamB) && alpha < uParamB
            : color.a < alphaCutoff)
        discard;

    if (!detailActive || (uRenderPass & 0x200) == 0)
        rgb = applyFog(rgb, vWorldPos);
    FragColor = vec4(rgb, alpha);
}
