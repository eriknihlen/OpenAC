using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Tests.Rendering;

public sealed class SyntheticEntityMeshReferenceOwnerTests
{
    [Fact]
    public void PrivateOwnerSurvivesIndependentWorldPresentationRelease()
    {
        const ulong mesh = 0x01000001u;
        var adapter = new RecordingMeshAdapter();
        adapter.IncrementRefCount(mesh);
        var privateOwner =
            new SyntheticEntityMeshReferenceOwner(adapter, [mesh]);
        privateOwner.Acquire();

        adapter.DecrementRefCount(mesh);

        Assert.Equal(1, adapter.ReferenceCount(mesh));
        privateOwner.Dispose();
        Assert.Equal(0, adapter.TotalReferences);
    }

    [Fact]
    public void PartialAcquireRollsBackEveryCommittedSibling()
    {
        var adapter = new RecordingMeshAdapter();
        adapter.FailIncrementBeforeCommit(0x01000001u, attempts: 1);
        var owner = new SyntheticEntityMeshReferenceOwner(
            adapter,
            [0x01000001u, 0x01000002u]);

        Assert.Throws<AggregateException>(owner.Acquire);
        Assert.Equal(0, adapter.TotalReferences);

        owner.Acquire();

        Assert.Equal(2, adapter.IncrementAttempts(0x01000001u));
        Assert.Equal(2, adapter.IncrementAttempts(0x01000002u));
        owner.Dispose();
        Assert.Equal(0, adapter.TotalReferences);
    }

    [Fact]
    public void CommittedAcquireFailureIsRolledBackBeforeRetry()
    {
        var adapter = new RecordingMeshAdapter();
        adapter.FailIncrementAfterCommit(0x01000001u, attempts: 1);
        var owner = new SyntheticEntityMeshReferenceOwner(adapter, [0x01000001u]);

        Assert.Throws<AggregateException>(owner.Acquire);
        Assert.Equal(0, adapter.TotalReferences);
        owner.Acquire();

        Assert.Equal(2, adapter.IncrementAttempts(0x01000001u));
        Assert.Equal(1, adapter.ReferenceCount(0x01000001u));
        owner.Dispose();
        Assert.Equal(0, adapter.TotalReferences);
    }

    [Fact]
    public void FailedAcquireRollbackRetainsExactReferenceForDisposeRetry()
    {
        var adapter = new RecordingMeshAdapter();
        adapter.FailIncrementBeforeCommit(0x01000001u, attempts: 1);
        adapter.FailDecrementBeforeCommit(0x01000002u, attempts: 1);
        var owner = new SyntheticEntityMeshReferenceOwner(
            adapter,
            [0x01000001u, 0x01000002u]);

        AggregateException failure = Assert.Throws<AggregateException>(owner.Acquire);

        Assert.Contains("rollback", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, adapter.ReferenceCount(0x01000001u));
        Assert.Equal(1, adapter.ReferenceCount(0x01000002u));

        owner.Dispose();

        Assert.True(owner.IsDisposed);
        Assert.Equal(2, adapter.DecrementAttempts(0x01000002u));
        Assert.Equal(0, adapter.TotalReferences);
    }

    [Fact]
    public void DisposeAttemptsEveryReferenceAndRetriesOnlyPendingRelease()
    {
        var adapter = new RecordingMeshAdapter();
        var owner = new SyntheticEntityMeshReferenceOwner(
            adapter,
            [0x01000001u, 0x01000002u]);
        owner.Acquire();
        adapter.FailDecrementBeforeCommit(0x01000001u, attempts: 1);

        Assert.Throws<AggregateException>(owner.Dispose);

        Assert.False(owner.IsDisposed);
        Assert.Equal(0, adapter.ReferenceCount(0x01000002u));
        owner.Dispose();
        owner.Dispose();

        Assert.True(owner.IsDisposed);
        Assert.Equal(2, adapter.DecrementAttempts(0x01000001u));
        Assert.Equal(1, adapter.DecrementAttempts(0x01000002u));
        Assert.Equal(0, adapter.TotalReferences);
    }

