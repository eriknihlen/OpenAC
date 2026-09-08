using AcDream.Core.Items;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Gameplay;

public readonly record struct RuntimeInventoryOwnershipSnapshot(
    bool IsDisposed,
    bool TransactionsDisposed,
    bool ShortcutsDisposed,
    int BusyCount,
    bool HasPendingRequest,
    uint RequestedContainerId,
    uint CurrentContainerId,
    int ItemManaCount,
    int ShortcutCount,
    int ShortcutSubscriberCount,
    long ShortcutDispatchFailureCount,
    long TransactionDispatchFailureCount,
    int OpenedCorpseCount,
    uint VendorId,
    int MaterializedVendorItemCount)
{
    public bool IsConverged =>
        IsDisposed
        && TransactionsDisposed
        && ShortcutsDisposed
        && BusyCount == 0
        && !HasPendingRequest
        && RequestedContainerId == 0u
        && CurrentContainerId == 0u
        && ItemManaCount == 0
        && ShortcutCount == 0
        && ShortcutSubscriberCount == 0
        && OpenedCorpseCount == 0
        && VendorId == 0u
        && MaterializedVendorItemCount == 0;
}

public sealed class RuntimeInventoryState : IDisposable
{
    private readonly RuntimeEntityObjectLifetime _entityObjects;
    private bool _disposed;

    public RuntimeInventoryState(RuntimeEntityObjectLifetime entityObjects)
    {
        _entityObjects = entityObjects
            ?? throw new ArgumentNullException(nameof(entityObjects));
        ExternalContainers = new ExternalContainerState();
        _entityObjects.Objects.ObjectRemoved += OnObjectRemoved;
        ItemMana = new ItemManaState();
        Shortcuts = new ShortcutStore();
        Transactions = new InventoryTransactionState(_entityObjects.Objects);
        Vendor = new VendorState();
        VendorItems = new VendorShopItemMaterializer(Vendor, _entityObjects.Objects);
        View = new InventoryStateView(this);
    }

    public ClientObjectTable Objects => _entityObjects.Objects;
    public ExternalContainerState ExternalContainers { get; }
    public ItemManaState ItemMana { get; }
    public ShortcutStore Shortcuts { get; }
    public InventoryTransactionState Transactions { get; }
    public VendorState Vendor { get; }
    public VendorShopItemMaterializer VendorItems { get; }
    public IRuntimeInventoryStateView View { get; }
    public bool IsDisposed => _disposed;

    public RuntimeInventoryOwnershipSnapshot CaptureOwnership() => new(
        _disposed,
        Transactions.IsDisposed,
        Shortcuts.IsDisposed,
        Transactions.BusyCount,
        Transactions.HasPendingRequest,
        ExternalContainers.RequestedContainerId,
        ExternalContainers.CurrentContainerId,
        ItemMana.Count,
        Shortcuts.Count,
        Shortcuts.SubscriberCount,
        Shortcuts.DispatchFailureCount,
        Transactions.DispatchFailureCount,
        ExternalContainers.OpenedCorpseCount,
        Vendor.VendorId,
        VendorItems.OwnedCount);

    public void ResetExternalContainer() => ExternalContainers.Reset();
    public void ResetTransactions() => Transactions.ResetSession();
    public void ResetItemMana() => ItemMana.Clear();
    public void ResetVendor() => Vendor.Reset();

    public void ResetPlayerSnapshots()
    {
        Shortcuts.Clear();
    }

    public bool TryAddShortcut(
        ShortcutEntry entry,
        Action publishOutbound)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(publishOutbound);
        if ((uint)entry.Index >= ShortcutStore.SlotCount
            || (entry.ObjectId == 0u && entry.SpellId == 0u))
        {
            return false;
        }

        try
        {
            publishOutbound();
        }
        finally
        {
            Shortcuts.Set(entry);
        }
        return true;
    }

    public bool TryRemoveShortcut(
        int index,
        Action publishOutbound)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(publishOutbound);
        if ((uint)index >= ShortcutStore.SlotCount)
            return false;

        try
        {
            publishOutbound();
        }
        finally
        {
            Shortcuts.Remove(index);
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        try
        {
            _entityObjects.Objects.ObjectRemoved -= OnObjectRemoved;
            Try(() => ExternalContainers.Reset(), ref failures);
            Try(() => Vendor.Reset(), ref failures);
            Try(VendorItems.Dispose, ref failures);
            Try(ItemMana.Clear, ref failures);
            Try(Shortcuts.Dispose, ref failures);
            Try(Transactions.Dispose, ref failures);
        }
        finally
        {
            _disposed = true;
        }
        if (failures is not null)
            throw new AggregateException(
                "Runtime inventory state did not converge during disposal.",
                failures);
    }

    private static void Try(Action action, ref List<Exception>? failures)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            (failures ??= []).Add(error);
        }
    }

    private void OnObjectRemoved(ClientObject item)
        => ExternalContainers.SetCorpseDeleted(item.ObjectId);

    private sealed class InventoryStateView(RuntimeInventoryState owner)
        : IRuntimeInventoryStateView
    {
        public RuntimeInventoryStateSnapshot Snapshot
        {
            get
            {
                RuntimePendingInventoryRequestSnapshot? pending = null;
                if (owner.Transactions.TryGetPending(
                        out PendingInventoryRequest request))
                {
                    pending = new RuntimePendingInventoryRequestSnapshot(
                        request.Token,
                        (int)request.Kind,
                        request.ItemId,
                        request.Dispatched);
                }

                return new RuntimeInventoryStateSnapshot(
                    owner.ExternalContainers.RequestedContainerId,
                    owner.ExternalContainers.CurrentContainerId,
                    owner.Transactions.BusyCount,
                    owner.Transactions.CanBeginRequest,
                    pending,
                    owner.Shortcuts.Count,
                    owner.Shortcuts.Revision,
                    owner.ItemMana.Count,
                    owner.ItemMana.Revision);
            }
        }

        public bool TryGetShortcut(
            int index,
            out RuntimeShortcutSnapshot shortcut)
        {
            IReadOnlyList<ShortcutEntry> shortcuts = owner.Shortcuts.Items;
            for (int i = 0; i < shortcuts.Count; i++)
            {
                if (shortcuts[i].Index != index)
                    continue;
                ShortcutEntry current = shortcuts[i];
                shortcut = new RuntimeShortcutSnapshot(
                    current.Index,
                    current.ObjectId,
                    current.SpellId);
                return true;
            }
            shortcut = default;
            return false;
        }

        public bool TryGetItemMana(uint objectId, out float fraction) =>
            owner.ItemMana.TryGetManaPercent(objectId, out fraction);
    }
}
