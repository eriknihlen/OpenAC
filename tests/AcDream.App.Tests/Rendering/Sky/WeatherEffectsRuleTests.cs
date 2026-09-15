using System;
using System.IO;
using Xunit;

namespace AcDream.App.Tests.Rendering.Sky;

public sealed class WeatherEffectsRuleTests
{
    [Fact]
    public void SkyRendererSkipsWeatherObjectsInBothPassesWhenTheOptionIsOn()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "AcDream.App", "Rendering", "Sky", "SkyRenderer.cs"));
        string code = StripLineComments(source);

        Assert.Contains(
            "internal Func<bool>? DisableMostWeatherEffects { get; set; }",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (obj.IsWeather && (DisableMostWeatherEffects?.Invoke() ?? false)) continue;",
            code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompositionGatesWeatherEffectsOnTheCharacterOption()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "AcDream.App", "Composition", "FrameRootComposition.cs"));
        string code = StripLineComments(source);

        Assert.Contains("DisableMostWeatherEffects = () =>", code, StringComparison.Ordinal);
        Assert.Contains(
            "CharacterOptionId.DisableMostWeatherEffects",
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

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
