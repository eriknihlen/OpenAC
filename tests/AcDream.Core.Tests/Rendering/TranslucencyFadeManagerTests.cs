using AcDream.Core.Rendering;
using Xunit;

namespace AcDream.Core.Tests.Rendering;

public sealed class TranslucencyFadeManagerTests
{
    [Fact]
    public void StartPartFade_InstantEpsilon_CommitsEndImmediately()
    {
        var mgr = new TranslucencyFadeManager();

        mgr.StartPartFade(entityId: 1, partIndex: 0, start: 0f, end: 1f,
            time: TranslucencyFadeManager.TranslucencyInstantEpsilon);

        Assert.True(mgr.TryGetCurrentValue(1, 0, out float value));
        Assert.Equal(1f, value);

        mgr.AdvanceAll(10f);
        Assert.True(mgr.TryGetCurrentValue(1, 0, out float after));
        Assert.Equal(1f, after);
    }

    [Fact]
    public void StartPartFade_ZeroTime_IsTreatedAsInstant()
    {
        var mgr = new TranslucencyFadeManager();
        mgr.StartPartFade(entityId: 5, partIndex: 2, start: 0f, end: 0.5f, time: 0f);

        Assert.True(mgr.TryGetCurrentValue(5, 2, out float value));
        Assert.Equal(0.5f, value);
    }

    [Fact]
    public void StartPartFade_RealDuration_ImmediatelyReadableAsStart()
    {
        var mgr = new TranslucencyFadeManager();
        mgr.StartPartFade(entityId: 1, partIndex: 0, start: 0.2f, end: 0.8f, time: 1f);

        Assert.True(mgr.TryGetCurrentValue(1, 0, out float value));
        Assert.Equal(0.2f, value);
    }

    [Fact]
    public void AdvanceAll_LinearInterpolation_MidpointIsExact()
    {
        var mgr = new TranslucencyFadeManager();
        mgr.StartPartFade(entityId: 1, partIndex: 0, start: 0f, end: 1f, time: 1f);

        mgr.AdvanceAll(0.5f);

        Assert.True(mgr.TryGetCurrentValue(1, 0, out float value));
        Assert.Equal(0.5f, value, 5);
    }

    [Fact]
    public void AdvanceAll_AsymmetricRange_LinearAtQuarterDuration()
    {
        var mgr = new TranslucencyFadeManager();
        mgr.StartPartFade(entityId: 1, partIndex: 0, start: 0.2f, end: 1.0f, time: 4f);

        mgr.AdvanceAll(1f); // t = 0.25

        Assert.True(mgr.TryGetCurrentValue(1, 0, out float value));
        Assert.Equal(0.2f + (1.0f - 0.2f) * 0.25f, value, 5);
    }

    [Fact]
    public void AdvanceAll_Overshoot_CommitsExactEndValue()
    {
        var mgr = new TranslucencyFadeManager();
        mgr.StartPartFade(entityId: 1, partIndex: 0, start: 0f, end: 1f, time: 0.267f);

        mgr.AdvanceAll(10f); // massive overshoot past duration

        Assert.True(mgr.TryGetCurrentValue(1, 0, out float value));
        Assert.Equal(1f, value); // bitwise-exact, not approximately-equal
    }

    [Fact]
    public void AdvanceAll_PastCompletion_IsIdempotent()
    {
        var mgr = new TranslucencyFadeManager();
        mgr.StartPartFade(entityId: 1, partIndex: 0, start: 0f, end: 1f, time: 0.1f);

        mgr.AdvanceAll(1f);
        Assert.True(mgr.TryGetCurrentValue(1, 0, out float first));

        mgr.AdvanceAll(1f);
        Assert.True(mgr.TryGetCurrentValue(1, 0, out float second));

        Assert.Equal(first, second);
        Assert.Equal(1f, second);
    }

    [Fact]
    public void ClearEntity_RemovesActiveAndCommittedState()
    {
        var mgr = new TranslucencyFadeManager();
        mgr.StartPartFade(entityId: 1, partIndex: 0, start: 0f, end: 1f, time: 1f);
        Assert.True(mgr.TryGetCurrentValue(1, 0, out _));

        mgr.ClearEntity(1);

        Assert.False(mgr.TryGetCurrentValue(1, 0, out _));
        mgr.AdvanceAll(1f); // must not throw even though the entity is gone
        Assert.False(mgr.TryGetCurrentValue(1, 0, out _));
    }

    [Fact]
    public void StartPartFade_SecondCallReplacesFirst()
    {
        var mgr = new TranslucencyFadeManager();
        mgr.StartPartFade(entityId: 1, partIndex: 0, start: 0f, end: 1f, time: 10f);
        mgr.AdvanceAll(1f); // 10% into the first (abandoned) ramp

        // Restart with a fresh, shorter ramp — the old target must be abandoned.
        mgr.StartPartFade(entityId: 1, partIndex: 0, start: 0.9f, end: 0f, time: 1f);
        Assert.True(mgr.TryGetCurrentValue(1, 0, out float atRestart));
        Assert.Equal(0.9f, atRestart);

        mgr.AdvanceAll(1f); // finishes the SECOND ramp, not the first
        Assert.True(mgr.TryGetCurrentValue(1, 0, out float value));
        Assert.Equal(0f, value);
    }

    [Fact]
    public void TryGetCurrentValue_UnknownPart_ReturnsFalse()
    {
        var mgr = new TranslucencyFadeManager();
        Assert.False(mgr.TryGetCurrentValue(entityId: 999, partIndex: 0, out float value));
        Assert.Equal(0f, value);
    }

    [Fact]
    public void MultiplePartsOnSameEntity_AdvanceIndependently()
    {
        var mgr = new TranslucencyFadeManager();
        mgr.StartPartFade(entityId: 1, partIndex: 0, start: 0f, end: 1f, time: 1f);
        mgr.StartPartFade(entityId: 1, partIndex: 1, start: 0f, end: 1f, time: 2f);

        mgr.AdvanceAll(1f);

        Assert.True(mgr.TryGetCurrentValue(1, 0, out float part0));
        Assert.True(mgr.TryGetCurrentValue(1, 1, out float part1));
        Assert.Equal(1f, part0);   // part 0's 1s ramp is done
        Assert.Equal(0.5f, part1, 5); // part 1's 2s ramp is halfway
    }

    [Fact]
    public void Revision_AdvancesOnlyWhenCommittedCasterOpacityChanges()
    {
        var mgr = new TranslucencyFadeManager();
        ulong initial = mgr.Revision;

        mgr.StartPartFade(1, 0, start: 0f, end: 1f, time: 1f);
        ulong started = mgr.Revision;
        Assert.True(started > initial);

        mgr.AdvanceAll(0f);
        Assert.Equal(started, mgr.Revision);
        mgr.AdvanceAll(0.5f);
        Assert.True(mgr.Revision > started);

        ulong advanced = mgr.Revision;
        mgr.ClearEntity(999);
        Assert.Equal(advanced, mgr.Revision);
        mgr.ClearEntity(1);
        Assert.True(mgr.Revision > advanced);
    }
}
