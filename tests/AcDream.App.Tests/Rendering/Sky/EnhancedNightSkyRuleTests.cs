using System;
using System.IO;
using Xunit;

namespace AcDream.App.Tests.Rendering.Sky;

public sealed class EnhancedNightSkyRuleTests
{
    [Fact]
    public void SkyFragmentShaderGatesTheProceduralSkyOnParamA()
    {
        string frag = File.ReadAllText(Path.Combine(ShaderRoot(), "sky.frag"));
        string code = StripLineComments(frag);

        Assert.Contains("if (uParamA > 0.5)", code, StringComparison.Ordinal);
        Assert.Contains("nightSky(normalize(vDir), uint(uParamB))", code, StringComparison.Ordinal);
        Assert.Contains("dFdx(dir)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("fwidth(g)", code, StringComparison.Ordinal);
        // No diffraction spikes — user-directed: flare is photographic.
        Assert.DoesNotContain("spike", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SkyRendererSwapsExactlyTheStarLayerAndOnlyUnderTheProvider()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "AcDream.App", "Rendering", "Sky", "SkyRenderer.cs"));
        string code = StripLineComments(source);

        Assert.Contains(
            "private const uint StarLayerGfxObjId = 0x010015EFu;",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "bool nightSky = gfxObjId == StarLayerGfxObjId",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "(EnhancedNightSkyActive?.Invoke() ?? false)",
            code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NightSkyDrawForcesTheAdditivePipeline()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "AcDream.App", "Rendering", "Sky", "SkyRenderer.Rhi.cs"));
        string code = StripLineComments(source);

        Assert.Contains(
            "nightSky || sub.IsAdditive ? _additivePipeline! : _alphaPipeline!",
            code,
            StringComparison.Ordinal);
        Assert.Contains("ParamA = nightSky ? 1f : 0f,", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositionGatesTheNightSkyOnTheAtmosphericPackRuntime()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "AcDream.App", "Composition", "FrameRootComposition.cs"));
        string code = StripLineComments(source);

        Assert.Contains("EnhancedNightSkyActive = () =>", code, StringComparison.Ordinal);
        Assert.Contains(
            "nightSkyController.ActiveRuntime?.Descriptor.Id",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "BuiltInAtmosphericRenderPack.Id",
            code,
            StringComparison.Ordinal);
    }

    private static string StripLineComments(string source)
    {
        var lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int idx = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (idx >= 0)
                lines[i] = lines[i][..idx];
        }
        return string.Join('\n', lines);
    }

    private static string ShaderRoot() =>
        Path.Combine(RepositoryRoot(), "src", "AcDream.App", "Rendering", "Shaders");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
