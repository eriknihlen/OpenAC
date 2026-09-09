using AcDream.App.Rendering;
using AcDream.App.Streaming;

namespace AcDream.App.Tests.Rendering;

public sealed class DevToolsRuntimeSourcesTests
{
    [Fact]
    public void FrameDiagnosticsLateBindingPreservesSeedAndExpectedOwnerRelease()
    {
        var source = new DeferredRenderFrameDiagnosticsSource();
        var first = new FrameSource(144, 6.9);
        var other = new FrameSource(30, 33.3);

        Assert.Equal(RenderFrameDiagnosticsSnapshot.Initial, source.Snapshot);
        source.Bind(first);
        Assert.Equal(144, source.Snapshot.Fps);
        source.Unbind(other);
        Assert.Equal(144, source.Snapshot.Fps);
        source.Unbind(first);
        Assert.Equal(RenderFrameDiagnosticsSnapshot.Initial, source.Snapshot);

        source.Bind(other);
        source.Deactivate();
        Assert.Equal(RenderFrameDiagnosticsSnapshot.Initial, source.Snapshot);
        Assert.Throws<ObjectDisposedException>(() => source.Bind(first));
    }

    [Fact]
    public void FrameDiagnosticsRejectsACompetingCanonicalOwner()
    {
        var source = new DeferredRenderFrameDiagnosticsSource();
        source.Bind(new FrameSource(60, 16.7));

        Assert.Throws<InvalidOperationException>(() =>
            source.Bind(new FrameSource(120, 8.3)));
    }

    [Fact]
    public void PlayerModeCommandSlotUsesLiveTargetAndRejectsStaleRelease()
    {
        var source = new DeferredDevToolsPlayerModeCommands();
        var first = new PlayerModeTarget();
        var other = new PlayerModeTarget();

        source.ToggleFlyOrChase();
        source.Bind(first);
        source.ToggleFlyOrChase();
        source.Unbind(other);
        source.ToggleFlyOrChase();
        source.Unbind(first);
        source.ToggleFlyOrChase();

        Assert.Equal(2, first.ToggleCalls);
        Assert.Equal(0, other.ToggleCalls);

        source.Bind(other);
        source.Deactivate();
        source.ToggleFlyOrChase();
        Assert.Equal(0, other.ToggleCalls);
        Assert.Throws<ObjectDisposedException>(() => source.Bind(first));
    }

    [Fact]
    public void PlayerModeOwnedBindingReleasesExactlyAndAllowsRebind()
    {
        var source = new DeferredDevToolsPlayerModeCommands();
        var first = new PlayerModeTarget();
        var second = new PlayerModeTarget();

        IDisposable firstBinding = source.BindOwned(first);
        Assert.Throws<InvalidOperationException>(() => source.BindOwned(second));

        firstBinding.Dispose();
        using IDisposable secondBinding = source.BindOwned(second);
        firstBinding.Dispose();
        source.ToggleFlyOrChase();

        Assert.Equal(0, first.ToggleCalls);
        Assert.Equal(1, second.ToggleCalls);
    }

    [Fact]
    public void WorldCountSlotCannotFreezeOrReleaseTheWrongWorld()
    {
        var source = new DeferredCanonicalWorldEntityCountSource();
        var first = new GpuWorldState();
        var other = new GpuWorldState();

        source.Bind(first);
        source.Unbind(other);
        Assert.Throws<InvalidOperationException>(() => source.Bind(other));
        source.Unbind(first);
        source.Bind(other);
        source.Deactivate();

        Assert.Equal(0, source.EntityCount);
        Assert.Throws<ObjectDisposedException>(() => source.Bind(first));
    }

    private sealed class FrameSource(double fps, double milliseconds)
        : IRenderFrameDiagnosticsSnapshotSource
    {
        public RenderFrameDiagnosticsSnapshot Snapshot { get; } =
            RenderFrameDiagnosticsSnapshot.Initial with
            {
                Fps = fps,
                FrameMilliseconds = milliseconds,
            };
    }

    private sealed class PlayerModeTarget : IDevToolsPlayerModeTarget
    {
        public int ToggleCalls { get; private set; }
        public void ToggleFlyOrChase() => ToggleCalls++;
    }
}
