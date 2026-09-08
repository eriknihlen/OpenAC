using System.Reflection;
using System.Runtime.InteropServices;
using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class ParticleBindlessInstanceTests
{
    [Fact]
    public void BillboardGpuInstance_MatchesVertexAttributeAbi()
    {
        Assert.Equal(72, Marshal.SizeOf<ParticleRenderer.BillboardGpuInstance>());
        Assert.Equal(
            new IntPtr(64),
            Marshal.OffsetOf<ParticleRenderer.BillboardGpuInstance>(
                nameof(ParticleRenderer.BillboardGpuInstance.TextureIndex)));
        Assert.Equal(
            new IntPtr(68),
            Marshal.OffsetOf<ParticleRenderer.BillboardGpuInstance>(
                nameof(ParticleRenderer.BillboardGpuInstance.ClipSlot)));
    }

    [Fact]
    public void BillboardShaders_ConsumeOneBindlessTextureHandlePerInstance()
    {
        string shadersDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Rendering",
            "Shaders");
        string vertex = File.ReadAllText(Path.Combine(shadersDirectory, "particle.vert"));
        string fragment = File.ReadAllText(Path.Combine(shadersDirectory, "particle.frag"));

        Assert.Contains("layout(location = 6) in uint aTextureIndex;", vertex);
        Assert.Contains("layout(location = 7) in uint aClipSlot;", vertex);
        Assert.Contains("flat out uint vTextureIndex;", vertex);
        Assert.Contains("vTextureIndex = aTextureIndex;", vertex);
        Assert.Contains("#extension GL_ARB_bindless_texture : require", fragment);
        Assert.Contains("flat in uint vTextureIndex;", fragment);
        Assert.Contains("ACDREAM_SAMPLE_ARRAY(vTextureIndex, vec3(vTex, 0.0))", fragment);
        Assert.Contains("vTextureIndex != ACDREAM_TEXTURE_NONE", fragment);
        Assert.DoesNotContain("uniform sampler2D uParticleTexture", fragment);
    }

    [Fact]
    public void TheReservedNoTextureSlotAgreesBetweenCpuAndTheVulkanPreamble()
    {
        const string literal = "0xFFFFFFFF";

        object? cpuValue = typeof(ParticleRenderer)
            .GetField("NoTextureSlot", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetRawConstantValue();
        Assert.Equal(0xFFFFFFFFu, Assert.IsType<uint>(cpuValue));

        string preamble = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "tools", "ShaderCompiler", "VulkanGlslPreamble.cs"));
        Assert.Contains($"#define ACDREAM_TEXTURE_NONE {literal}u", preamble);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repository root from the test binary.");
    }
}
