namespace AcDream.Plugin.Abstractions.Rendering;

/// <summary>
/// The fixed interface a render pack's shaders must match: which descriptor
/// sets and bindings the client fills in, how large each block is, and how big
/// a shader file may be. A pack's compiled shaders are rejected when they bind
/// anything outside this layout.
/// </summary>
public static class RenderPackShaderAbi
{
    /// <summary>
    /// The version of this shader interface. It changes when the layout of a
    /// block below changes.
    /// </summary>
    public const int ShaderAbiVersion = 2;

    /// <summary>The descriptor set in which the client binds the uniform blocks below.</summary>
    public const int UniformDescriptorSet = 3;

    /// <summary>
    /// Binding of the per-frame atmosphere block: sun and camera facts, weather,
    /// frame time, and the wind values.
    /// </summary>
    public const int AtmosphericFrameBinding = 5;

    /// <summary>ABI v1 AtmosphericFrame size: seven vec4/mat4 members, 160 bytes.</summary>
    public const int AtmosphericFrameSizeBytesV1 = 160;

    /// <summary>ABI v2 AtmosphericFrame size, 192 bytes.</summary>
    public const int AtmosphericFrameSizeBytesV2 = 192;

    /// <summary>The AtmosphericFrame size shaders must declare today.</summary>
    public const int AtmosphericFrameSizeBytes = AtmosphericFrameSizeBytesV2;

    /// <summary>
    /// Binding of the directional-shadow block: the cascade matrices, their
    /// ranges and biases, and the chosen light direction.
    /// </summary>
    public const int DirectionalShadowBinding = 6;

    /// <summary>Size in bytes of the directional-shadow block.</summary>
    public const int DirectionalShadowSizeBytes = 336;

    /// <summary>
    /// Binding of the per-pass parameter block the client fills for the pass
    /// being drawn.
    /// </summary>
    public const int PackPassBinding = 7;

    /// <summary>Size in bytes of the per-pass parameter block: four vec4s.</summary>
    public const int PackPassSizeBytes = 64;

    /// <summary>
    /// Binding of the block holding the pack's own settings, one float per
    /// declared setting in declaration order.
    /// </summary>
    public const int PackSettingsBinding = 8;

    /// <summary>Size in bytes of the settings block: sixteen vec4s.</summary>
    public const int PackSettingsSizeBytes = 256;

    /// <summary>
    /// How many settings a pack may declare, because the settings block holds
    /// that many float slots.
    /// </summary>
    public const int PackSettingScalarCapacity = 64;

    /// <summary>The descriptor set holding the client's shared texture table.</summary>
    public const int SampledTextureDescriptorSet = 2;

    /// <summary>The binding of the shared texture table inside its set.</summary>
    public const int SampledTextureBinding = 0;

    /// <summary>How many textures one declared pass may sample.</summary>
    public const int SampledPassInputCapacity = 4;

    /// <summary>
    /// Size in bytes of the push-constant block the client always supplies: a
    /// 4-by-4 matrix followed by eight scalars.
    /// </summary>
    public const int PushConstantSizeBytes = 96;

    /// <summary>The largest single compiled shader file the client will load, 16 MiB.</summary>
    public const int MaximumShaderAssetBytes = 16 * 1024 * 1024;
}
