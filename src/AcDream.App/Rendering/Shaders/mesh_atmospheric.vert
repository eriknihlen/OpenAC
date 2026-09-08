#version 430 core
#extension GL_ARB_shader_draw_parameters : require

#include "directional_shadow_common.glsl"
#include "atmospheric_common.glsl"
#include "foliage_wind.glsl"

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;

struct InstanceData {
    mat4 transform;
};

struct BatchData {
    uint  textureIndex;    // slot into the global texture table
    float surfaceOpacity;  // authored material alpha (not base texture alpha)
    uint  textureLayer;
    uint  flags;           // reserved — N.5 dispatcher owns all blend state
};

layout(std430, binding = 0) readonly buffer InstanceBuffer {
    InstanceData Instances[];
};

// binding=1 here is the SSBO namespace — distinct from the UBO namespace.
// SceneLighting UBO also uses binding=1 in the fragment shader; GL keeps
// GL_SHADER_STORAGE_BUFFER and GL_UNIFORM_BUFFER binding tables separate.
// Task 10 dispatcher binds:
//   glBindBufferBase(GL_SHADER_STORAGE_BUFFER, 0, instanceSsbo)
//   glBindBufferBase(GL_SHADER_STORAGE_BUFFER, 1, batchSsbo)
// Existing SceneLightingUboBinding handles the UBO side.
layout(std430, binding = 1) readonly buffer BatchBuffer {
    BatchData Batches[];
};

layout(std430, binding = 3) readonly buffer ClipSlotBuf {
    uint instanceClipSlot[];
};

struct GlobalLight {
    vec4 posAndKind;
    vec4 dirAndRange;
    vec4 colorAndIntensity;
    vec4 coneAngleEtc;
};
layout(std430, binding = 4) readonly buffer GlobalLightBuf {
    GlobalLight gLights[];
};
layout(std430, binding = 5) readonly buffer InstanceLightSetBuf {
    int instanceLightIdx[];   // object: 8; EnvCell: 47; -1 = unused
};

layout(std430, binding = 6) readonly buffer InstanceIndoorBuf {
    uint instanceIndoor[];
};

layout(std430, binding = 7) readonly buffer InstanceAlphaBuf {
    float instanceAlpha[];
};

layout(std430, binding = 8) readonly buffer InstanceSelectionLightingBuf {
    vec2 instanceSelectionLighting[];
};

layout(std430, binding = 9) readonly buffer InstanceDetailCategoryBuf {
    uint instanceDetailCategory[];
};

uniform mat4 uViewProjection;
// Absolute transform prefix in the shared shadow/world pose arena. Every
// parallel per-instance array remains local to this submission, so only the
// transform lookup keeps the absolute index.
uniform uint uTextureIndexB;

uniform int uDrawIDOffset;
uniform int uLightingMode;   // A7 Fix D: 0 = OBJECT (plain Lambert + sun), 1 = ENVCELL (half-Lambert wrap, no sun)
uniform int uLightDebug;

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

vec3 pointContribution(vec3 N, vec3 worldPos, GlobalLight L) {
    int kind     = int(L.posAndKind.w);
    vec3 toL     = L.posAndKind.xyz - worldPos;   // D (un-normalised)
    float distsq = dot(toL, toL);
    float d      = sqrt(distsq);
    float range  = L.dirAndRange.w;               // falloff_eff = Falloff × 1.3 (static) / × 1.5 (dynamic)
    if (d >= range || range <= 1e-4) return vec3(0.0);
    float intensity = L.colorAndIntensity.w;
    vec3  baseCol   = L.colorAndIntensity.xyz;

    if (L.coneAngleEtc.y > 0.5) {
        if (uLightDebug == 2) return vec3(0.0);
        vec3 Ldir = toL / max(d, 1e-4);
        float ndl = max(0.0, dot(N, Ldir));
        if (ndl <= 0.0) return vec3(0.0);
        if (kind == 2) {
            if (dot(-Ldir, L.dirAndRange.xyz) <= cos(L.coneAngleEtc.x * 0.5)) return vec3(0.0);
        }
        return (intensity * ndl / max(d, 1e-3)) * baseCol;   // att = 1/d
    }

    float angular = (uLightingMode == 1)
        ? (1.0 / 1.5) * (dot(N, toL) + 0.5 * d)   // half-Lambert wrap (EnvCell bake)
        : max(0.0, dot(N, toL));                  // plain Lambert (object/hardware)
    if (angular <= 0.0) return vec3(0.0);
    float norm   = (distsq > 1.0) ? (distsq * d) : d;
    float scale  = (1.0 - d / range) * intensity * (angular / norm);
    if (kind == 2) {
        vec3 Ldir = toL / max(d, 1e-4);
        float cos_edge = cos(L.coneAngleEtc.x * 0.5);
        float cos_l    = dot(-Ldir, L.dirAndRange.xyz);
        if (cos_l <= cos_edge) scale = 0.0;
    }
    return min(scale * baseCol, baseCol);
}

