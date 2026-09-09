using AcDream.App.Rendering.Selection;

namespace AcDream.App.Tests.Rendering;

public sealed class RetailSelectionLightingPulseTests
{
    [Fact]
    public void ClickRunsRetailHighLowHighLowRestoreCadence()
    {
        double now = 10d;
        var pulse = new RetailSelectionLightingPulse(() => now);

        pulse.Start(0x5000_1234u, 1234u);
        AssertLighting(pulse, 0x5000_1234u, 1234u, RetailSelectionLighting.High);

        now = 10.199;
        pulse.Tick();
        AssertLighting(pulse, 0x5000_1234u, 1234u, RetailSelectionLighting.High);

        now = 10.2;
        pulse.Tick();
        AssertLighting(pulse, 0x5000_1234u, 1234u, RetailSelectionLighting.Low);

        now = 10.4;
        pulse.Tick();
        AssertLighting(pulse, 0x5000_1234u, 1234u, RetailSelectionLighting.High);

        now = 10.6;
        pulse.Tick();
        AssertLighting(pulse, 0x5000_1234u, 1234u, RetailSelectionLighting.Low);

        now = 10.8;
        pulse.Tick();
        Assert.False(pulse.TryGet(0x5000_1234u, 1234u, out _));
    }

    [Fact]
    public void LongFrameAdvancesOnlyOneRetailGlobalLoopFlip()
    {
        double now = 1d;
        var pulse = new RetailSelectionLightingPulse(() => now);
        pulse.Start(0x5000_1234u, 1234u);

        now = 20d;
        pulse.Tick();

        AssertLighting(pulse, 0x5000_1234u, 1234u, RetailSelectionLighting.Low);
    }

    [Fact]
    public void NewClickRestoresOldTargetAndRestartsHighOnNewTarget()
    {
        double now = 4d;
        var pulse = new RetailSelectionLightingPulse(() => now);
        pulse.Start(0x5000_0001u, 1u);

        now = 4.2;
        pulse.Tick();
        AssertLighting(pulse, 0x5000_0001u, 1u, RetailSelectionLighting.Low);

        pulse.Start(0x5000_0002u, 2u);

        Assert.False(pulse.TryGet(0x5000_0001u, 1u, out _));
        AssertLighting(pulse, 0x5000_0002u, 2u, RetailSelectionLighting.High);
    }

    [Fact]
    public void GuidReuseDoesNotTransferPulseToReplacementIncarnation()
    {
        var pulse = new RetailSelectionLightingPulse(() => 1d);
        pulse.Start(0x5000_0001u, 10u);

        Assert.False(pulse.TryGet(0x5000_0001u, 11u, out _));
        AssertLighting(pulse, 0x5000_0001u, 10u, RetailSelectionLighting.High);
    }

    private static void AssertLighting(
        RetailSelectionLightingPulse pulse,
        uint serverGuid,
        uint localEntityId,
        RetailSelectionLighting expected)
    {
        Assert.True(pulse.TryGet(serverGuid, localEntityId, out var actual));
        Assert.Equal(expected, actual);
    }
}
