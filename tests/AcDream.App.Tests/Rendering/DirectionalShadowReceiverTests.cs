using System.Numerics;
using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class DirectionalShadowReceiverTests
{
    [Theory]
    [InlineData("atmospheric-world-hdr", false, true, false)]
    [InlineData("atmospheric-world-hdr", true, false, false)]
    [InlineData("vk-world", true, true, false)]
    [InlineData("atmospheric-world-hdr", true, true, true)]
    internal void RetailPipelineRemainsExactUnlessPackAndCurrentBindingAreActive(
        string pass,
        bool source,
        bool binding,
        bool expected) =>
        Assert.Equal(
            expected,
            DirectionalShadowReceiverPolicy.ShouldSelectReceiverPipeline(
                pass,
                source,
                binding));

    [Fact]
    public void CascadeTransition_IsContinuousAcrossTheSplitInWorldMetres()
    {
        Vector4 splits = new(10f, 30f, 60f, 100f);

        DirectionalShadowCascadeBlend atSplit =
            DirectionalShadowReceiverPolicy.SelectCascade(10f, splits, 4, 2f);
        DirectionalShadowCascadeBlend afterSplit =
            DirectionalShadowReceiverPolicy.SelectCascade(10.0001f, splits, 4, 2f);

        Assert.Equal(0, atSplit.PrimaryCascade);
        Assert.Equal(1, atSplit.SecondaryCascade);
        Assert.Equal(1f, atSplit.SecondaryWeight, 5);
        Assert.Equal(1, afterSplit.PrimaryCascade);
        Assert.Equal(2, afterSplit.SecondaryCascade);
        Assert.InRange(afterSplit.SecondaryWeight, 0f, 0.00001f);
        Assert.True(atSplit.WithinShadowReach);
        Assert.True(afterSplit.WithinShadowReach);
    }

    [Fact]
    public void CascadeSelection_StopsAtConfiguredReach()
    {
        DirectionalShadowCascadeBlend outside =
            DirectionalShadowReceiverPolicy.SelectCascade(
                100.01f,
                new Vector4(10f, 30f, 60f, 100f),
                4,
                2f);

        Assert.False(outside.WithinShadowReach);
        Assert.Equal(3, outside.PrimaryCascade);
    }

    [Fact]
    public void BiasRemainsWorldMetresAndScalesWithSurfaceSlope()
    {
        var bias = new DirectionalShadowWorldBias(0.01f, 0.04f, 0.02f);

        Assert.Equal(0.01f, DirectionalShadowReceiverPolicy.ReceiverBiasMeters(bias, 1f), 6);
        Assert.Equal(0.05f, DirectionalShadowReceiverPolicy.ReceiverBiasMeters(bias, 0f), 6);
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, true, false)]
    internal void IndoorAndMissingDirectionalTermsNeverSample(
        bool binding,
        bool indoor,
        bool directional,
        bool expected) =>
        Assert.Equal(
            expected,
            DirectionalShadowReceiverPolicy.ShouldSample(
                binding,
                indoor,
                directional));

    [Fact]
    public void ReceiverShadersPreserveAnimatedFoliageMaterialAndTransparencySemantics()
    {
        string root = RepositoryRoot();
        string shaderRoot = Path.Combine(
            root,
            "src", "AcDream.App", "Rendering", "Shaders");
        string vertex = File.ReadAllText(Path.Combine(shaderRoot, "mesh_atmospheric.vert"));
        string fragment = File.ReadAllText(Path.Combine(shaderRoot, "mesh_atmospheric.frag"));
        string terrain = File.ReadAllText(Path.Combine(shaderRoot, "terrain_atmospheric.frag"));
        string receiver = File.ReadAllText(Path.Combine(
            shaderRoot,
            "directional_shadow_receiver.glsl"));

        Assert.Contains("int transformIndex = gl_BaseInstanceARB + gl_InstanceID", vertex, StringComparison.Ordinal);
        Assert.Contains("int instanceIndex = transformIndex - int(uTextureIndexB)", vertex, StringComparison.Ordinal);
        Assert.Contains("Instances[transformIndex].transform", vertex, StringComparison.Ordinal);
        Assert.Contains("instanceIndoor[instanceIndex]", vertex, StringComparison.Ordinal);
        Assert.Contains("vSelectionLighting", fragment, StringComparison.Ordinal);
        Assert.Contains("vOpacityMultiplier", fragment, StringComparison.Ordinal);
        Assert.Contains("color.a < alphaCutoff", fragment, StringComparison.Ordinal);
        Assert.Contains("ACDREAM_SAMPLE_ARRAY", fragment, StringComparison.Ordinal);
        Assert.Contains("combineOverlays", terrain, StringComparison.Ordinal);
        Assert.Contains("combineRoad", terrain, StringComparison.Ordinal);
        Assert.Contains("smoothstep", receiver, StringComparison.Ordinal);
        Assert.Contains("acdreamShadowBiasScale(cascade)", receiver, StringComparison.Ordinal);
        Assert.Contains("textureGather", receiver, StringComparison.Ordinal);
        Assert.Contains("acdreamShadowBilinearCompare", receiver, StringComparison.Ordinal);
        Assert.Contains("float weight = float(2 - abs(x))", receiver, StringComparison.Ordinal);
        Assert.Contains("float reachFade = 1.0 - smoothstep", receiver, StringComparison.Ordinal);
        Assert.Contains("radius = clamp(radius, 0, 2)", receiver, StringComparison.Ordinal);
        Assert.Contains("float softness = max(uShadowControl.y, 1.0)", receiver, StringComparison.Ordinal);

        foreach (string worldVertexName in new[] { "mesh_modern.vert", "mesh_atmospheric.vert" })
        {
            string worldVertex = File.ReadAllText(Path.Combine(shaderRoot, worldVertexName));
            Assert.Contains("Instances[", worldVertex, StringComparison.Ordinal);
            Assert.Contains("].transform", worldVertex, StringComparison.Ordinal);
            Assert.Contains("instanceDetailCategory[instanceIndex]", worldVertex, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