vec3 accumulateAmbientLocalLights(
    vec3 N,
    vec3 worldPos,
    int instanceIndex,
    out vec3 directionalLit)
{
    vec3 lit = uCellAmbient.xyz;
    directionalLit = vec3(0.0);
    if (uLightDebug == 1) return lit;

    if (uLightingMode == 0) {
        if (instanceIndoor[instanceIndex] == 0u) {
            int activeLights = int(uCellAmbient.w);
            for (int i = 0; i < 8; ++i) {
                if (i >= activeLights) break;
                if (int(uLights[i].posAndKind.w) != 0) continue;   // directional only
                vec3 Ldir = -uLights[i].dirAndRange.xyz;
                float ndl = max(0.0, dot(N, Ldir));
                directionalLit += uLights[i].colorAndIntensity.xyz
                    * uLights[i].colorAndIntensity.w * ndl;
            }
        }
    }

    vec3 pointAcc = vec3(0.0);
    int lightStride = (uLightingMode == 1) ? 47 : 8;
    int base = instanceIndex * lightStride;
    for (int k = 0; k < lightStride; ++k) {
        int gi = instanceLightIdx[base + k];
        if (gi < 0) continue;
        pointAcc += pointContribution(N, worldPos, gLights[gi]);
    }
    lit += min(pointAcc, vec3(1.0));

    return lit;                        // frag still does the final min(lit, 1.0)
}

out vec3 vNormal;
out vec2 vTexCoord;
out vec3 vWorldPos;
out vec3 vAmbientLocalLit;
out vec3 vDirectionalLit;  // authored outdoor directional sun, shadowable
out flat uint  vTextureIndex;
out flat uint  vTextureLayer;
out flat float vOpacityMultiplier;
out flat vec2  vSelectionLighting;
out flat uint  vReceivesDirectionalShadow;
out flat float vSurfaceOpacity;
out flat uint  vBatchFlags;
out flat uint  vDetailCategory;

void main() {
    int transformIndex = gl_BaseInstanceARB + gl_InstanceID;
    int instanceIndex = transformIndex - int(uTextureIndexB);
    mat4 model = Instances[transformIndex].transform;
    vOpacityMultiplier = instanceAlpha[instanceIndex];
    vSelectionLighting = (uLightingMode == 0)
        ? instanceSelectionLighting[instanceIndex]
        : vec2(0.0, 1.0);

    BatchData b = Batches[uDrawIDOffset + gl_DrawIDARB];

    vec4 worldPos = model * vec4(aPosition, 1.0);
    worldPos.xyz = acdreamFoliageDisplace(
        worldPos.xyz,
        model[3].xyz,
        b.flags,
        uAtmosphereClockWind,
        uAtmosphereWindAmplitude);
    gl_Position = uViewProjection * worldPos;

    vWorldPos = worldPos.xyz;
    vNormal = normalize(mat3(model) * aNormal);
    vAmbientLocalLit = accumulateAmbientLocalLights(
        vNormal,
        vWorldPos,
        instanceIndex,
        vDirectionalLit);
    // EnvCell-parented objects keep the authored indoor result. The separate
    // EnvCell shell renderer never selects this receiver variant at all.
    vReceivesDirectionalShadow = (uLightingMode == 0
        && instanceIndoor[instanceIndex] == 0u)
        ? 1u
        : 0u;
    vTexCoord = aTexCoord;

    vTextureIndex = b.textureIndex;
    vTextureLayer = b.textureLayer;
    vSurfaceOpacity = b.surfaceOpacity;
    vBatchFlags = b.flags;
    vDetailCategory = instanceDetailCategory[instanceIndex];
}
