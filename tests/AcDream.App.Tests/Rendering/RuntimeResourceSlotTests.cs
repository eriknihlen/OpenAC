using System.Globalization;
using AcDream.App.Rendering;
using AcDream.App.Update;

namespace AcDream.App.Tests.Rendering;

public sealed class RuntimeResourceSlotTests
{
    [Fact]
    public void OwnedSlotPublishesOnceAndRetriesOnlyFailedRelease()
    {
        var slot = new OwnedResourceSlot<RetryableResource>();
        var resource = new RetryableResource(remainingFailures: 1);

        Assert.Same(resource, slot.Acquire(() => resource));
        Assert.Same(resource, slot.Borrow());
        Assert.Throws<InvalidOperationException>(() =>
            slot.Acquire(() => new RetryableResource()));

        Assert.Throws<InvalidOperationException>(slot.Release);
        Assert.True(slot.HasResource);

        slot.Release();
        slot.Release();

        Assert.False(slot.HasResource);
        Assert.Equal(2, resource.DisposeCalls);
    }

    [Fact]
    public void TransferSlotKeepsFallbackUntilCompleteDestinationExists()
    {
        var slot = new TransferableResourceSlot<RetryableResource>();
        var resource = slot.Acquire(() => new RetryableResource());

        Assert.Throws<InvalidOperationException>(() =>
            slot.Transfer<object>(_ => throw new InvalidOperationException("destination failed")));
        Assert.True(slot.HasFallback);
        Assert.Same(resource, slot.Borrow());

        var owner = slot.Transfer(value => new ResourceOwner(value));

        Assert.False(slot.HasFallback);
        slot.ReleaseFallback();
        Assert.Equal(0, resource.DisposeCalls);

        owner.Dispose();
        Assert.Equal(1, resource.DisposeCalls);
    }

    [Fact]
    public void TransferSlotReleasesOnlyFallbackOnPartialShutdown()
    {
        var fallback = new TransferableResourceSlot<RetryableResource>();
        RetryableResource fallbackResource = fallback.Acquire(
            () => new RetryableResource());

        fallback.ReleaseFallback();
        fallback.ReleaseFallback();

        Assert.Equal(1, fallbackResource.DisposeCalls);

        var transferred = new TransferableResourceSlot<RetryableResource>();
        RetryableResource transferredResource = transferred.Acquire(
            () => new RetryableResource());
        ResourceOwner owner = transferred.Transfer(value => new ResourceOwner(value));

        owner.Dispose();
        transferred.ReleaseFallback();

        Assert.Equal(1, transferredResource.DisposeCalls);
    }

