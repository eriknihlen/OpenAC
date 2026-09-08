#version 430 core

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;

// set 0 binding 0 — per-instance transforms, exactly as GpuBindingModel pins it.
struct InstanceData {
    mat4 transform;
};
layout(std430, binding = 0) readonly buffer InstanceBuffer {
    InstanceData Instances[];
};

struct BatchData {
    uint textureIndex;
    uint textureLayer;
    uint tint;
    uint pad;
};
layout(std430, binding = 1) readonly buffer BatchBuffer {
    BatchData Batches[];
};

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vTexCoord;
layout(location = 2) out flat uint vTextureIndex;
layout(location = 3) out flat uint vTextureLayer;
layout(location = 4) out flat uint vTint;

void main() {
    // gl_BaseInstanceARB + gl_InstanceID is the GL idiom mesh_modern uses; the
    // injected preamble maps it onto Vulkan's gl_InstanceIndex, which already
    // includes firstInstance.
    int instanceIndex = gl_BaseInstanceARB + gl_InstanceID;
    mat4 model = Instances[instanceIndex].transform;

    BatchData b = Batches[uDrawIDOffset + gl_DrawIDARB];
    vTextureIndex = b.textureIndex;
    vTextureLayer = b.textureLayer;
    vTint = b.tint;

    vec4 world = model * vec4(aPosition, 1.0);
    gl_Position = uViewProjection * world;
    vNormal = mat3(model) * aNormal;
    vTexCoord = aTexCoord;
}
