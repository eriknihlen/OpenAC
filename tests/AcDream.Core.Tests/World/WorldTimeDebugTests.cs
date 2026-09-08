using System;
using AcDream.Core.World;
using Xunit;

namespace AcDream.Core.Tests.World;

[Collection(DerethDateTimeCollection.Name)]
public sealed class WorldTimeDebugTests
{
    [Fact]
    public void SetDebugTime_OverridesDayFraction()
    {
        var service = new WorldTimeService(SkyStateProvider.Default());
        service.SyncFromServer(0);  // server tick 0 (= Morntide-and-Half)

        service.SetDebugTime(0.5f);   // force noon (Midsong-and-Half)
        Assert.InRange(service.DayFraction, 0.499, 0.501);
    }

    [Fact]
    public void ClearDebugTime_RestoresServerTime()
    {
        // Post tick-0-offset fix: DayFraction(tick) = ((tick + 7/16 * DayTicks) % DayTicks) / DayTicks.
        // Pick a server tick whose real-world meaning is straightforward to verify.
        // Sync to (0.25 - 7/16) * DayTicks negative means "3 slots before midnight
        // past Morntide-and-Half", which in positive terms is 13/16 of the day
        // past Morntide-and-Half, but simpler: sync to "1/16 past midnight" =
        // ticks giving fraction 1/16. Required tick offset from 0 to land at
        // fraction 1/16: solve (t + 7/16*D) mod D = 1/16*D
        //                 → t = (1/16 - 7/16) * D mod D = -6/16 * D mod D = 10/16 * D.
        double targetFraction = 1.0 / 16.0;  // Darktide-and-Half
        var service = new WorldTimeService(SkyStateProvider.Default());
        double syncTick = targetFraction * DerethDateTime.DayTicks
            - service.Calendar.OriginOffsetTicks;
        while (syncTick < 0) syncTick += DerethDateTime.DayTicks;

        service.SyncFromServer(syncTick);
        service.SetDebugTime(0.5f);
        service.ClearDebugTime();

        Assert.InRange(service.DayFraction, targetFraction - 0.01, targetFraction + 0.01);
    }

    [Fact]
    public void SyncFromServer_ClearsDebugOverride()
    {
        var service = new WorldTimeService(SkyStateProvider.Default());
        service.SetDebugTime(0.75f);
        service.SyncFromServer(0);  // tick 0 = Morntide-and-Half → fraction 7/16

        Assert.InRange(service.DayFraction, 7.0 / 16.0 - 0.01, 7.0 / 16.0 + 0.01);
    }

    [Fact]
    public void PinnedDayFraction_SurvivesServerSyncAndOutranksTheDebugOverride()
    {
        var service = new WorldTimeService(SkyStateProvider.Default())
        {
            PinnedDayFraction = 0.5f,
        };

        service.SyncFromServer(0);                 // tick 0 = fraction 7/16
        Assert.InRange(service.DayFraction, 0.499, 0.501);

        service.SetDebugTime(0.75f);
        Assert.InRange(service.DayFraction, 0.499, 0.501);

        service.SyncFromServer(DerethDateTime.DayTicks / 4.0);
        Assert.InRange(service.DayFraction, 0.499, 0.501);
    }

    [Fact]
    public void PinnedDayFraction_UnsetLeavesTheServerClockAlone()
    {
        var service = new WorldTimeService(SkyStateProvider.Default());
        service.SyncFromServer(0);
        Assert.Null(service.PinnedDayFraction);
        Assert.InRange(service.DayFraction, 7.0 / 16.0 - 0.01, 7.0 / 16.0 + 0.01);

        service.PinnedDayFraction = 0.125f;
        Assert.InRange(service.DayFraction, 0.124, 0.126);

        service.PinnedDayFraction = null;
        Assert.InRange(service.DayFraction, 7.0 / 16.0 - 0.01, 7.0 / 16.0 + 0.01);
    }

    [Fact]
    public void SetProvider_AcceptsNewKeyframes()
    {
        var service = new WorldTimeService(SkyStateProvider.Default());
        var custom = new SkyStateProvider(new[]
        {
            new SkyKeyframe(
                Begin: 0f,
                SunHeadingDeg: 0f,
                SunPitchDeg: 90f,
                DirColor: System.Numerics.Vector3.One,
                DirBright: 1f,
                AmbColor: System.Numerics.Vector3.One,
                AmbBright: 1f,
                FogColor: System.Numerics.Vector3.Zero,
                FogDensity: 0f),
        });
        service.SetProvider(custom);
        Assert.Equal(1, custom.KeyframeCount);
    }
}
