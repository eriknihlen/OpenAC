using System.Numerics;
using AcDream.Core.Rendering;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Rendering;

public sealed class TranslucencyHookSinkTests
{
    [Fact]
    public void TransparentPartHook_ForwardsFieldsVerbatimToManager()
    {
        var mgr = new TranslucencyFadeManager();
        var sink = new TranslucencyHookSink(mgr);

        var hook = new TransparentPartHook { PartIndex = 3, Start = 0.1f, End = 0.9f, Time = 0.5f };
        sink.OnHook(entityId: 42, entityWorldPosition: Vector3.Zero, hook: hook);

        Assert.True(mgr.TryGetCurrentValue(42, 3, out float value));
        Assert.Equal(0.1f, value); // t=0 value, readable immediately after the hook fires
    }

    [Fact]
    public void TransparentPartHook_InstantTime_CommitsEndImmediately()
    {
        var mgr = new TranslucencyFadeManager();
        var sink = new TranslucencyHookSink(mgr);

        var hook = new TransparentPartHook { PartIndex = 0, Start = 0f, End = 1f, Time = 0f };
        sink.OnHook(entityId: 1, entityWorldPosition: Vector3.Zero, hook: hook);

        Assert.True(mgr.TryGetCurrentValue(1, 0, out float value));
        Assert.Equal(1f, value);
    }

    [Fact]
    public void EtherealHook_IsIgnored()
    {
        var mgr = new TranslucencyFadeManager();
        var sink = new TranslucencyHookSink(mgr);

        sink.OnHook(entityId: 1, entityWorldPosition: Vector3.Zero, hook: new EtherealHook { Ethereal = true });

        Assert.False(mgr.TryGetCurrentValue(1, 0, out _));
    }

    [Fact]
    public void WholeObjectTransparentHook_IsIgnored()
    {
        var mgr = new TranslucencyFadeManager();
        var sink = new TranslucencyHookSink(mgr);

        sink.OnHook(entityId: 1, entityWorldPosition: Vector3.Zero,
            hook: new TransparentHook { Start = 0f, End = 1f, Time = 1f });

        Assert.False(mgr.TryGetCurrentValue(1, 0, out _));
    }

    [Fact]
    public void UnrelatedHook_IsIgnored()
    {
        var mgr = new TranslucencyFadeManager();
        var sink = new TranslucencyHookSink(mgr);

        sink.OnHook(entityId: 1, entityWorldPosition: Vector3.Zero, hook: new SoundTableHook());

        Assert.False(mgr.TryGetCurrentValue(1, 0, out _));
    }
}