    [Fact]
    public void TransferSlotCoversAcquirePrepareAndPostTransferBindingPrefixes()
    {
        var acquisition = new TransferableResourceSlot<RetryableResource>();
        Assert.Throws<InvalidOperationException>(() =>
            acquisition.Acquire(() => throw new InvalidOperationException("create failed")));
        Assert.False(acquisition.HasFallback);

        var preparation = new TransferableResourceSlot<RetryableResource>();
        var prepared = new RetryableResource(remainingPrepareFailures: 1);
        Assert.Throws<InvalidOperationException>(() =>
            preparation.AcquirePrepared(() => prepared, static value => value.Prepare()));
        Assert.True(preparation.HasFallback);
        Assert.Throws<InvalidOperationException>(() =>
            preparation.Transfer(value => new ResourceOwner(value)));
        Assert.Same(
            prepared,
            preparation.AcquirePrepared(
                () => throw new InvalidOperationException("factory replayed"),
                static value => value.Prepare()));
        Assert.Equal(2, prepared.PrepareCalls);
        preparation.ReleaseFallback();
        Assert.Equal(1, prepared.DisposeCalls);

        var binding = new TransferableResourceSlot<RetryableResource>();
        RetryableResource transferred = binding.Acquire(() => new RetryableResource());
        ResourceOwner? owner = null;
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            owner = binding.Transfer(value => new ResourceOwner(value));
            throw new InvalidOperationException("binding failed");
        }));
        Assert.False(binding.HasFallback);
        Assert.NotNull(owner);
        owner.Dispose();
        binding.ReleaseFallback();
        Assert.Equal(1, transferred.DisposeCalls);
    }

    [Fact]
    public void FrameGraphsPublishAtomicallyRouteAndBecomeSilentAfterWithdrawal()
    {
        var calls = new List<string>();
        var slot = new GameFrameGraphSlot();

        Assert.False(slot.Tick(new UpdateFrameInput(0.25)));
        Assert.False(slot.Render(new RenderFrameInput(0.5, 800, 600), out _));

        var update = new RecordingUpdateRoot(calls);
        var render = new RecordingRenderRoot(calls);
        slot.Publish(update, render);

        Assert.True(slot.IsPublished);
        Assert.Throws<InvalidOperationException>(() =>
            slot.Publish(new RecordingUpdateRoot(calls), new RecordingRenderRoot(calls)));
        Assert.True(slot.Tick(new UpdateFrameInput(0.25)));
        Assert.True(slot.Render(new RenderFrameInput(0.5, 800, 600), out _));
        Assert.Equal(["update:0.25", "render:0.5:800:600"], calls);

        slot.Withdraw();
        slot.Withdraw();

        Assert.False(slot.IsPublished);
        Assert.False(slot.Tick(new UpdateFrameInput(1)));
        Assert.False(slot.Render(new RenderFrameInput(1, 1, 1), out _));
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void WithdrawnFrameSlotAcceptsACompleteReplacementPair()
    {
        var calls = new List<string>();
        var slot = new GameFrameGraphSlot();
        slot.Publish(new RecordingUpdateRoot(calls), new RecordingRenderRoot(calls));
        slot.Withdraw();

        slot.Publish(new RecordingUpdateRoot(calls), new RecordingRenderRoot(calls));

        Assert.True(slot.Tick(new UpdateFrameInput(2)));
        Assert.Equal(["update:2"], calls);
    }

    [Fact]
    public void OwnedFramePublicationWithdrawsOnlyItsExactPair()
    {
        var calls = new List<string>();
        var slot = new GameFrameGraphSlot();
        IDisposable stale = slot.PublishOwned(
            new RecordingUpdateRoot(calls),
            new RecordingRenderRoot(calls));

        stale.Dispose();
        IDisposable current = slot.PublishOwned(
            new RecordingUpdateRoot(calls),
            new RecordingRenderRoot(calls));
        stale.Dispose();

        Assert.True(slot.Tick(new UpdateFrameInput(3)));
        current.Dispose();
        current.Dispose();
        Assert.False(slot.Tick(new UpdateFrameInput(4)));
        Assert.Equal(["update:3"], calls);
    }

    [Fact]
    public void OwnedSlotRejectsReentrantAcquireWithoutLosingOuterResource()
    {
        var slot = new OwnedResourceSlot<RetryableResource>();
        var outer = new RetryableResource();
        int innerFactories = 0;

        Assert.Same(outer, slot.Acquire(() =>
        {
            Assert.Throws<InvalidOperationException>(() => slot.Acquire(() =>
            {
                innerFactories++;
                return new RetryableResource();
            }));
            return outer;
        }));

        Assert.Equal(0, innerFactories);
        Assert.Same(outer, slot.Borrow());
        slot.Release();
        Assert.Equal(1, outer.DisposeCalls);
    }

    [Fact]
    public void OwnedSlotRejectsReentrantReleaseWithoutDoubleDisposal()
    {
        var slot = new OwnedResourceSlot<CallbackResource>();
        var resource = new CallbackResource(() =>
            Assert.Throws<InvalidOperationException>(slot.Release));
        slot.Acquire(() => resource);

        slot.Release();

        Assert.Equal(1, resource.DisposeCalls);
        Assert.False(slot.HasResource);
    }

    [Fact]
    public void TransferSlotRejectsNestedTransferAndPreservesOuterTransfer()
    {
        var slot = new TransferableResourceSlot<RetryableResource>();
        var resource = slot.Acquire(() => new RetryableResource());
        int nestedOwners = 0;

        ResourceOwner owner = slot.Transfer(value =>
        {
            Assert.Throws<InvalidOperationException>(() =>
                slot.Transfer(nested =>
                {
                    nestedOwners++;
                    return new ResourceOwner(nested);
                }));
            return new ResourceOwner(value);
        });

        Assert.Equal(0, nestedOwners);
        Assert.False(slot.HasFallback);
        owner.Dispose();
        Assert.Equal(1, resource.DisposeCalls);
    }

    private sealed class RetryableResource(
        int remainingFailures = 0,
        int remainingPrepareFailures = 0) : IDisposable
    {
        private int _remainingFailures = remainingFailures;
        private int _remainingPrepareFailures = remainingPrepareFailures;

        public int DisposeCalls { get; private set; }
        public int PrepareCalls { get; private set; }

        public void Prepare()
        {
            PrepareCalls++;
            if (_remainingPrepareFailures-- > 0)
                throw new InvalidOperationException("prepare failed");
        }

        public void Dispose()
        {
            DisposeCalls++;
            if (_remainingFailures-- > 0)
                throw new InvalidOperationException("synthetic release failure");
        }
    }

    private sealed class ResourceOwner(RetryableResource resource) : IDisposable
    {
        public void Dispose() => resource.Dispose();
    }

    private sealed class CallbackResource(Action onDispose) : IDisposable
    {
        public int DisposeCalls { get; private set; }

        public void Dispose()
        {
            DisposeCalls++;
            onDispose();
        }
    }

    private sealed class RecordingUpdateRoot(List<string> calls) : IGameUpdateFrameRoot
    {
        public void Tick(UpdateFrameInput input) => calls.Add(
            string.Create(
                CultureInfo.InvariantCulture,
                $"update:{input.HostDeltaSeconds}"));
    }

    private sealed class RecordingRenderRoot(List<string> calls) : IGameRenderFrameRoot
    {
        public RenderFrameOutcome Render(RenderFrameInput input)
        {
            calls.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"render:{input.DeltaSeconds}:{input.ViewportWidth}:{input.ViewportHeight}"));
            return default;
        }
    }
}
