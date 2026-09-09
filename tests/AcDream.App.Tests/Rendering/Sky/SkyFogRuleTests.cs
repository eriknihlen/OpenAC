using System;
using System.IO;
using Xunit;

namespace AcDream.App.Tests.Rendering.Sky;

public sealed class SkyFogRuleTests
{
    [Fact]
    public void SkyFragmentShaderFogsTheDomeOnlyThroughUApplyFogAndWithoutAFloor()
    {
        string frag = File.ReadAllText(Path.Combine(ShaderRoot(), "sky.frag"));
        string code = StripLineComments(frag);

        Assert.DoesNotContain("SKY_FOG_FLOOR", code, StringComparison.Ordinal);
        Assert.DoesNotContain("max(vFogFactor", code, StringComparison.Ordinal);
        Assert.Contains("if (uApplyFog > 0.5 && fogMode != 0)", code, StringComparison.Ordinal);
        Assert.Contains("rgb = mix(uFogColor.rgb, rgb, vFogFactor);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SkyRendererAppliesFogOnlyUnderAnAdminEnvironsOverrideAndNeverOnAdditiveLayers()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "AcDream.App", "Rendering", "Sky", "SkyRenderer.cs"));
        string code = StripLineComments(source);

        Assert.Contains(
            "_params.ApplyFog = environOverrideActive && !sub.DisableFog ? 1f : 0f;",
            code,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_params.ApplyFog = sub.DisableFog ? 0f : 1f;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void WorldFrameBuilderLeavesTheAuthoredFogRangeAlone()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "AcDream.App", "Rendering", "WorldRenderFrameBuilder.cs"));
        string code = StripLineComments(source);

        // SceneLightingUbo.Build writes FogParams.xy from AtmosphereSnapshot
        // (the keyframe's authored range, SceneLightingUboTests pins that);
        // nothing downstream may overwrite them from the streaming window.
        Assert.DoesNotContain("ubo.FogParams = new Vector4(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("FogEndMultiplier", code, StringComparison.Ordinal);
        Assert.DoesNotContain("FogStartMultiplier", code, StringComparison.Ordinal);
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
