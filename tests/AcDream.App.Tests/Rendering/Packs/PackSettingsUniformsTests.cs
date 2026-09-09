using AcDream.App.Rendering.Packs;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Plugin.Abstractions.Rendering;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class PackSettingsUniformsTests
{
    [Fact]
    public void WriterResolvesPresetThenEncodesEveryV1ScalarKindInvariantly()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor with
        {
            Settings =
            [
                Setting("float", RenderSettingKind.Float, "1.25"),
                Setting("integer", RenderSettingKind.Integer, "12"),
                Setting("bool-false", RenderSettingKind.Boolean, "false"),
                Setting("bool-true", RenderSettingKind.Boolean, "true"),
                Setting("choice", RenderSettingKind.Choice, "low", ["low", "medium", "high"]),
                Setting("invalid-integer", RenderSettingKind.Integer, "1.5"),
                Setting("invalid-float", RenderSettingKind.Float, "1,5"),
            ],
        };
        RenderQualityPreset preset = descriptor.QualityPresets[0] with
        {
            SettingOverrides =
            [
                new RenderQualitySettingOverride("float", "2.5"),
                new RenderQualitySettingOverride("integer", "-7"),
                new RenderQualitySettingOverride("bool-false", "true"),
                new RenderQualitySettingOverride("choice", "high"),
            ],
        };

        PackSettingsUniforms values = PackSettingsUniforms.Create(descriptor, preset);

        Assert.Equal(2.5f, values[0]);
        Assert.Equal(-7f, values[1]);
        Assert.Equal(1f, values[2]);
        Assert.Equal(1f, values[3]);
        Assert.Equal(2f, values[4]);
        Assert.Equal(0f, values[5]);
        Assert.Equal(0f, values[6]);
        Assert.Equal(0f, values[63]);
    }

    [Fact]
    public void DescriptorValidatorRejectsMoreThanSixtyFourSettings()
    {
        RenderSettingDeclaration[] settings = Enumerable.Range(0, 65)
            .Select(index => Setting($"setting-{index}", RenderSettingKind.Float, "0"))
            .ToArray();
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor with
        {
            Settings = settings,
        };
        var device = new RecordingGpuDevice();

        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            descriptor,
            RenderPackCapabilityResolver.Resolve(device.Capabilities));

        Assert.False(result.Success);
        Assert.Contains("64-setting", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void User_value_wins_preset_and_default_in_b8_and_built_in_cpu_settings()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        RenderQualityPreset preset = descriptor.QualityPresets[0] with
        {
            SettingOverrides =
            [
                new RenderQualitySettingOverride("exposure", "1.25"),
                new RenderQualitySettingOverride("bloom-strength", "0.25"),
            ],
        };
        var overrides = new RenderPackSettingOverrides(
            new Dictionary<string, string>
            {
                ["EXPOSURE"] = "1.75",
            });

        PackSettingsUniforms uniforms = PackSettingsUniforms.Create(
            descriptor,
            preset,
            overrides);
        AtmosphericPostProcessSettings cpu = AtmosphericPostProcessSettings.FromDescriptor(
            descriptor,
            preset,
            overrides);

        int exposureIndex = descriptor.Settings.ToList().FindIndex(value => value.Id == "exposure");
        int bloomIndex = descriptor.Settings.ToList().FindIndex(value => value.Id == "bloom-strength");
        Assert.Equal(1.75f, uniforms[exposureIndex]);
        Assert.Equal(0.25f, uniforms[bloomIndex]);
        Assert.Equal(1.75f, cpu.Exposure);
        Assert.Equal(0.25f, cpu.BloomStrength);
    }

    private static RenderSettingDeclaration Setting(
        string id,
        RenderSettingKind kind,
        string defaultValue,
        IReadOnlyList<string>? choices = null) => new(
        id,
        id,
        kind,
        defaultValue,
        Minimum: null,
        Maximum: null,
        Step: null,
        choices ?? []);
}
