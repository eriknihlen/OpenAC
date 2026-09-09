#version 460 core
#extension GL_ARB_bindless_texture : require


layout(location = 0) in vec3  aPos;
layout(location = 1) in vec3  aNormal;
layout(location = 2) in uvec4 aPacked0;
layout(location = 3) in uvec4 aPacked1;
layout(location = 4) in uvec4 aPacked2;
layout(location = 5) in uvec4 aPacked3;

uniform mat4 uViewProjection;

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


out vec2  vBaseUV;
out vec3  vWorldNormal;
out vec3  vWorldPos;
out vec3  vLightingRGB;
out vec4  vOverlay0;
out vec4  vOverlay1;
out vec4  vOverlay2;
out vec4  vRoad0;
out vec4  vRoad1;
flat out float vBaseTexIdx;

const float MIN_FACTOR = 0.0;

vec4 unpackOverlayLayer(uint texIdxU, uint alphaIdxU, uint rotIdx, vec2 baseUV) {
    float texIdx   = float(texIdxU);
    float alphaIdx = float(alphaIdxU);
    if (texIdx   >= 254.0) texIdx   = -1.0;
    if (alphaIdx >= 254.0) alphaIdx = -1.0;

    vec2 rotatedUV = baseUV;
    if      (rotIdx == 1u) rotatedUV = vec2(1.0 - baseUV.y,       baseUV.x);
    else if (rotIdx == 2u) rotatedUV = vec2(1.0 - baseUV.x, 1.0 - baseUV.y);
    else if (rotIdx == 3u) rotatedUV = vec2(      baseUV.y, 1.0 - baseUV.x);

    return vec4(rotatedUV.x, rotatedUV.y, texIdx, alphaIdx);
}

void main() {
    // Unpack rotation fields from aPacked3. Bit layout (data3):
    //   .x  (byte 0): bits 0-1 rotBase (unused), 2-3 rotOvl0, 4-5 rotOvl1, 6-7 rotOvl2
    //   .y  (byte 1): bits 0-1 rotRd0 (= data3 bit 8-9),
    //                 bits 2-3 rotRd1 (= data3 bit 10-11),
    //                 bit  4   splitDir (= data3 bit 12)
    uint rotOvl0 = (aPacked3.x >> 2u) & 3u;
    uint rotOvl1 = (aPacked3.x >> 4u) & 3u;
    uint rotOvl2 = (aPacked3.x >> 6u) & 3u;
    uint rotRd0  =  aPacked3.y        & 3u;
    uint rotRd1  = (aPacked3.y >> 2u) & 3u;
    uint splitDir= (aPacked3.y >> 4u) & 1u;

    int vIdx = gl_VertexID % 6;
    int corner = 0;
    if (splitDir == 0u) {
        if      (vIdx == 0) corner = 0;
        else if (vIdx == 1) corner = 1;
        else if (vIdx == 2) corner = 2;
        else if (vIdx == 3) corner = 0;
        else if (vIdx == 4) corner = 2;
        else                corner = 3;
    } else {
        if      (vIdx == 0) corner = 0;
        else if (vIdx == 1) corner = 1;
        else if (vIdx == 2) corner = 3;
        else if (vIdx == 3) corner = 1;
        else if (vIdx == 4) corner = 2;
        else                corner = 3;
    }

    vec2 baseUV;
    if      (corner == 0) baseUV = vec2(0.0, 1.0);
    else if (corner == 1) baseUV = vec2(1.0, 1.0);
    else if (corner == 2) baseUV = vec2(1.0, 0.0);
    else                  baseUV = vec2(0.0, 0.0);

    vBaseUV = baseUV;
    vWorldPos = aPos;
    vWorldNormal = normalize(aNormal);

    vec3 sunDir = uLights[0].dirAndRange.xyz;
    vec3 sunCol = uLights[0].colorAndIntensity.xyz * uLights[0].colorAndIntensity.w;
    float L = max(dot(vWorldNormal, -sunDir), MIN_FACTOR);
    vLightingRGB = sunCol * L + uCellAmbient.xyz;

    float baseTex = float(aPacked0.x);
    if (baseTex >= 254.0) baseTex = -1.0;
    vBaseTexIdx = baseTex;

    vOverlay0 = unpackOverlayLayer(aPacked0.z, aPacked0.w, rotOvl0, baseUV);
    vOverlay1 = unpackOverlayLayer(aPacked1.x, aPacked1.y, rotOvl1, baseUV);
    vOverlay2 = unpackOverlayLayer(aPacked1.z, aPacked1.w, rotOvl2, baseUV);
    vRoad0    = unpackOverlayLayer(aPacked2.x, aPacked2.y, rotRd0,  baseUV);
    vRoad1    = unpackOverlayLayer(aPacked2.z, aPacked2.w, rotRd1,  baseUV);

    vec3 terrainPos = vec3(aPos.xy, aPos.z - 0.01);
    gl_Position = uViewProjection * vec4(terrainPos, 1.0);
}
