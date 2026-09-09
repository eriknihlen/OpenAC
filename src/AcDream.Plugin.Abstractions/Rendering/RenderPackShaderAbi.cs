namespace AcDream.Plugin.Abstractions.Rendering;

public static class RenderPackShaderAbi
{
    public const int ShaderAbiVersion = 2;

    public const int UniformDescriptorSet = 3;
    public const int AtmosphericFrameBinding = 5;

    /// <summary>ABI v1 AtmosphericFrame size: seven vec4/mat4 members, 160 bytes.</summary>
    public const int AtmosphericFrameSizeBytesV1 = 160;

    public const int AtmosphericFrameSizeBytesV2 = 192;

    public const int AtmosphericFrameSizeBytes = AtmosphericFrameSizeBytesV2;

    public const int DirectionalShadowBinding = 6;
    public const int DirectionalShadowSizeBytes = 336;
    public const int PackPassBinding = 7;
    public const int PackPassSizeBytes = 64;
    public const int PackSettingsBinding = 8;
    public const int PackSettingsSizeBytes = 256;
    public const int PackSettingScalarCapacity = 64;

    public const int SampledTextureDescriptorSet = 2;
    public const int SampledTextureBinding = 0;
    public const int SampledPassInputCapacity = 4;

    public const int PushConstantSizeBytes = 96;

    public const int MaximumShaderAssetBytes = 16 * 1024 * 1024;
}
