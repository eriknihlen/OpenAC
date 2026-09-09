#version 460 core
#extension GL_ARB_shader_draw_parameters : require

#include "directional_shadow_common.glsl"
#include "atmospheric_common.glsl"
#include "foliage_wind.glsl"

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;

struct InstanceData { mat4 transform; };
struct BatchData {
    uint textureIndex;
    uint _pad;
    uint textureLayer;
    uint flags;
};

layout(std430, binding = 0) readonly buffer InstanceBuffer {
    InstanceData Instances[];
};
layout(std430, binding = 1) readonly buffer BatchBuffer {
    BatchData Batches[];
};

uniform int uDrawIDOffset;
uniform int uRenderPass;

out vec2 vShadowTexCoord;
flat out uint vShadowTextureIndex;
flat out uint vShadowTextureLayer;

void main() {
    int instanceIndex = gl_BaseInstanceARB + gl_InstanceID;
    mat4 model = Instances[instanceIndex].transform;
    BatchData batch = Batches[uDrawIDOffset + gl_DrawIDARB];
    vec4 worldPosition = model * vec4(aPosition, 1.0);
    worldPosition.xyz = acdreamFoliageDisplace(
        worldPosition.xyz,
        model[3].xyz,
        batch.flags,
        uAtmosphereClockWind,
        uAtmosphereWindAmplitude);
    gl_Position = uShadowWorldToClip[uRenderPass] * worldPosition;

    vShadowTexCoord = aTexCoord;
    vShadowTextureIndex = batch.textureIndex;
    vShadowTextureLayer = batch.textureLayer;
}
