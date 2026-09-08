using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class TextureAtlasSlotAllocatorTests
{
    [Fact]
    public void SlotIsNotAvailableUntilGpuRetirementReturnsIt()
    {
        var slots = new TextureAtlasSlotAllocator(capacity: 2);
        int first = slots.Rent();
        int second = slots.Rent();

        Assert.Equal(0, slots.AvailableCount);
        Assert.Throws<InvalidOperationException>(() => slots.Rent());

        Assert.Equal(0, slots.AvailableCount);

        slots.Return(first);

        Assert.Equal(1, slots.AvailableCount);
        Assert.Equal(first, slots.Rent());
        Assert.Equal(0, slots.AvailableCount);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void DoubleReturnIsRejected()
    {
        var slots = new TextureAtlasSlotAllocator(capacity: 1);
        int slot = slots.Rent();
        slots.Return(slot);

        Assert.Throws<InvalidOperationException>(() => slots.Return(slot));
    }

    [Fact]
    public void LayerRetirement_PublicationFailureRetainsPhysicalReturn()
    {
        var queue = new DeferredQueue { FailNextPublication = true };
        var retirement = new TextureAtlasLayerRetirement(queue);
        int returns = 0;
        int notifications = 0;

        Assert.Throws<InvalidOperationException>(() =>
            retirement.Retire(() => returns++, () => notifications++));
        Assert.Equal(1, retirement.AwaitingPublicationCount);
        Assert.Equal(0, returns);

        retirement.RetryPendingPublications();
        Assert.Equal(0, retirement.AwaitingPublicationCount);
        queue.Actions.Single()();
        Assert.Equal(1, returns);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void LayerRetirement_ObserverFailureNeverReturnsSlotTwice()
    {
        var queue = new DeferredQueue();
        var retirement = new TextureAtlasLayerRetirement(queue);
        int returns = 0;
        int notifications = 0;
        retirement.Retire(
            () => returns++,
            () =>
            {
                notifications++;
                if (notifications == 1)
                    throw new InvalidOperationException("observer");
            });
        Action callback = queue.Actions.Single();

        Assert.Throws<InvalidOperationException>(callback);
        callback();

        Assert.Equal(1, returns);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void DisposeTransaction_ArrayPublicationFailureRetainsRetryableOwnership()
    {
        var transaction = new TextureAtlasDisposeTransaction();
        int layerRetries = 0;
        int arrayDisposals = 0;
        int logicalCommits = 0;
        bool failFirstArrayDisposal = true;

        Assert.Throws<InvalidOperationException>(() => transaction.Advance(
            () => layerRetries++,
            () => {
                arrayDisposals++;
                if (failFirstArrayDisposal) {
                    failFirstArrayDisposal = false;
                    throw new InvalidOperationException("injected array publication failure");
                }
            },
            () => logicalCommits++));

        Assert.False(transaction.IsComplete);
        Assert.Equal(1, layerRetries);
        Assert.Equal(1, arrayDisposals);
        Assert.Equal(0, logicalCommits);

        transaction.Advance(
            () => layerRetries++,
            () => arrayDisposals++,
            () => logicalCommits++);
        transaction.Advance(
            () => layerRetries++,
            () => arrayDisposals++,
            () => logicalCommits++);

        Assert.True(transaction.IsComplete);
        Assert.Equal(2, layerRetries);
        Assert.Equal(2, arrayDisposals);
        Assert.Equal(1, logicalCommits);
    }

    [Fact]
    public void DisposeTransaction_ReentrantAdvanceDoesNotReplayActiveStage()
    {
        var transaction = new TextureAtlasDisposeTransaction();
        int layerRetries = 0;
        int arrayDisposals = 0;
        int logicalCommits = 0;
        Action retryLayers = () => layerRetries++;
        Action commit = () => logicalCommits++;
        Action? disposeArray = null;
        disposeArray = () => {
            arrayDisposals++;
            transaction.Advance(retryLayers, disposeArray!, commit);
        };

        transaction.Advance(retryLayers, disposeArray, commit);

        Assert.True(transaction.IsComplete);
        Assert.Equal(1, layerRetries);
        Assert.Equal(1, arrayDisposals);
        Assert.Equal(1, logicalCommits);
    }

    private sealed class DeferredQueue : IGpuResourceRetirementQueue
    {
        public bool FailNextPublication { get; set; }
        public List<Action> Actions { get; } = [];

        public void Retire(Action release)
        {
            if (FailNextPublication)
            {
                FailNextPublication = false;
                throw new InvalidOperationException("publication");
            }
            Actions.Add(release);
        }
    }
}
