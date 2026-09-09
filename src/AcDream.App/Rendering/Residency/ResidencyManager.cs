namespace AcDream.App.Rendering.Residency;

/// <summary>
/// Single-writer logical residency owner. It has no dependency on OpenGL and
/// never stores physical resource names.
/// </summary>
internal sealed class ResidencyManager
{
    private sealed class AssetSlot
    {
        public required RuntimeTypeHandle AssetType { get; init; }
        public required ushort Generation { get; init; }
        public required ResidencyAssetKey Key { get; init; }
        public required ulong WorldGeneration { get; init; }
        public AssetResidencyState State { get; set; }
        public ResidencyPriority Priority { get; set; }
        public long LastUsedFrame { get; set; }
        public long RebuildCost { get; set; }
        public ResidencyCharges Charges { get; set; }
        public string? Failure { get; set; }
        public HashSet<OwnerToken> Owners { get; } = [];
    }

    private sealed class OwnerSlot
    {
        public required ushort Generation { get; init; }
        public required ResidencyOwnerKind Kind { get; init; }
        public required ulong LogicalId { get; init; }
        public required ulong WorldGeneration { get; init; }
        public HashSet<AssetReference> Assets { get; } = [];
    }

    private readonly record struct TypedAssetKey(
        RuntimeTypeHandle AssetType,
        ResidencyAssetKey Key);

    private readonly List<AssetSlot?> _assets = [null];
    private readonly List<ushort> _assetGenerations = [0];
    private readonly Stack<uint> _freeAssets = [];
    private readonly Dictionary<TypedAssetKey, uint> _assetByKey = [];

    private readonly List<OwnerSlot?> _owners = [null];
    private readonly List<ushort> _ownerGenerations = [0];
    private readonly Stack<uint> _freeOwners = [];

    private readonly List<IResidencyDomainSource> _domainSources = [];
    private readonly List<ResidencyObservation> _observationScratch = [];
    private readonly List<ResidencyDomainSnapshot> _snapshotScratch = [];

    public ResidencyManager(ResidencyBudgetOptions? budgets = null) =>
        Budgets = budgets ?? ResidencyBudgetOptions.Default;

    public ResidencyBudgetOptions Budgets { get; }

    public int ActiveAssetCount => _assetByKey.Count;

    public int ActiveOwnerCount => _owners.Count(owner => owner is not null);