    [Fact]
    public void CommittedReleaseFailureCompletesOwnershipWithoutDoubleDecrement()
    {
        var adapter = new RecordingMeshAdapter();
        var owner = new SyntheticEntityMeshReferenceOwner(adapter, [0x01000001u]);
        owner.Acquire();
        adapter.FailDecrementAfterCommit(0x01000001u, attempts: 1);

        Assert.Throws<AggregateException>(owner.Dispose);

        Assert.True(owner.IsDisposed);
        owner.Dispose();
        Assert.Equal(1, adapter.DecrementAttempts(0x01000001u));
        Assert.Equal(0, adapter.TotalReferences);
    }

    [Fact]
    public void DisposeReentryDuringAcquireReconcilesBeforeReturning()
    {
        SyntheticEntityMeshReferenceOwner? owner = null;
        var adapter = new RecordingMeshAdapter
        {
            AfterIncrement = _ => owner!.Dispose(),
        };
        owner = new SyntheticEntityMeshReferenceOwner(adapter, [0x01000001u]);

        Assert.Throws<ObjectDisposedException>(owner.Acquire);
        owner.Dispose();

        Assert.True(owner.IsDisposed);
        Assert.Equal(1, adapter.IncrementAttempts(0x01000001u));
        Assert.Equal(1, adapter.DecrementAttempts(0x01000001u));
        Assert.Equal(0, adapter.TotalReferences);
    }

    private sealed class RecordingMeshAdapter : IWbMeshAdapter
    {
        private readonly Dictionary<ulong, int> _references = [];
        private readonly Dictionary<ulong, int> _incrementAttempts = [];
        private readonly Dictionary<ulong, int> _decrementAttempts = [];
        private readonly Dictionary<ulong, int> _incrementFailuresBeforeCommit = [];
        private readonly Dictionary<ulong, int> _incrementFailuresAfterCommit = [];
        private readonly Dictionary<ulong, int> _decrementFailuresBeforeCommit = [];
        private readonly Dictionary<ulong, int> _decrementFailuresAfterCommit = [];

        public Action<ulong>? AfterIncrement { get; init; }
        public int TotalReferences => _references.Values.Sum();

        public int ReferenceCount(ulong id) => _references.GetValueOrDefault(id);
        public int IncrementAttempts(ulong id) => _incrementAttempts.GetValueOrDefault(id);
        public int DecrementAttempts(ulong id) => _decrementAttempts.GetValueOrDefault(id);

        public void FailIncrementBeforeCommit(ulong id, int attempts) =>
            _incrementFailuresBeforeCommit[id] = attempts;

        public void FailIncrementAfterCommit(ulong id, int attempts) =>
            _incrementFailuresAfterCommit[id] = attempts;

        public void FailDecrementBeforeCommit(ulong id, int attempts) =>
            _decrementFailuresBeforeCommit[id] = attempts;

        public void FailDecrementAfterCommit(ulong id, int attempts) =>
            _decrementFailuresAfterCommit[id] = attempts;

        public void IncrementRefCount(ulong id)
        {
            _incrementAttempts[id] = IncrementAttempts(id) + 1;
            if (Consume(_incrementFailuresBeforeCommit, id))
                throw new InvalidOperationException("synthetic pre-commit acquire failure");

            _references[id] = ReferenceCount(id) + 1;
            AfterIncrement?.Invoke(id);
            if (Consume(_incrementFailuresAfterCommit, id))
            {
                throw new MeshReferenceMutationException(
                    "synthetic committed acquire failure",
                    mutationCommitted: true,
                    new InvalidOperationException());
            }
        }

        public void DecrementRefCount(ulong id)
        {
            _decrementAttempts[id] = DecrementAttempts(id) + 1;
            if (Consume(_decrementFailuresBeforeCommit, id))
                throw new InvalidOperationException("synthetic pre-commit release failure");

            int references = ReferenceCount(id);
            if (references <= 0)
                throw new InvalidOperationException("reference underflow");
            _references[id] = references - 1;
            if (Consume(_decrementFailuresAfterCommit, id))
            {
                throw new MeshReferenceMutationException(
                    "synthetic committed release failure",
                    mutationCommitted: true,
                    new InvalidOperationException());
            }
        }

        private static bool Consume(Dictionary<ulong, int> remaining, ulong id)
        {
            int count = remaining.GetValueOrDefault(id);
            if (count <= 0)
                return false;
            remaining[id] = count - 1;
            return true;
        }
    }
}
