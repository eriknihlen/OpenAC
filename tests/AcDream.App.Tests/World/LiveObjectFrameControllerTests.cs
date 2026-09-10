using AcDream.App.Settings;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Update;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.World;

public sealed class LiveObjectFrameControllerTests
{
    [Fact]
    public void SkyPesActivationBindingReleasesOnlyItsOwnGate()
    {
        var slot = new SkyPesActivationGateSlot();
        var first = new RecordingSkyPesActivationGate();
        var second = new RecordingSkyPesActivationGate();

        IDisposable firstLease = slot.BindOwned(first);
        slot.Tick();
        Assert.Equal(1, first.Calls);

        firstLease.Dispose();
        IDisposable secondLease = slot.BindOwned(second);
        firstLease.Dispose();
        slot.Tick();
        secondLease.Dispose();

        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
    }

    [Theory]
    [InlineData(ParticleRange.Retail, 1f)]
    [InlineData(
        ParticleRange.Extended,
        ParticleVisibilityController.ExtendedRangeMultiplier)]
    public void ParticleRange_UsesCurrentSettingsPreview(
        ParticleRange range,
        float expected)
    {
        var preview = new FakeSettingsPreview(
            DisplaySettings.Default with { ParticleRange = range });
        var source = new SettingsParticleRangeSource(preview);

        Assert.Equal(expected, source.RangeMultiplier);
    }

    [Fact]
    public void ParticleRange_UpdatesImmediatelyWhenPreviewChanges()
    {
        var preview = new FakeSettingsPreview(DisplaySettings.Default);
        var source = new SettingsParticleRangeSource(preview);

        preview.DisplayPreview = preview.DisplayPreview with
        {
            ParticleRange = ParticleRange.Retail,
        };
        Assert.Equal(1f, source.RangeMultiplier);

        preview.DisplayPreview = preview.DisplayPreview with
        {
            ParticleRange = ParticleRange.Extended,
        };
        Assert.Equal(
            ParticleVisibilityController.ExtendedRangeMultiplier,
            source.RangeMultiplier);
    }

    private sealed class FakeSettingsPreview(DisplaySettings display)
        : IRuntimeSettingsPreviewSource
    {
        public bool HasDraftPreview => true;

        public DisplaySettings DisplayPreview { get; set; } = display;

        public AudioSettings AudioPreview => AudioSettings.Default;
    }

    private sealed class RecordingSkyPesActivationGate : ISkyPesActivationGate
    {
        public int Calls { get; private set; }

        public void Tick() => Calls++;
    }
}
