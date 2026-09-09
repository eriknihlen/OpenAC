using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Rendering.Gpu;

internal static class GpuBindingModel
{
    // ---- set 0: storage buffers (identical numbering to today's SSBO bindings) ----

    /// <summary>Per-instance transforms. std430 <c>InstanceData { mat4 transform; }</c>.</summary>
    public const uint StorageInstances = 0;

    public const uint StorageBatches = 1;

    public const uint StorageClipRegions = 2;

    public const uint StorageClipSlots = 3;

    /// <summary>A7 Fix B global point/spot light array.</summary>
    public const uint StorageGlobalLights = 4;

    /// <summary>
    /// Per-instance indices into the global light array: object submissions use
    /// stride 8; EnvCell submissions use stride 47. Unused entries are -1.
    /// </summary>
    public const uint StorageInstanceLightSets = 5;

    public const uint StorageInstanceIndoor = 6;

    public const uint StorageInstanceAlpha = 7;

    public const uint StorageInstanceSelectionLighting = 8;

    public const uint StorageInstanceDetailCategory = 9;


    public const uint StorageBindingCount = 10;

    // ---- set 1: uniform buffers ----

    public const uint UniformSceneLighting = 1;

    public const uint UniformTerrainTiling = 3;

    public const uint UniformSkyParams = 4;

    /// <summary>
    /// Immutable authored-atmosphere inputs for one enhanced world frame in
    /// opt-in render-pack descriptor set 3.
    /// The std140 ABI is four vec4 values: sunScreen, sunColor, viewport, and
    /// weather. See AtmosphericFrameUniforms and atmospheric_common.glsl.
    /// </summary>
    public const uint UniformAtmosphericFrame = 5;

    public const uint UniformDirectionalShadow = 6;

    public const uint UniformPackPass = 7;

    /// <summary>
    /// Pack-declared settings in declaration order: sixteen std140 vec4 values
    /// (64 scalar slots). Preset overrides are resolved before activation.
    /// </summary>
    public const uint UniformPackSettings = RenderPackShaderAbi.PackSettingsBinding;

    public const uint UniformSet = 1;

    public const uint RenderPackUniformSet = RenderPackShaderAbi.UniformDescriptorSet;

    // ---- set 2: the global texture table ----

    /// <summary>Set index of the sampled-texture descriptor array.</summary>
    public const uint TextureTableSet = 2;

    public const uint TextureTableBinding = 0;

    public const uint TextureTableCapacity = 16384;


    public const int PushConstantBytes = 96;

    public const int MaxPushConstantBytes = 128;

    // ---- shared layout facts the CPU writers and the shaders must agree on ----

    public const int GpuBatchDataStrideBytes = 16;

    public const int ClipPlanesPerSlot = 8;

    public const int ClipRegionStrideBytes = 16 + (ClipPlanesPerSlot * 16);

    public const int MaxLightsPerObject = 8;

    public const int MaxLightsPerEnvCell = 47;
}
