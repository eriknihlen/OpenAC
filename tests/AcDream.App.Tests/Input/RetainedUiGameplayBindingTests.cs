using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.UI;

namespace AcDream.App.Tests.Input;

public sealed class RetainedUiGameplayBindingTests
{
    [Fact]
    public void AttachedBinding_RoutesOnlyItemPayloads()
    {
        var surface = new Surface();
        var calls = new List<(uint Id, int X, int Y)>();
        var binding = Create(surface, calls);
        binding.Attach();

        surface.Raise("not an item", 1, 2);
        surface.Raise(Item(42), 7, 9);

        Assert.Equal([(42u, 7, 9)], calls);
    }

    [Fact]
    public void AddAfterSideEffectFailure_RollsBackExactCallback()
    {
        var surface = new Surface { ThrowAfterAdd = true };
        var binding = Create(surface, []);

        Assert.Throws<InvalidOperationException>(binding.Attach);

        Assert.True(binding.IsDisposalComplete);
        Assert.Null(surface.Callback);
        Assert.Equal(1, surface.RemoveCalls);
    }

    [Fact]
    public void ReentrantDeliveryDuringAdd_CannotEnterPartialBinding()
    {
        var surface = new Surface { RaiseDuringAdd = true };
        var calls = new List<(uint Id, int X, int Y)>();
        var binding = Create(surface, calls);

        binding.Attach();
        Assert.Empty(calls);
        surface.Raise(Item(2), 3, 4);
        Assert.Equal([(2u, 3, 4)], calls);
    }

    [Fact]
    public void AttachAndRollbackFailure_RetainsCleanupForLaterDispose()
    {
        var surface = new Surface
        {
            ThrowAfterAdd = true,
            RemoveFailures = int.MaxValue,
        };
        var binding = Create(surface, []);

        Assert.Throws<AggregateException>(binding.Attach);
        Assert.False(binding.IsDisposalComplete);

        surface.RemoveFailures = 0;
        binding.Dispose();

        Assert.True(binding.IsDisposalComplete);
        Assert.Null(surface.Callback);
    }

    [Fact]
    public void Deactivate_MakesCopiedCallbackSilentBeforePhysicalDetach()
    {
        var surface = new Surface();
        var calls = new List<(uint Id, int X, int Y)>();
        var binding = Create(surface, calls);
        binding.Attach();
        Action<object, int, int> copied = surface.Callback!;

        binding.Deactivate();
        copied(Item(8), 1, 1);

        Assert.Empty(calls);
        Assert.False(binding.IsDisposalComplete);
    }

    [Fact]
    public void Dispose_RetriesFailedRemovalWithoutReattaching()
    {
        var surface = new Surface { RemoveFailures = int.MaxValue };
        var binding = Create(surface, []);
        binding.Attach();

        Assert.Throws<AggregateException>(binding.Dispose);
        Assert.False(binding.IsDisposalComplete);

        surface.RemoveFailures = 0;
        binding.Dispose();

        Assert.True(binding.IsDisposalComplete);
        Assert.Equal(1, surface.AddCalls);
        Assert.Equal(3, surface.RemoveCalls);
    }

    [Fact]
    public void DisposeBeforeAttach_IsTerminal()
    {
        var surface = new Surface();
        var binding = Create(surface, []);

        binding.Dispose();

        Assert.True(binding.IsDisposalComplete);
        Assert.Throws<ObjectDisposedException>(binding.Attach);
        Assert.Null(surface.Callback);
    }

    private static RetainedUiGameplayBinding Create(
        Surface surface,
        List<(uint Id, int X, int Y)> calls) =>
        new(
            surface,
            (item, x, y) => calls.Add((item.ObjId, x, y)),
            new HostQuiescenceGate());

    private static ItemDragPayload Item(uint id) =>
        new(id, ItemDragSource.Inventory, 0, SourceCell: null!);

    private sealed class Surface : IRetainedUiDragReleaseSurface
    {
        public Action<object, int, int>? Callback { get; private set; }
        public bool ThrowAfterAdd { get; set; }
        public bool RaiseDuringAdd { get; set; }
        public int RemoveFailures { get; set; }
        public int AddCalls { get; private set; }
        public int RemoveCalls { get; private set; }

        public void AddReleasedOutside(Action<object, int, int> callback)
        {
            AddCalls++;
            Callback = callback;
            if (RaiseDuringAdd)
                callback(Item(1), 1, 1);
            if (ThrowAfterAdd)
                throw new InvalidOperationException("add");
        }

        public void RemoveReleasedOutside(Action<object, int, int> callback)
        {
            RemoveCalls++;
            if (RemoveFailures-- > 0)
                throw new InvalidOperationException("remove");
            if (ReferenceEquals(Callback, callback))
                Callback = null;
        }

        public void Raise(object payload, int x, int y) =>
            Callback?.Invoke(payload, x, y);
    }
}
