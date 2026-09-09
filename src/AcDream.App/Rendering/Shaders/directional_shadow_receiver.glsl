#ifndef ACDREAM_DIRECTIONAL_SHADOW_RECEIVER_GLSL
#define ACDREAM_DIRECTIONAL_SHADOW_RECEIVER_GLSL

#include "directional_shadow_common.glsl"

int acdreamShadowCascade(float cameraDistanceMeters) {
    int count = int(uShadowTextureAndFlags.y);
    for (int i = 0; i < count; ++i) {
        if (cameraDistanceMeters <= uShadowSplitFarMeters[i])
            return i;
    }
    return -1;
}

float acdreamShadowBiasScale(int cascade) {
    int farCascade = max(int(uShadowTextureAndFlags.y) - 1, 0);
    mat4 cascadeMatrix = uShadowWorldToClip[cascade];
    mat4 farMatrix = uShadowWorldToClip[farCascade];
    float cascadeDensity = 0.5 * (
        length(vec3(cascadeMatrix[0][0], cascadeMatrix[1][0], cascadeMatrix[2][0]))
        + length(vec3(cascadeMatrix[0][1], cascadeMatrix[1][1], cascadeMatrix[2][1])));
    float farDensity = 0.5 * (
        length(vec3(farMatrix[0][0], farMatrix[1][0], farMatrix[2][0]))
        + length(vec3(farMatrix[0][1], farMatrix[1][1], farMatrix[2][1])));
    return clamp(farDensity / max(cascadeDensity, 1e-7), 0.0, 1.0);
}

float acdreamShadowBilinearCompare(
    int cascade,
    vec2 uv,
    float receiverDepth)
{
    float resolution = max(float(uShadowTextureAndFlags.z), 1.0);
    vec2 texelPosition = uv * resolution - vec2(0.5);
    vec2 blend = fract(texelPosition);
    vec4 gatheredDepth = textureGather(
        ACDREAM_TEXTURE(uShadowTextureAndFlags.x),
        vec3(uv, float(cascade)),
        0);
    vec4 compared = step(vec4(receiverDepth), gatheredDepth);
    float lower = mix(compared.w, compared.z, blend.x);
    float upper = mix(compared.x, compared.y, blend.x);
    return mix(lower, upper, blend.y);
}

float acdreamShadowCascadePcf(
    int cascade,
    vec3 receiverWorldPosition,
    vec3 worldNormal,
    vec3 surfaceToLight)
{
    float ndl = clamp(dot(worldNormal, surfaceToLight), 0.0, 1.0);
    vec3 cascadeBiasMeters = max(
        uShadowBiasMeters.xyz * acdreamShadowBiasScale(cascade),
        vec3(0.001));
    float depthBiasMeters = cascadeBiasMeters.x
        + cascadeBiasMeters.y * (1.0 - ndl);
    vec3 biasedWorldPosition = receiverWorldPosition
        + worldNormal * cascadeBiasMeters.z
        + surfaceToLight * depthBiasMeters;

    vec4 shadowClip = uShadowWorldToClip[cascade]
        * vec4(biasedWorldPosition, 1.0);
    vec3 shadowNdc = shadowClip.xyz / max(abs(shadowClip.w), 1e-7);
    vec2 uv = vec2(
        shadowNdc.x * 0.5 + 0.5,
        0.5 - shadowNdc.y * 0.5);
    float receiverDepth = shadowNdc.z;
    if (uv.x <= 0.0 || uv.x >= 1.0
        || uv.y <= 0.0 || uv.y >= 1.0
        || receiverDepth <= 0.0 || receiverDepth >= 1.0)
        return 1.0;

    int radius = int((uShadowTextureAndFlags.w >> 8u) & 0xFu);
    radius = clamp(radius, 0, 2);
    float texel = 1.0 / max(float(uShadowTextureAndFlags.z), 1.0);
    float softness = max(uShadowControl.y, 1.0);
    if (radius == 0)
        return acdreamShadowBilinearCompare(cascade, uv, receiverDepth);

    float lit = 0.0;
    float weightSum = 0.0;
    if (radius == 1) {
        for (int y = 0; y < 2; ++y) {
            for (int x = 0; x < 2; ++x) {
                vec2 offset = (vec2(x, y) - vec2(0.5))
                    * softness * texel;
                lit += acdreamShadowBilinearCompare(
                    cascade, uv + offset, receiverDepth);
                weightSum += 1.0;
            }
        }
    } else {
        for (int y = -1; y <= 1; ++y) {
            for (int x = -1; x <= 1; ++x) {
                float weight = float(2 - abs(x)) * float(2 - abs(y));
                vec2 offset = vec2(x, y) * 1.5 * softness * texel;
                lit += acdreamShadowBilinearCompare(
                    cascade, uv + offset, receiverDepth) * weight;
                weightSum += weight;
            }
        }
    }
    return lit / max(weightSum, 1.0);
}

float acdreamDirectionalShadowVisibility(
    vec3 receiverWorldPosition,
    vec3 worldNormal,
    vec3 cameraWorldPosition,
    vec3 surfaceToLight)
{
    if ((uShadowTextureAndFlags.w & 1u) == 0u)
        return 1.0;

    float cameraDistanceMeters = length(
        receiverWorldPosition - cameraWorldPosition);
    if (cameraDistanceMeters > uShadowControl.z)
        return 1.0;

    int cascade = acdreamShadowCascade(cameraDistanceMeters);
    if (cascade < 0)
        return 1.0;

    float visibility = acdreamShadowCascadePcf(
        cascade,
        receiverWorldPosition,
        worldNormal,
        surfaceToLight);
    int cascadeCount = int(uShadowTextureAndFlags.y);
    if (cascade + 1 < cascadeCount) {
        float split = uShadowSplitFarMeters[cascade];
        float widthMeters = max(uShadowControl.w, 1e-4);
        float blend = smoothstep(
            max(0.0, split - widthMeters),
            split,
            cameraDistanceMeters);
        if (blend > 0.0) {
            float nextVisibility = acdreamShadowCascadePcf(
                cascade + 1,
                receiverWorldPosition,
                worldNormal,
                surfaceToLight);
            visibility = mix(visibility, nextVisibility, blend);
        }
    }
    float reachFade = 1.0 - smoothstep(
        max(0.0, uShadowControl.z - max(uShadowControl.w, 1e-4)),
        uShadowControl.z,
        cameraDistanceMeters);
    float shadowWeight = clamp(uShadowControl.x, 0.0, 1.0) * reachFade;
    return mix(1.0, visibility, shadowWeight);
}

#endif