    public void RegisterDomainSource(IResidencyDomainSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_domainSources.Any(candidate => candidate.Domain == source.Domain))
        {
            throw new InvalidOperationException(
                $"Residency domain {source.Domain} is already registered.");
        }
        _domainSources.Add(source);
    }

    public OwnerToken CreateOwner(
        ResidencyOwnerKind kind,
        ulong logicalId,
        ulong worldGeneration)
    {
        uint index = AllocateIndex(_owners, _ownerGenerations, _freeOwners);
        ushort generation = NextGeneration(_ownerGenerations, index);
        _owners[checked((int)index)] = new OwnerSlot
        {
            Generation = generation,
            Kind = kind,
            LogicalId = logicalId,
            WorldGeneration = worldGeneration,
        };
        return new OwnerToken(index, generation);
    }

    public bool RetireOwner(OwnerToken owner)
    {
        if (!TryGetOwner(owner, out OwnerSlot slot))
            return false;

        AssetReference[] assets = slot.Assets.ToArray();
        for (int i = 0; i < assets.Length; i++)
        {
            if (TryGetAsset(assets[i], out AssetSlot asset))
                asset.Owners.Remove(owner);
        }

        _owners[checked((int)owner.Index)] = null;
        _freeOwners.Push(owner.Index);
        return true;
    }

    public AssetHandle<TAsset> Request<TAsset>(
        ResidencyAssetKey key,
        ResidencyPriority priority,
        ulong worldGeneration,
        long frame,
        long rebuildCost = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfNegative(rebuildCost);
        RuntimeTypeHandle type = typeof(TAsset).TypeHandle;
        var typedKey = new TypedAssetKey(type, key);
        if (_assetByKey.TryGetValue(typedKey, out uint existingIndex))
        {
            AssetSlot existing = _assets[checked((int)existingIndex)]
                ?? throw new InvalidOperationException(
                    "Residency key index did not resolve to a live slot.");
            if (IsTerminal(existing.State))
            {
                RecycleAsset(existingIndex, existing);
            }
            else
            {
                existing.Priority = Max(existing.Priority, priority);
                existing.LastUsedFrame = Math.Max(existing.LastUsedFrame, frame);
                existing.RebuildCost = Math.Max(existing.RebuildCost, rebuildCost);
                return new AssetHandle<TAsset>(
                    existingIndex,
                    existing.Generation);
            }
        }

        uint index = AllocateIndex(_assets, _assetGenerations, _freeAssets);
        ushort generation = NextGeneration(_assetGenerations, index);
        var slot = new AssetSlot
        {
            AssetType = type,
            Generation = generation,
            Key = key,
            WorldGeneration = worldGeneration,
            State = AssetResidencyState.Requested,
            Priority = priority,
            LastUsedFrame = frame,
            RebuildCost = rebuildCost,
        };
        _assets[checked((int)index)] = slot;
        _assetByKey.Add(typedKey, index);
        return new AssetHandle<TAsset>(index, generation);
    }

    public AssetLease<TAsset> Acquire<TAsset>(
        AssetHandle<TAsset> handle,
        OwnerToken owner,
        ResidencyPriority priority,
        long frame)
    {
        AssetSlot asset = GetAsset(handle);
        OwnerSlot ownerSlot = GetOwner(owner);
        if (asset.State is AssetResidencyState.Retiring
            or AssetResidencyState.Cancelled
            or AssetResidencyState.Missing
            or AssetResidencyState.Corrupt
            or AssetResidencyState.Failed)
        {
            throw new InvalidOperationException(
                $"Cannot acquire an asset in {asset.State} state.");
        }
        if (ownerSlot.WorldGeneration != asset.WorldGeneration
            && ownerSlot.Kind is not ResidencyOwnerKind.Session
                and not ResidencyOwnerKind.UserInterface
                and not ResidencyOwnerKind.Tooling)
        {
            throw new InvalidOperationException(
                "A world-scoped owner cannot acquire an asset from another generation.");
        }

        var lease = new AssetLease<TAsset>(handle, owner);
        if (asset.Owners.Add(owner))
            ownerSlot.Assets.Add(handle.Untyped);
        asset.Priority = Max(asset.Priority, priority);
        asset.LastUsedFrame = Math.Max(asset.LastUsedFrame, frame);
        return lease;
    }

    public bool Release<TAsset>(AssetLease<TAsset> lease)
    {
        if (!TryGetAsset(lease.Handle.Untyped, out AssetSlot asset)
            || !TryGetOwner(lease.Owner, out OwnerSlot owner))
        {
            return false;
        }

        bool removed = asset.Owners.Remove(lease.Owner);
        if (removed)
        {
            owner.Assets.Remove(lease.Handle.Untyped);
            if (asset.Owners.Count == 0)
                asset.Priority = ResidencyPriority.Speculative;
        }
        return removed;
    }

    public bool Touch<TAsset>(
        AssetHandle<TAsset> handle,
        ResidencyPriority priority,
        long frame)
    {
        if (!TryGetAsset(handle.Untyped, out AssetSlot asset))
            return false;
        asset.Priority = Max(asset.Priority, priority);
        asset.LastUsedFrame = Math.Max(asset.LastUsedFrame, frame);
        return true;
    }

    public bool Transition<TAsset>(
        AssetHandle<TAsset> handle,
        AssetResidencyState next,
        ResidencyCharges charges,
        long frame,
        string? failure = null) =>
        Transition(handle.Untyped, next, charges, frame, failure);

    public bool CompleteRetirement<TAsset>(AssetHandle<TAsset> handle) =>
        CompleteRetirement(handle.Untyped);

    public bool CompleteRetirement(AssetReference asset)
    {
        if (!TryGetAsset(asset, out AssetSlot slot))
            return false;
        if (slot.State != AssetResidencyState.Retiring)
        {
            throw new InvalidOperationException(
                $"Only a retiring asset can become absent; current={slot.State}.");
        }
        if (slot.Owners.Count != 0)
            throw new InvalidOperationException(
                "A retiring asset retained logical owners.");

        RecycleAsset(asset.Index, slot);
        return true;
    }

    public bool TryGetSnapshot<TAsset>(
        AssetHandle<TAsset> handle,
        out ResidencyEntrySnapshot snapshot) =>
        TryGetSnapshot(handle.Untyped, out snapshot);

    public int Drain(ResidencyObservationJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        _observationScratch.Clear();
        journal.DrainTo(_observationScratch);
        _observationScratch.Sort(static (left, right) =>
        {
            int byIndex = left.Asset.Index.CompareTo(right.Asset.Index);
            if (byIndex != 0)
                return byIndex;
            return left.Kind.CompareTo(right.Kind);
        });

        int applied = 0;
        for (int i = 0; i < _observationScratch.Count; i++)
        {
            ResidencyObservation observation = _observationScratch[i];
            bool accepted = observation.Kind switch
            {
                ResidencyObservationKind.Touch =>
                    Touch(
                        observation.Asset,
                        observation.Priority,
                        observation.Frame),
                ResidencyObservationKind.Transition =>
                    Transition(
                        observation.Asset,
                        observation.State,
                        observation.Charges,
                        observation.Frame,
                        observation.Failure),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(observation),
                    observation.Kind,
                    "unknown residency observation kind"),
            };
            if (accepted)
                applied++;
        }
        return applied;
    }

    public IReadOnlyList<ResidencyTrimRequest> SelectEvictions(
        ResidencyDomain domain,
        long bytesToRelease,
        int maximumCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesToRelease);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        if (bytesToRelease == 0 || maximumCount == 0)
            return Array.Empty<ResidencyTrimRequest>();

        List<(uint Index, AssetSlot Slot)> candidates = [];
        for (uint index = 1; index < _assets.Count; index++)
        {
            AssetSlot? slot = _assets[checked((int)index)];
            if (slot is null
                || slot.Key.Domain != domain
                || slot.Owners.Count != 0
                || slot.State is not (AssetResidencyState.Resident
                    or AssetResidencyState.Prepared))
            {
                continue;
            }
            candidates.Add((index, slot));
        }

        candidates.Sort(static (left, right) =>
        {
            int order = left.Slot.Priority.CompareTo(right.Slot.Priority);
            if (order != 0)
                return order;
            order = left.Slot.LastUsedFrame.CompareTo(right.Slot.LastUsedFrame);
            if (order != 0)
                return order;
            order = left.Slot.RebuildCost.CompareTo(right.Slot.RebuildCost);
            return order != 0
                ? order
                : left.Index.CompareTo(right.Index);
        });

        List<ResidencyTrimRequest> requests = [];
        long selectedBytes = 0;
        for (int i = 0;
             i < candidates.Count
                && requests.Count < maximumCount
                && selectedBytes < bytesToRelease;
             i++)
        {
            (uint index, AssetSlot slot) = candidates[i];
            ResidencyCharges retiring = ToRetiringCharges(slot.Charges);
            var asset = new AssetReference(
                index,
                slot.Generation,
                slot.AssetType);
            Transition(
                asset,
                AssetResidencyState.Retiring,
                retiring,
                slot.LastUsedFrame);
            requests.Add(new ResidencyTrimRequest(asset, slot.Key, retiring));
            selectedBytes = checked(
                selectedBytes + Math.Max(
                    1,
                    retiring.RetiringBytes
                    + retiring.CommittedCpuBytes));
        }
        return requests;
    }

    public ResidencySnapshot CaptureSnapshot()
    {
        _snapshotScratch.Clear();
        Span<bool> sourced = stackalloc bool[
            Enum.GetValues<ResidencyDomain>().Length];
        ResidencyCharges totals = default;
        for (int i = 0; i < _domainSources.Count; i++)
        {
            ResidencyDomainSnapshot snapshot =
                _domainSources[i].CaptureResidency();
            _snapshotScratch.Add(snapshot);
            sourced[(int)snapshot.Domain] = true;
            totals += snapshot.Charges;
        }

        ResidencyCharges[] logicalCharges =
            new ResidencyCharges[sourced.Length];
        int[] logicalEntries = new int[sourced.Length];
        int[] logicalOwners = new int[sourced.Length];
        for (uint index = 1; index < _assets.Count; index++)
        {
            AssetSlot? slot = _assets[checked((int)index)];
            if (slot is null || sourced[(int)slot.Key.Domain])
                continue;
            int domain = (int)slot.Key.Domain;
            logicalCharges[domain] += slot.Charges;
            logicalEntries[domain]++;
            logicalOwners[domain] = checked(
                logicalOwners[domain] + slot.Owners.Count);
        }

        for (int domain = 0; domain < logicalEntries.Length; domain++)
        {
            if (logicalEntries[domain] == 0)
                continue;
            ResidencyCharges charges = logicalCharges[domain];
            _snapshotScratch.Add(new ResidencyDomainSnapshot(
                (ResidencyDomain)domain,
                logicalEntries[domain],
                logicalOwners[domain],
                charges));
            totals += charges;
        }

        return new ResidencySnapshot(_snapshotScratch.ToArray(), totals);
    }

    private bool Transition(
        AssetReference asset,
        AssetResidencyState next,
        ResidencyCharges charges,
        long frame,
        string? failure = null)
    {
        if (!TryGetAsset(asset, out AssetSlot slot))
            return false;
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        charges.Validate();
        ValidateTransition(slot.State, next);
        ValidateCharges(next, charges);
        if (next == AssetResidencyState.Retiring && slot.Owners.Count != 0)
        {
            throw new InvalidOperationException(
                "An owned asset cannot enter retiring state.");
        }

        slot.State = next;
        slot.Charges = charges;
        slot.LastUsedFrame = Math.Max(slot.LastUsedFrame, frame);
        slot.Failure = next is AssetResidencyState.Corrupt
            or AssetResidencyState.Failed
            ? failure
            : null;
        if (IsTerminal(next))
            DetachAllOwners(asset, slot);
        return true;
    }

    private bool Touch(
        AssetReference asset,
        ResidencyPriority priority,
        long frame)
    {
        if (!TryGetAsset(asset, out AssetSlot slot))
            return false;
        slot.Priority = Max(slot.Priority, priority);
        slot.LastUsedFrame = Math.Max(slot.LastUsedFrame, frame);
        return true;
    }

    private bool TryGetSnapshot(
        AssetReference asset,
        out ResidencyEntrySnapshot snapshot)
    {
        if (!TryGetAsset(asset, out AssetSlot slot))
        {
            snapshot = default;
            return false;
        }

        snapshot = new ResidencyEntrySnapshot(
            asset,
            slot.Key,
            slot.State,
            slot.Priority,
            slot.WorldGeneration,
            slot.LastUsedFrame,
            slot.RebuildCost,
            slot.Owners.Count,
            slot.Charges,
            slot.Failure);
        return true;
    }

    private AssetSlot GetAsset<TAsset>(AssetHandle<TAsset> handle) =>
        TryGetAsset(handle.Untyped, out AssetSlot slot)
            ? slot
            : throw new InvalidOperationException("Asset handle is stale or invalid.");

    private OwnerSlot GetOwner(OwnerToken owner) =>
        TryGetOwner(owner, out OwnerSlot slot)
            ? slot
            : throw new InvalidOperationException("Owner token is stale or invalid.");

    private bool TryGetAsset(
        AssetReference asset,
        out AssetSlot slot)
    {
        slot = null!;
        if (!asset.IsValid || asset.Index >= _assets.Count)
            return false;
        AssetSlot? candidate = _assets[checked((int)asset.Index)];
        if (candidate is null
            || candidate.Generation != asset.Generation
            || !candidate.AssetType.Equals(asset.AssetType))
        {
            return false;
        }
        slot = candidate;
        return true;
    }

    private bool TryGetOwner(OwnerToken owner, out OwnerSlot slot)
    {
        slot = null!;
        if (!owner.IsValid || owner.Index >= _owners.Count)
            return false;
        OwnerSlot? candidate = _owners[checked((int)owner.Index)];
        if (candidate is null || candidate.Generation != owner.Generation)
            return false;
        slot = candidate;
        return true;
    }

    private void RecycleAsset(uint index, AssetSlot slot)
    {
        if (slot.Owners.Count != 0)
            throw new InvalidOperationException(
                "An asset cannot be recycled while owners remain.");
        _assetByKey.Remove(new TypedAssetKey(slot.AssetType, slot.Key));
        _assets[checked((int)index)] = null;
        _freeAssets.Push(index);
    }

    private void DetachAllOwners(AssetReference asset, AssetSlot slot)
    {
        if (slot.Owners.Count == 0)
            return;
        OwnerToken[] owners = slot.Owners.ToArray();
        for (int i = 0; i < owners.Length; i++)
        {
            if (TryGetOwner(owners[i], out OwnerSlot owner))
                owner.Assets.Remove(asset);
        }
        slot.Owners.Clear();
    }

    private static uint AllocateIndex<T>(
        List<T?> slots,
        List<ushort> generations,
        Stack<uint> free)
        where T : class
    {
        if (free.TryPop(out uint reused))
            return reused;
        uint index = checked((uint)slots.Count);
        slots.Add(null);
        generations.Add(0);
        return index;
    }

    private static ushort NextGeneration(
        List<ushort> generations,
        uint index)
    {
        ushort generation = unchecked(
            (ushort)(generations[checked((int)index)] + 1));
        if (generation == 0)
            generation = 1;
        generations[checked((int)index)] = generation;
        return generation;
    }

    private static ResidencyPriority Max(
        ResidencyPriority left,
        ResidencyPriority right) =>
        left >= right ? left : right;

    private static bool IsTerminal(AssetResidencyState state) =>
        state is AssetResidencyState.Cancelled
            or AssetResidencyState.Missing
            or AssetResidencyState.Corrupt
            or AssetResidencyState.Failed;

    private static void ValidateTransition(
        AssetResidencyState current,
        AssetResidencyState next)
    {
        bool legal = current switch
        {
            AssetResidencyState.Requested =>
                next is AssetResidencyState.Prepared
                    or AssetResidencyState.Cancelled
                    or AssetResidencyState.Missing
                    or AssetResidencyState.Corrupt
                    or AssetResidencyState.Failed
                    or AssetResidencyState.Retiring,
            AssetResidencyState.Prepared =>
                next is AssetResidencyState.UploadPending
                    or AssetResidencyState.Retiring,
            AssetResidencyState.UploadPending =>
                next is AssetResidencyState.Resident
                    or AssetResidencyState.Prepared
                    or AssetResidencyState.Retiring
                    or AssetResidencyState.Cancelled
                    or AssetResidencyState.Corrupt
                    or AssetResidencyState.Failed,
            AssetResidencyState.Resident =>
                next is AssetResidencyState.Retiring,
            _ => false,
        };
        if (!legal)
        {
            throw new InvalidOperationException(
                $"Illegal residency transition {current} -> {next}.");
        }
    }

    private static void ValidateCharges(
        AssetResidencyState state,
        ResidencyCharges charges)
    {
        if (state is AssetResidencyState.Cancelled
            or AssetResidencyState.Missing
            or AssetResidencyState.Corrupt
            or AssetResidencyState.Failed)
        {
            if (!charges.IsZero)
            {
                throw new InvalidOperationException(
                    $"Terminal state {state} cannot retain residency charges.");
            }
            return;
        }

        if (state == AssetResidencyState.Retiring
            && (charges.GpuRequestedBytes != 0
                || charges.GpuResidentBytes != 0
                || charges.StagingBytes != 0))
        {
            throw new InvalidOperationException(
                "Retiring charges must move transient and resident GPU bytes into RetiringBytes.");
        }
    }

    private static ResidencyCharges ToRetiringCharges(
        ResidencyCharges current) =>
        new(
            LogicalBytes: current.LogicalBytes,
            CpuPreparedBytes: current.CpuPreparedBytes,
            DecodedBytes: current.DecodedBytes,
            ScratchBytes: current.ScratchBytes,
            PinnedBytes: current.PinnedBytes,
            RetiringBytes: checked(
                current.GpuRequestedBytes
                + current.GpuResidentBytes
                + current.RetiringBytes));
}
