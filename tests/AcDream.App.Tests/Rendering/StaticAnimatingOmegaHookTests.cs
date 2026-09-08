using System.Numerics;
using AcDream.App.Rendering;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Rendering;

/// <summary>
/// The SetOmega hook is what moves AC's ambient flyers; ignoring it left the
/// birds and butterflies animating in place.
/// </summary>
public sealed class StaticAnimatingOmegaHookTests
{
    private static SetOmegaHook Omega(float x, float y, float z)
        => new() { Axis = new Vector3(x, y, z) };

    [Fact]
    public void NoHooksLeavesOmegaUntouched()
    {
        Assert.False(
            RetailStaticAnimatingObjectScheduler.TryResolveOmega([], out _));
    }

    [Fact]
    public void AHookBatchWithoutSetOmegaLeavesOmegaUntouched()
    {
        Assert.False(
            RetailStaticAnimatingObjectScheduler.TryResolveOmega(
                [new SoundTableHook()], out _));
    }

    [Fact]
    public void SetOmegaIsTakenVerbatim()
    {
        Assert.True(
            RetailStaticAnimatingObjectScheduler.TryResolveOmega(
                [Omega(0f, 0f, -0.027f)], out Vector3 omega));

        Assert.Equal(new Vector3(0f, 0f, -0.027f), omega);
    }

    [Fact]
    public void TheLastSetOmegaInABatchWins()
    {
        Assert.True(
            RetailStaticAnimatingObjectScheduler.TryResolveOmega(
                [Omega(0f, 0f, -0.02f), Omega(0f, 0f, 0.05f)],
                out Vector3 omega));
        Assert.Equal(new Vector3(0f, 0f, 0.05f), omega);
    }
}
