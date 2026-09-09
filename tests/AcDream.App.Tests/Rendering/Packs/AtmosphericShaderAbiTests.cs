using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Packs;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class AtmosphericShaderAbiTests
{
    [Fact]
    public void HostStructsMatchTheCheckedInStd140AtmosphericAbi()
    {
        Assert.Equal(192, Marshal.SizeOf<AtmosphericFrameUniforms>());
        Assert.Equal(0, Offset<AtmosphericFrameUniforms>(nameof(AtmosphericFrameUniforms.SunScreen)));
        Assert.Equal(16, Offset<AtmosphericFrameUniforms>(nameof(AtmosphericFrameUniforms.SunColor)));
        Assert.Equal(32, Offset<AtmosphericFrameUniforms>(nameof(AtmosphericFrameUniforms.Viewport)));
        Assert.Equal(48, Offset<AtmosphericFrameUniforms>(nameof(AtmosphericFrameUniforms.Weather)));
        Assert.Equal(64, Offset<AtmosphericFrameUniforms>(nameof(AtmosphericFrameUniforms.SunDirection)));
        Assert.Equal(80, Offset<AtmosphericFrameUniforms>(nameof(AtmosphericFrameUniforms.Policy)));
        Assert.Equal(96, Offset<AtmosphericFrameUniforms>(nameof(AtmosphericFrameUniforms.InverseViewProjection)));
        Assert.Equal(160, Offset<AtmosphericFrameUniforms>(nameof(AtmosphericFrameUniforms.ClockWind)));
        Assert.Equal(176, Offset<AtmosphericFrameUniforms>(nameof(AtmosphericFrameUniforms.WindAmplitude)));
        Assert.Equal(160, RenderPackShaderAbi.AtmosphericFrameSizeBytesV1);
        Assert.Equal(192, RenderPackShaderAbi.AtmosphericFrameSizeBytesV2);
        Assert.Equal(2, RenderPackShaderAbi.ShaderAbiVersion);

        Assert.Equal(64, Marshal.SizeOf<AtmosphericPackPassUniforms>());
        Assert.Equal(0, Offset<AtmosphericPackPassUniforms>(nameof(AtmosphericPackPassUniforms.Params0)));
        Assert.Equal(16, Offset<AtmosphericPackPassUniforms>(nameof(AtmosphericPackPassUniforms.Params1)));
        Assert.Equal(32, Offset<AtmosphericPackPassUniforms>(nameof(AtmosphericPackPassUniforms.Params2)));
        Assert.Equal(48, Offset<AtmosphericPackPassUniforms>(nameof(AtmosphericPackPassUniforms.Params3)));
        Assert.Equal(256, Marshal.SizeOf<PackSettingsUniforms>());

        Assert.Equal(5u, GpuBindingModel.UniformAtmosphericFrame);
        Assert.Equal(6u, GpuBindingModel.UniformDirectionalShadow);
        Assert.Equal(7u, GpuBindingModel.UniformPackPass);
        Assert.Equal(8u, GpuBindingModel.UniformPackSettings);
        Assert.Equal(5, VulkanFrameBindings.UniformBindingCount);
        Assert.Equal(
            [1u, 2u, 3u, 4u],
            VulkanPipelineLayouts.DeclaredUniformBindings);
        Assert.Equal(3, RenderPackShaderAbi.UniformDescriptorSet);
        Assert.Equal(3u, GpuBindingModel.RenderPackUniformSet);
        Assert.False(VulkanPipelineLayouts.IsDeclaredUniformBinding(5));
        Assert.True(VulkanPipelineLayouts.IsDeclaredPackUniformBinding(5));
        Assert.True(VulkanPipelineLayouts.IsDeclaredPackUniformBinding(8));

        Assert.Equal(RenderPackShaderAbi.AtmosphericFrameBinding, (int)GpuBindingModel.UniformAtmosphericFrame);
        Assert.Equal(RenderPackShaderAbi.AtmosphericFrameSizeBytes, AtmosphericFrameUniforms.SizeInBytes);
        Assert.Equal(RenderPackShaderAbi.DirectionalShadowBinding, (int)GpuBindingModel.UniformDirectionalShadow);
        Assert.Equal(RenderPackShaderAbi.PackPassBinding, (int)GpuBindingModel.UniformPackPass);
        Assert.Equal(RenderPackShaderAbi.PackPassSizeBytes, AtmosphericPackPassUniforms.SizeInBytes);
        Assert.Equal(RenderPackShaderAbi.PackSettingsBinding, (int)GpuBindingModel.UniformPackSettings);
        Assert.Equal(RenderPackShaderAbi.PackSettingsSizeBytes, PackSettingsUniforms.SizeInBytes);
        Assert.Equal(RenderPackShaderAbi.PushConstantSizeBytes, GpuBindingModel.PushConstantBytes);
    }

    [Fact]
    public void CheckedInCommonIncludeNamesTheSameBindingsAndMemberOrder()
    {
        string text = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "AcDream.App",
            "Rendering",
            "Shaders",
            "atmospheric_common.glsl"));

        AssertOrdered(text,
            "ACDREAM_PACK_UBO_SET binding = 5",
            "uAtmosphereSunScreen",
            "uAtmosphereSunColor",
            "uAtmosphereViewport",
            "uAtmosphereWeather",
            "uAtmosphereSunDirection",
            "uAtmospherePolicy",
            "uAtmosphereInverseViewProjection",
            "uAtmosphereClockWind",
            "uAtmosphereWindAmplitude",
            "binding = 7",
            "uPackParams0",
            "uPackParams1",
            "uPackParams2",
            "uPackParams3",
            "FusedAtmosphericPostProcess PackPass ABI",
            "binding = 8",
            "uPackSettings[16]");
    }

    [Theory]
    [InlineData("mesh_atmospheric.vert")]
    [InlineData("directional_shadow_world_opaque.vert")]
    [InlineData("directional_shadow_world_opaque_multiview.vert")]
    [InlineData("directional_shadow_world_cutout.vert")]
    [InlineData("directional_shadow_world_cutout_multiview.vert")]
    public void FoliageWindShadersEachCallAcdreamFoliageDisplaceExactlyOnce(string fileName)
    {
        string text = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "AcDream.App", "Rendering", "Shaders", fileName));

        Assert.Contains("#include \"foliage_wind.glsl\"", text, StringComparison.Ordinal);
        int calls = CountOccurrences(text, "acdreamFoliageDisplace(");
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("mesh_modern.vert")]
    [InlineData("terrain_modern.vert")]
    [InlineData("terrain_atmospheric.vert")]
    [InlineData("directional_shadow_terrain.vert")]
    [InlineData("directional_shadow_terrain_multiview.vert")]
    public void RetailAndTerrainShadersNeverCallAcdreamFoliageDisplace(string fileName)
    {
        string text = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "AcDream.App", "Rendering", "Shaders", fileName));

        Assert.DoesNotContain("foliage_wind.glsl", text, StringComparison.Ordinal);
        Assert.DoesNotContain("acdreamFoliageDisplace(", text, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }

    [Fact]
    public void FusedLowShadersRetainTheDeclaredOcclusionAndBloomPixelKernels()
    {
        string shaderRoot = Path.Combine(
            RepositoryRoot(),
            "src",
            "AcDream.App",
            "Rendering",
            "Shaders");
        string occlusion = File.ReadAllText(Path.Combine(
            shaderRoot,
            "atmospheric_sun_occlusion.frag"));
        string rays = File.ReadAllText(Path.Combine(
            shaderRoot,
            "atmospheric_sun_rays.frag"));
        string blur = File.ReadAllText(Path.Combine(
            shaderRoot,
            "atmospheric_bloom_blur.frag"));
        string downsample = File.ReadAllText(Path.Combine(
            shaderRoot,
            "atmospheric_bloom_downsample.frag"));
        string filmic = File.ReadAllText(Path.Combine(
            shaderRoot,
            "atmospheric_filmic.frag"));

        foreach (string threshold in (string[])["0.9975", "0.99995"])
        {
            Assert.Contains(threshold, occlusion, StringComparison.Ordinal);
            Assert.Contains(threshold, rays, StringComparison.Ordinal);
        }
        foreach (string kernel in (string[])
                 ["0.227027", "0.316216", "1.384615", "0.070270", "3.230769"])
        {
            Assert.Contains(kernel, blur, StringComparison.Ordinal);
            Assert.Contains(kernel, filmic, StringComparison.Ordinal);
        }
        Assert.Contains("round(clamp(unobstructedSky * enabled", rays,
            StringComparison.Ordinal);
        Assert.Contains("uPackParams1.z > 0.5", filmic,
            StringComparison.Ordinal);
        Assert.Contains("brightness - threshold + knee", filmic,
            StringComparison.Ordinal);
        foreach (string extraction in (string[])
                 ["0.2126", "0.7152", "0.0722", "brightness - threshold + knee"])
        {
            Assert.Contains(extraction, downsample, StringComparison.Ordinal);
            Assert.Contains(extraction, filmic, StringComparison.Ordinal);
        }
        const double oneDimensionalWeight =
            0.227027 + (2 * 0.316216) + (2 * 0.070270);
        Assert.InRange(
            oneDimensionalWeight * oneDimensionalWeight,
            0.99999,
            1.00001);
    }

    private static int Offset<T>(string field) where T : struct =>
        Marshal.OffsetOf<T>(field).ToInt32();

    private static void AssertOrdered(string text, params string[] tokens)
    {
        int prior = -1;
        foreach (string token in tokens)
        {
            int next = text.IndexOf(token, prior + 1, StringComparison.Ordinal);
            Assert.True(next > prior, $"'{token}' is missing or out of ABI order.");
            prior = next;
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
