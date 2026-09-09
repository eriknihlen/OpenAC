using System;
using System.Diagnostics;
using System.IO;
using AcDream.App.Rendering.Sky;
using Xunit;

namespace AcDream.App.Tests.Rendering.Sky;

public sealed class RainAnimationClockTests
{
    [Fact]
    public void AnimationPhaseUsesMonotonicStopwatchTicks()
    {
        const long start = 1234;

        Assert.Equal(
            1f,
            SkyRenderer.ElapsedAnimationSeconds(start, start + Stopwatch.Frequency));
    }

    [Fact]
    public void RendererDoesNotReadTheAdjustableSystemClock()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "AcDream.App", "Rendering", "Sky", "SkyRenderer.cs"));

        Assert.Contains("Stopwatch.GetTimestamp()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime.UtcNow", source, StringComparison.Ordinal);
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
