using System;
using System.Numerics;
using AcDream.Core.World;
using Xunit;

namespace AcDream.Core.Tests.World;

public sealed class WeatherSystemTests
{
    [Fact]
    public void Roll_Deterministic_ForSameDayIndex()
    {
        var a = new WeatherSystem();
        var b = new WeatherSystem();

        for (int d = 0; d < 100; d++)
        {
            a.Tick(0, d, 100f);  // big dt to finish any transition
            b.Tick(0, d, 100f);
            Assert.Equal(a.Kind, b.Kind);
        }
    }

    [Fact]
    public void Roll_WeightsDominatedByClear()
    {
        var sys = new WeatherSystem();
        int clear = 0;
        for (int d = 0; d < 1000; d++)
        {
            sys.Tick(0, d, 100f);
            if (sys.Kind == WeatherKind.Clear) clear++;
        }
        double frac = clear / 1000.0;
        Assert.InRange(frac, 0.45, 0.75);
    }

    [Fact]
    public void Snapshot_AlwaysPassesKeyframeFog_RegardlessOfKind()
    {
        var kf = SkyStateProvider.Default().Interpolate(0.5f);

        foreach (var kind in new[] {
            WeatherKind.Clear, WeatherKind.Overcast,
            WeatherKind.Rain, WeatherKind.Snow, WeatherKind.Storm,
        })
        {
            var sys = new WeatherSystem();
            sys.ForceWeather(kind);
            sys.Tick(0, 1, 100f);   // finalize any transition
            var snap = sys.Snapshot(in kf);

            Assert.Equal(kind, snap.Kind);
            Assert.Equal(kf.FogStart, snap.FogStart, precision: 2);
            Assert.Equal(kf.FogEnd,   snap.FogEnd,   precision: 2);
            Assert.Equal(kf.FogColor, snap.FogColor);
        }
    }


    [Fact]
    public void DisableDistanceFogSource_Unbound_LeavesKeyframeFogModeUnchanged()
    {
        var sys = new WeatherSystem();
        var kf = SkyStateProvider.Default().Interpolate(0.5f);

        var snap = sys.Snapshot(in kf);

        Assert.Equal(kf.FogMode, snap.FogMode);
    }

    [Fact]
    public void DisableDistanceFogSource_True_ForcesFogModeOff()
    {
        var sys = new WeatherSystem { DisableDistanceFogSource = () => true };
        var kf = SkyStateProvider.Default().Interpolate(0.5f);
        Assert.NotEqual(FogMode.Off, kf.FogMode); // sanity: the keyframe itself authors real fog

        var snap = sys.Snapshot(in kf);

        Assert.Equal(FogMode.Off, snap.FogMode);
        // Distances are left alone (the shader never reads them once off).
        Assert.Equal(kf.FogStart, snap.FogStart, precision: 2);
        Assert.Equal(kf.FogEnd, snap.FogEnd, precision: 2);
    }

    [Fact]
    public void DisableDistanceFogSource_False_LeavesFogModeAlone()
    {
        var sys = new WeatherSystem { DisableDistanceFogSource = () => false };
        var kf = SkyStateProvider.Default().Interpolate(0.5f);

        var snap = sys.Snapshot(in kf);

        Assert.Equal(kf.FogMode, snap.FogMode);
    }

    [Fact]
    public void EnvironOverride_ForcesTintedFog()
    {
        var sys = new WeatherSystem();
        sys.Override = EnvironOverride.RedFog;

        var kf = SkyStateProvider.Default().Interpolate(0.5f);
        var snap = sys.Snapshot(in kf);

        Assert.Equal(EnvironOverride.RedFog, snap.Override);
        Assert.True(snap.FogColor.X > snap.FogColor.Y);
        Assert.True(snap.FogColor.X > snap.FogColor.Z);
    }

    [Fact]
    public void Flash_DecaysOverTime()
    {
        var sys = new WeatherSystem();
        sys.TriggerFlash();

        var kf = SkyStateProvider.Default().Interpolate(0.5f);
        var imm = sys.Snapshot(in kf);
        Assert.True(imm.LightningFlash > 0.9f);

        // After 1 second the flash should be mostly decayed.
        sys.Tick(0, 0, 1.0f);
        var later = sys.Snapshot(in kf);
        Assert.True(later.LightningFlash < 0.1f,
            $"lightning flash didn't decay: {later.LightningFlash}");
    }

    [Fact]
    public void Snapshot_ClearKind_PassesThroughKeyframeFog()
    {
        var sys = new WeatherSystem();
        sys.ForceWeather(WeatherKind.Clear);
        sys.Tick(0, 0, 100f);  // finish transition

        var kf = SkyStateProvider.Default().Interpolate(0.5f);
        var snap = sys.Snapshot(in kf);

        Assert.Equal(kf.FogStart, snap.FogStart, precision: 2);
        Assert.Equal(kf.FogEnd,   snap.FogEnd,   precision: 2);
    }

    [Theory]
    [InlineData("Sunny",        WeatherKind.Clear)]
    [InlineData("SUNNY",        WeatherKind.Clear)]
    [InlineData("Clear",        WeatherKind.Clear)]
    [InlineData("",             WeatherKind.Clear)]
    [InlineData(null,           WeatherKind.Clear)]
    [InlineData("Cloudy",       WeatherKind.Overcast)]
    [InlineData("Overcast",     WeatherKind.Overcast)]
    [InlineData("Dark skies",   WeatherKind.Overcast)]
    [InlineData("Fog",          WeatherKind.Overcast)]
    [InlineData("Rainy",        WeatherKind.Overcast)]
    [InlineData("heavy rain",   WeatherKind.Overcast)]
    [InlineData("Snowy",        WeatherKind.Overcast)]
    [InlineData("Blizzard",     WeatherKind.Clear)]    // no matcher — default
    [InlineData("Stormy",       WeatherKind.Overcast)]
    [InlineData("Thunderstorm", WeatherKind.Overcast)]
    public void SetKindFromDayGroupName_MapsRetailNames(string? name, WeatherKind expected)
    {
        var sys = new WeatherSystem();
        sys.SetKindFromDayGroupName(name);
        sys.Tick(0, 0, 100f);  // finalize transition
        Assert.Equal(expected, sys.Kind);
    }

    [Fact]
    public void SetKindFromDayGroupName_DisablesInternalRoll()
    {
        // Once driven externally, advancing dayIndex must NOT re-roll
        // to a different kind via the internal RollKind hash.
        var sys = new WeatherSystem();
        sys.SetKindFromDayGroupName("Sunny");
        sys.Tick(0, 0, 100f);

        var clearKind = sys.Kind;
        Assert.Equal(WeatherKind.Clear, clearKind);

        for (int d = 1; d < 50; d++)
        {
            sys.Tick(0, d, 100f);
            Assert.Equal(clearKind, sys.Kind);  // stays put — no auto-roll
        }
    }
}
