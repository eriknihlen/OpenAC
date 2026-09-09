#version 460 core
#extension GL_ARB_bindless_texture : require


in  vec2  vBaseUV;
in  vec3  vWorldNormal;
in  vec3  vWorldPos;
in  vec3  vLightingRGB;
in  vec4  vOverlay0;
in  vec4  vOverlay1;
in  vec4  vOverlay2;
in  vec4  vRoad0;
in  vec4  vRoad1;
flat in float vBaseTexIdx;

out vec4 fragColor;

uniform uint uTextureIndexA;
uniform uint uTextureIndexB;

#define sampleTerrain(uvw) ACDREAM_SAMPLE_ARRAY(uTextureIndexA, uvw)
#define sampleAlpha(uvw)   ACDREAM_SAMPLE_ARRAY(uTextureIndexB, uvw)

layout(std140, ACDREAM_UBO_SET binding = 3) uniform TerrainTiling {
    float uTexTiling[36];
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
    vec4  uFogParams;
    vec4  uFogColor;
    vec4  uCameraAndTime;
};

float terrainTiling(float layer) {
    return uTexTiling[int(layer)];
}

vec4 maskBlend3(vec4 t0, vec4 t1, vec4 t2, float h0, float h1, float h2) {
    float a0 = h0 == 0.0 ? 1.0 : t0.a;
    float a1 = h1 == 0.0 ? 1.0 : t1.a;
    float a2 = h2 == 0.0 ? 1.0 : t2.a;
    float aR = 1.0 - (a0 * a1 * a2);
    float aRsafe = max(aR, 1e-6);
    a0 = 1.0 - a0;
    a1 = 1.0 - a1;
    a2 = 1.0 - a2;
    vec3 r0 = (a0 * t0.rgb + (1.0 - a0) * a1 * t1.rgb + (1.0 - a1) * a2 * t2.rgb);
    return vec4(r0 / aRsafe, aR);
}

vec4 combineOverlays(vec2 baseUV, vec4 pOverlay0, vec4 pOverlay1, vec4 pOverlay2) {
    float h0 = pOverlay0.z < 0.0 ? 0.0 : 1.0;
    float h1 = pOverlay1.z < 0.0 ? 0.0 : 1.0;
    float h2 = pOverlay2.z < 0.0 ? 0.0 : 1.0;
    vec4 t0 = vec4(0.0), t1 = vec4(0.0), t2 = vec4(0.0);

    if (h0 > 0.0) {
        t0 = sampleTerrain(vec3(baseUV * terrainTiling(pOverlay0.z), pOverlay0.z));
        if (pOverlay0.w >= 0.0) {
            vec4 a = sampleAlpha(vec3(pOverlay0.xy, pOverlay0.w));
            t0.a = a.a;
        }
    }
    if (h1 > 0.0) {
        t1 = sampleTerrain(vec3(baseUV * terrainTiling(pOverlay1.z), pOverlay1.z));
        if (pOverlay1.w >= 0.0) {
            vec4 a = sampleAlpha(vec3(pOverlay1.xy, pOverlay1.w));
            t1.a = a.a;
        }
    }
    if (h2 > 0.0) {
        t2 = sampleTerrain(vec3(baseUV * terrainTiling(pOverlay2.z), pOverlay2.z));
        if (pOverlay2.w >= 0.0) {
            vec4 a = sampleAlpha(vec3(pOverlay2.xy, pOverlay2.w));
            t2.a = a.a;
        }
    }
    return maskBlend3(t0, t1, t2, h0, h1, h2);
}

vec4 combineRoad(vec2 baseUV, vec4 pRoad0, vec4 pRoad1) {
    float h0 = pRoad0.z < 0.0 ? 0.0 : 1.0;
    float h1 = pRoad1.z < 0.0 ? 0.0 : 1.0;
    vec4 result = vec4(0.0);
    if (h0 > 0.0) {
        result = sampleTerrain(vec3(baseUV * terrainTiling(pRoad0.z), pRoad0.z));
        if (pRoad0.w >= 0.0) {
            vec4 a0 = sampleAlpha(vec3(pRoad0.xy, pRoad0.w));
            result.a = 1.0 - a0.a;
            if (h1 > 0.0 && pRoad1.w >= 0.0) {
                vec4 a1 = sampleAlpha(vec3(pRoad1.xy, pRoad1.w));
                result.a = 1.0 - (a0.a * a1.a);
            }
        }
    }
    return result;
}

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

void main() {
    vec4 baseColor = vec4(0.0);
    if (vBaseTexIdx >= 0.0) {
        baseColor = sampleTerrain(vec3(vBaseUV * terrainTiling(vBaseTexIdx), vBaseTexIdx));
    }

    vec4 overlays = vec4(0.0);
    if (vOverlay0.z >= 0.0)
        overlays = combineOverlays(vBaseUV, vOverlay0, vOverlay1, vOverlay2);

    vec4 roads = vec4(0.0);
    if (vRoad0.z >= 0.0)
        roads = combineRoad(vBaseUV, vRoad0, vRoad1);

    vec3 baseMasked = baseColor.rgb * ((1.0 - overlays.a) * (1.0 - roads.a));
    vec3 ovlMasked  = overlays.rgb  * (overlays.a        * (1.0 - roads.a));
    vec3 roadMasked = roads.rgb     *  roads.a;
    vec3 rgb = clamp(baseMasked + ovlMasked + roadMasked, 0.0, 1.0);

    vec3 lit = rgb * min(vLightingRGB, vec3(1.0));

    float flash = uFogParams.z;
    lit += flash * vec3(0.6, 0.6, 0.75);

    lit = applyFog(lit, vWorldPos);

    fragColor = vec4(lit, 1.0);
}
