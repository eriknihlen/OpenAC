using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class JumperTests
{
    [Theory]
    [InlineData("jump", new string[0], false, false, true, float.NaN, 0)]
    [InlineData("jumpw", new[] { "500" }, true, false, true, float.NaN, 500)]
    [InlineData("jumpws", new[] { "90", "1000" }, true, false, false, 90f, 1000)]
    [InlineData("jumpz", new[] { "180", "1500" }, false, true, true, 180f, 1000)]
    public void ParsesTheUtilityBeltVerb(string verb, string[] arguments, bool forward, bool strafeLeft, bool run, float heading, int milliseconds)
    {
        Assert.True(Jumper.TryParse(verb, arguments, out PluginMovementIntent keys, out float parsedHeading, out int parsedMs, out _));
        Assert.Equal(forward, keys.Forward);
        Assert.Equal(strafeLeft, keys.StrafeLeft);
        Assert.Equal(run, keys.Run);
        Assert.True(keys.Jump);
        Assert.Equal(heading, parsedHeading);
        Assert.Equal(milliseconds, parsedMs);
    }

    [Fact]
    public void RejectsUnknownKeysAndNonNumbers()
    {
        Assert.False(Jumper.TryParse("jumpq", [], out _, out _, out _, out string error));
        Assert.Contains("q", error);
        Assert.False(Jumper.TryParse("jump", ["north"], out _, out _, out _, out _));
    }

    [Fact]
    public void FacesThenChargesThenReleasesAndTakesTheEngineOverMeanwhile()
    {
        var surface = new FakeAutomationSurface();
        var engine = new BotEngine(surface, new FakeLogger(), []);
        surface.Position = surface.Position with { HeadingDegrees = 0f };

        Assert.True(Jumper.TryParse("jumpw", ["90", "300"], out PluginMovementIntent keys, out float heading, out int ms, out _));
        engine.Jumper.Start(keys, heading, ms, engine.Clock.Now);

        engine.Tick(0.05d);
        Assert.Equal(["face:90"], surface.Commands); // the fake turns at once
        engine.Tick(0.05d);
        engine.Tick(0.2d);
        Assert.Equal("move:forward+jump", surface.Commands[^1]);
        Assert.Equal("jumping", engine.LastReason);

        engine.Tick(0.2d);
        Assert.Equal("move:forward+jump", surface.Commands[^1]); // still charging
        engine.Tick(0.2d);
        Assert.Equal("move:clear", surface.Commands[^1]);

        engine.Tick(0.5d);
        Assert.False(engine.Jumper.IsBusy);
    }
}
