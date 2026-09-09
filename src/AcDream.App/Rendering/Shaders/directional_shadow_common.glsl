#ifndef ACDREAM_DIRECTIONAL_SHADOW_COMMON_GLSL
#define ACDREAM_DIRECTIONAL_SHADOW_COMMON_GLSL

// Render-pack shader ABI v1, set 3/binding 6. This is the byte-level SSOT for
// DirectionalShadowUniforms (std140, 336 bytes).
layout(std140, ACDREAM_PACK_UBO_SET binding = 6) uniform DirectionalShadow {
    mat4  uShadowWorldToClip[4];   //   0, 64, 128, 192
    vec4  uShadowSplitFarMeters;   // 256
    vec4  uShadowControl;          // 272: strength, softness, reach m, blend m
    vec4  uShadowBiasMeters;
    uvec4 uShadowTextureAndFlags;
    vec4  uShadowLightDirectionAndSource; // 320: surface-to-light xyz, source kind
};

#endif
