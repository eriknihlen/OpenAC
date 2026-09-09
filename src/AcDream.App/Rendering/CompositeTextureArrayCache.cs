using AcDream.App.Rendering.Gpu;
using AcDream.Core.Textures;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal readonly struct BindlessTextureLocation : IEquatable<BindlessTextureLocation>
{
    private readonly uint _slotPlusOne;

    public BindlessTextureLocation(GpuTextureSlot slot, uint layer)
    {
        _slotPlusOne = slot.IsAssigned ? slot.Index + 1 : 0;
        Layer = layer;
    }

    public static BindlessTextureLocation Unresolved => default;

    public uint Layer { get; }

    public bool IsResolved => _slotPlusOne != 0;

    public GpuTextureSlot Slot =>
        _slotPlusOne == 0 ? GpuTextureSlot.Unassigned : new GpuTextureSlot(_slotPlusOne - 1);

    public bool Equals(BindlessTextureLocation other) =>
        _slotPlusOne == other._slotPlusOne && Layer == other.Layer;

    public override bool Equals(object? obj) =>
        obj is BindlessTextureLocation other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_slotPlusOne, Layer);

    public static bool operator ==(BindlessTextureLocation left, BindlessTextureLocation right) =>
        left.Equals(right);

    public static bool operator !=(BindlessTextureLocation left, BindlessTextureLocation right) =>
        !left.Equals(right);

    public override string ToString() =>
        IsResolved ? $"{Slot}/layer{Layer}" : "unresolved";
}

internal enum CompositeTextureKind : byte
{
    OriginalTextureOverride,
    PaletteComposite,
}

internal readonly struct PaletteCompositeIdentity : IEquatable<PaletteCompositeIdentity>
{
    private readonly IReadOnlyList<PaletteOverride.SubPaletteRange>? _ranges;

    public PaletteCompositeIdentity(PaletteOverride palette, ulong hash)
    {
        ArgumentNullException.ThrowIfNull(palette);
        BasePaletteId = palette.BasePaletteId;
        Hash = hash;
        _ranges = palette.SubPalettes;
    }

    public uint BasePaletteId { get; }
    public ulong Hash { get; }
    public int RangeCount => _ranges?.Count ?? 0;

    public bool Equals(PaletteCompositeIdentity other)
    {
        if (Hash != other.Hash
            || BasePaletteId != other.BasePaletteId
            || RangeCount != other.RangeCount)
        {
            return false;
        }

        for (int i = 0; i < RangeCount; i++)
            if (_ranges![i] != other._ranges![i])
                return false;
        return true;
    }

    public override bool Equals(object? obj) =>
        obj is PaletteCompositeIdentity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(BasePaletteId, Hash, RangeCount);

    public static bool operator ==(PaletteCompositeIdentity left, PaletteCompositeIdentity right) =>
        left.Equals(right);

    public static bool operator !=(PaletteCompositeIdentity left, PaletteCompositeIdentity right) =>
        !left.Equals(right);
}

internal readonly record struct CompositeTextureKey(
    CompositeTextureKind Kind,
    uint SurfaceId,
    uint OrigTextureOverride,
    PaletteCompositeIdentity Palette);

internal sealed class CompositeTextureArrayResource
{
    /// <summary>The GL texture name, or 0 on the backend-neutral arm.</summary>
    public required uint Name { get; init; }

    /// <summary>The resident bindless handle, or 0 on the backend-neutral arm.</summary>
    public required ulong Handle { get; init; }

    public Gpu.IGpuTexture? Image { get; init; }

    public required GpuTextureSlot Slot { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Capacity { get; init; }
    public required long Bytes { get; init; }
}

internal interface ICompositeTextureArrayBackend
{
    int MaximumArrayLayers { get; }
    CompositeTextureArrayResource Create(int width, int height, int capacity);
    void Upload(CompositeTextureArrayResource resource, int layer, byte[] rgba);
    void MakeNonResident(CompositeTextureArrayResource resource);
    void Delete(CompositeTextureArrayResource resource);
}

internal sealed class RhiCompositeTextureArrayBackend : ICompositeTextureArrayBackend
{
    private readonly Gpu.IGpuDevice _device;
    private readonly Gpu.IGpuSampler _sampler;

    internal RhiCompositeTextureArrayBackend(Gpu.IGpuDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _sampler = device.CreateSampler(Gpu.GpuSamplerDescription.WorldClamp with
        {
            MipFilter = Gpu.GpuMipFilter.None,
        });
    }

    public int MaximumArrayLayers => checked((int)Math.Min(
        _device.Capabilities.MaxImageArrayLayers,
        (uint)int.MaxValue));

    public CompositeTextureArrayResource Create(int width, int height, int capacity)
    {
        Gpu.IGpuTexture? image = null;
        try
        {
            image = _device.CreateTexture(new Gpu.GpuTextureDescription(
                $"composite-array-{width}x{height}x{capacity}",
                Gpu.GpuTextureKind.Texture2DArray,
                Gpu.GpuTextureFormat.Rgba8Unorm,
                width,
                height,
                capacity,
                MipLevelCount: 1));
            Gpu.GpuTextureSlot slot = _device.RegisterTexture(image, _sampler);
            return new CompositeTextureArrayResource
            {
                Name = 0,
                Handle = 0,
                Image = image,
                Slot = slot,
                Width = width,
                Height = height,
                Capacity = capacity,
                Bytes = checked((long)width * height * 4L * capacity),
            };
        }
        catch
        {
            image?.Dispose();
            throw;
        }
    }

    public void Upload(CompositeTextureArrayResource resource, int layer, byte[] rgba) =>
        RequireImage(resource).Upload(0, layer, rgba);

    /// <summary>
    /// On GL this makes a bindless handle non-resident after retiring its table
    /// entry. There is no residency on the RHI arm, so retiring the entry is the
    /// whole of it — and it is a slot the device defers behind its own
    /// retirement queue, exactly as the GL arm's release does.
    /// </summary>
    public void MakeNonResident(CompositeTextureArrayResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Slot.IsAssigned)
            _device.ReleaseTextureSlot(resource.Slot);
    }

    public void Delete(CompositeTextureArrayResource resource) => RequireImage(resource).Dispose();

    private static Gpu.IGpuTexture RequireImage(CompositeTextureArrayResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.Image
            ?? throw new InvalidOperationException(
                "This composite resource was created by the GL backend and has no RHI image.");
    }
}

internal sealed class CompositeTextureArrayCache : IDisposable
{
    internal const long DefaultUnownedBudgetBytes = 64L * 1024 * 1024;
    internal const long DefaultPhysicalBudgetBytes = 128L * 1024 * 1024;
    internal const long TargetArrayBytes = 4L * 1024 * 1024;
    internal const int MaximumLayersPerArray = 64;
    internal const int DefaultMaximumUploadsPerFrame = 16;
    internal const int DestinationRevealMaximumUploadsPerFrame = 64;
    internal const long DefaultMaximumUploadBytesPerFrame = 8L * 1024 * 1024;
    internal const int MaximumLogicalEvictionsPerFrame = 16;
    internal const int MaximumAtlasCreationsPerFrame = 1;

    private readonly ICompositeTextureArrayBackend _backend;
    private readonly GpuRetirementLedger _retirementLedger;
    private readonly OwnerScopedResourceRegistry<CompositeTextureKey> _owners = new();
    private readonly BoundedUnownedResourceCache<CompositeTextureKey> _unowned;
    private readonly long _physicalBudgetBytes;
    private readonly int _maximumArrayLayers;
    private readonly int _maximumUploadsPerFrame;
    private readonly long _maximumUploadBytesPerFrame;
    private readonly Dictionary<CompositeTextureKey, Entry> _entries = new();
    private readonly Dictionary<(int Width, int Height), List<Atlas>> _atlasesBySize = new();
    private readonly List<Atlas> _atlases = new();
    private long _allocatedBytes;
    private long _useSequence;
    private int _frameUploadCount;
    private long _frameUploadBytes;
    private int _frameAtlasCreationCount;
    private bool _uploadBudgetBlocked;
    private bool _destinationRevealUploadPriority;
    private int _pendingAtlasWidth;
    private int _pendingAtlasHeight;
    private long _pendingAtlasAllocationBytes;
    private readonly List<CompositeTextureKey> _evictionScratch = new(MaximumLogicalEvictionsPerFrame);
    private bool _disposeRequested;
    private bool _disposed;

    private sealed class Entry
    {
        public required Atlas Atlas { get; init; }
        public required int Layer { get; init; }
        public required long Bytes { get; init; }
    }

    private sealed class Atlas
    {
        public required CompositeTextureArrayResource Resource { get; init; }
        public required Wb.TextureAtlasSlotAllocator Slots { get; init; }
        public int EntryCount { get; set; }
        public int PendingRetirements { get; set; }
        public long LastUseSequence { get; set; }
        public bool ReleaseRequested { get; set; }
        public AtlasReleaseStage ReleaseStage { get; set; }

        public int AvailableLayers => Slots.AvailableCount;
        public bool IsGpuSafeEmpty => EntryCount == 0 && PendingRetirements == 0;
        public bool IsReusable => !ReleaseRequested && ReleaseStage == AtlasReleaseStage.Resident;
        public bool Deleted => ReleaseStage >= AtlasReleaseStage.Deleted;
    }

    private enum AtlasReleaseStage : byte
    {
        Resident,
        NonResident,
        Deleted,
        Accounted,
    }

    internal CompositeTextureArrayCache(
        ICompositeTextureArrayBackend backend,
        IGpuResourceRetirementQueue retirementQueue,
        long unownedBudgetBytes = DefaultUnownedBudgetBytes,
        long physicalBudgetBytes = DefaultPhysicalBudgetBytes,
        int maximumUploadsPerFrame = DefaultMaximumUploadsPerFrame,
        long maximumUploadBytesPerFrame = DefaultMaximumUploadBytesPerFrame)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        ArgumentNullException.ThrowIfNull(retirementQueue);
        _retirementLedger = new GpuRetirementLedger(retirementQueue);
        ArgumentOutOfRangeException.ThrowIfNegative(physicalBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUploadsPerFrame, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUploadBytesPerFrame, 1);
        _unowned = new BoundedUnownedResourceCache<CompositeTextureKey>(unownedBudgetBytes);
        _physicalBudgetBytes = physicalBudgetBytes;
        _maximumUploadsPerFrame = maximumUploadsPerFrame;
        _maximumUploadBytesPerFrame = maximumUploadBytesPerFrame;
        _maximumArrayLayers = Math.Max(
            1,
            Math.Min(backend.MaximumArrayLayers, MaximumLayersPerArray));
    }

    internal int ActiveResourceCount => _owners.ResourceCount;
    internal int OwnerCount => _owners.OwnerCount;
    internal int CachedEntryCount => _entries.Count;
    internal int UnownedEntryCount => _unowned.Count;
    internal long UnownedBytes => _unowned.ResidentBytes;
    internal int AtlasCount => _atlases.Count;
    internal long AllocatedBytes => _allocatedBytes;
    internal long PhysicalBudgetBytes => _physicalBudgetBytes;
    internal long UnownedBudgetBytes => _unowned.BudgetBytes;
    internal int FrameUploadCount => _frameUploadCount;
    internal long FrameUploadBytes => _frameUploadBytes;
    internal bool CanStartUpload =>
        !_uploadBudgetBlocked
        && _frameUploadCount < CurrentMaximumUploadsPerFrame
        && (_frameUploadCount == 0 || _frameUploadBytes < _maximumUploadBytesPerFrame);

    private int CurrentMaximumUploadsPerFrame =>
        _destinationRevealUploadPriority
            ? Math.Max(
                _maximumUploadsPerFrame,
                DestinationRevealMaximumUploadsPerFrame)
            : _maximumUploadsPerFrame;

    internal Residency.ResidencyDomainSnapshot CaptureResidency()
    {
        long retiringBytes = 0;
        long usedBytes = 0;
        long availableBytes = 0;
        for (int i = 0; i < _atlases.Count; i++)
        {
            Atlas atlas = _atlases[i];
            if (atlas.ReleaseRequested
                || atlas.ReleaseStage != AtlasReleaseStage.Resident)
            {
                retiringBytes = checked(
                    retiringBytes + atlas.Resource.Bytes);
                usedBytes = checked(
                    usedBytes + atlas.Resource.Bytes);
                continue;
            }

            long layerBytes = atlas.Resource.Bytes / atlas.Slots.Capacity;
            usedBytes = checked(
                usedBytes
                + layerBytes * checked(
                    atlas.EntryCount + atlas.PendingRetirements));
            availableBytes = checked(
                availableBytes
                + layerBytes * atlas.AvailableLayers);
        }

        return new Residency.ResidencyDomainSnapshot(
            Residency.ResidencyDomain.CompositeTextures,
            EntryCount: _entries.Count,
            OwnerCount: _owners.OwnerCount,
            Charges: new Residency.ResidencyCharges(
                GpuRequestedBytes: _pendingAtlasAllocationBytes,
                GpuResidentBytes: checked(
                    _allocatedBytes - retiringBytes),
                RetiringBytes: retiringBytes),
            BudgetBytes: _physicalBudgetBytes,
            CapacityBytes: _allocatedBytes,
            UsedBytes: usedBytes,
            LargestFreeBytes: availableBytes);
    }

    internal bool CanUpload(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        if (_frameUploadCount >= CurrentMaximumUploadsPerFrame)
            return false;

        return _frameUploadCount == 0
            || bytes <= _maximumUploadBytesPerFrame - _frameUploadBytes;
    }

    internal bool CanPrepareUpload(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        long layerBytes = checked((long)width * height * 4L);
        if (!CanUpload(layerBytes))
        {
            _uploadBudgetBlocked = true;
            return false;
        }

        if (_atlasesBySize.TryGetValue((width, height), out List<Atlas>? compatible))
        {
            for (int i = 0; i < compatible.Count; i++)
                if (compatible[i].IsReusable && compatible[i].AvailableLayers != 0)
                    return true;
        }

        int capacity = CalculateLayerCapacity(width, height, _maximumArrayLayers);
        long requestedBytes = checked(layerBytes * capacity);
        if (_frameAtlasCreationCount < MaximumAtlasCreationsPerFrame
            && CanAllocateAtlas(requestedBytes))
            return true;

        SetPendingAllocation(width, height, requestedBytes);
        _uploadBudgetBlocked = true;
        return false;
    }

    public void BeginFrame(bool destinationRevealUploadPriority = false)
    {
        ThrowIfUnavailable();
        _retirementLedger.RetryPendingPublications();
        _destinationRevealUploadPriority =
            destinationRevealUploadPriority;
        _frameUploadCount = 0;
        _frameUploadBytes = 0;
        _frameAtlasCreationCount = 0;
        _uploadBudgetBlocked = false;
    }

    public bool TryAcquire(
        uint ownerLocalId,
        CompositeTextureKey key,
        out BindlessTextureLocation location)
    {
        ThrowIfUnavailable();
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            location = BindlessTextureLocation.Unresolved;
            return false;
        }

        _owners.Acquire(ownerLocalId, key);
        _unowned.MarkOwned(key);
        entry.Atlas.LastUseSequence = ++_useSequence;
        location = new BindlessTextureLocation(
            entry.Atlas.Resource.Slot,
            checked((uint)entry.Layer));
        return true;
    }

    public bool TryAddAndAcquire(
        uint ownerLocalId,
        CompositeTextureKey key,
        DecodedTexture decoded,
        out BindlessTextureLocation location)
    {
        ThrowIfUnavailable();
        if (TryAcquire(ownerLocalId, key, out BindlessTextureLocation existing))
        {
            location = existing;
            return true;
        }

        ValidateDecodedTexture(decoded);
        long bytes = checked((long)decoded.Width * decoded.Height * 4L);
        if (!CanUpload(bytes))
        {
            _uploadBudgetBlocked = true;
            location = BindlessTextureLocation.Unresolved;
            return false;
        }

        if (!TryFindOrCreateAtlas(decoded.Width, decoded.Height, out Atlas atlas))
        {
            _uploadBudgetBlocked = true;
            location = BindlessTextureLocation.Unresolved;
            return false;
        }

        int layer = atlas.Slots.Rent();
        try
        {
            _backend.Upload(atlas.Resource, layer, decoded.Rgba8);
        }
        catch (Exception uploadFailure)
        {
            atlas.Slots.Return(layer);
            if (atlas.IsGpuSafeEmpty)
            {
                try { DeleteAtlas(atlas); }
                catch (Exception releaseFailure)
                {
                    throw new AggregateException(
                        "Composite upload and empty-atlas rollback both failed.",
                        uploadFailure,
                        releaseFailure);
                }
            }
            throw;
        }

        var entry = new Entry { Atlas = atlas, Layer = layer, Bytes = bytes };
        _entries.Add(key, entry);
        atlas.EntryCount++;
        atlas.LastUseSequence = ++_useSequence;
        _owners.Acquire(ownerLocalId, key);
        _frameUploadCount++;
        _frameUploadBytes = checked(_frameUploadBytes + bytes);
        location = new BindlessTextureLocation(atlas.Resource.Slot, checked((uint)layer));
        return true;
    }

    public void ReleaseOwner(uint ownerLocalId)
    {
        ThrowIfUnavailable();
        IReadOnlyList<CompositeTextureKey> unowned = _owners.ReleaseOwner(ownerLocalId);
        for (int i = 0; i < unowned.Count; i++)
        {
            CompositeTextureKey key = unowned[i];
            if (_entries.TryGetValue(key, out Entry? entry))
                _unowned.MarkUnowned(key, entry.Bytes);
        }
    }

    public void Tick()
    {
        ThrowIfUnavailable();
        _retirementLedger.RetryPendingPublications();

        bool allocationPressure = HasPendingAllocationPressure();
        bool physicalOverBudget = _allocatedBytes > _physicalBudgetBytes;
        bool needsPhysicalRelief = allocationPressure || physicalOverBudget;

        // Finish an already-started release before selecting another array.
        // This keeps driver-visible destruction bounded to one array per tick
        // even when a prior non-resident/delete stage had to be retried.
        bool servicedPendingRelease = CompleteOnePendingAtlasRelease();

        // A fence may have made an array safe since the prior frame. Free one
        // first; this is the only driver-visible destruction operation here.
        if (needsPhysicalRelief && !servicedPendingRelease)
            DeleteOneGpuSafeEmptyAtlas(
                _pendingAtlasAllocationBytes == 0 ? null : (_pendingAtlasWidth, _pendingAtlasHeight));

        allocationPressure = HasPendingAllocationPressure();
        physicalOverBudget = _allocatedBytes > _physicalBudgetBytes;
        needsPhysicalRelief = allocationPressure || physicalOverBudget;

        int evicted = 0;
        if (needsPhysicalRelief && _pendingAtlasAllocationBytes != 0)
        {
            evicted += EvictCompatibleUnowned(
                _pendingAtlasWidth,
                _pendingAtlasHeight,
                MaximumLogicalEvictionsPerFrame);
        }

        while (evicted < MaximumLogicalEvictionsPerFrame)
        {
            bool take = needsPhysicalRelief
                ? _unowned.TryTakeOldest(out CompositeTextureKey key)
                : _unowned.TryTakeOldestOverBudget(out key);
            if (!take)
                break;
            EvictEntry(key);
            evicted++;
        }

        // Allocation pressure is a one-frame demand signal. The requesting
        // entity will set it again later this frame if it is still relevant;
        // stale portal destinations must not keep evicting unrelated storage.
        _pendingAtlasWidth = 0;
        _pendingAtlasHeight = 0;
        _pendingAtlasAllocationBytes = 0;
    }

    internal void VisitEntries(Action<uint, int, int> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        foreach ((CompositeTextureKey key, Entry entry) in _entries)
            visitor(key.SurfaceId, entry.Atlas.Resource.Width, entry.Atlas.Resource.Height);
    }

    internal static int CalculateLayerCapacity(int width, int height, int driverMaximumLayers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(driverMaximumLayers, 1);

        long layerBytes = checked((long)width * height * 4L);
        long targetLayers = Math.Max(1L, TargetArrayBytes / layerBytes);
        return checked((int)Math.Min(
            targetLayers,
            Math.Min(driverMaximumLayers, MaximumLayersPerArray)));
    }

    private bool TryFindOrCreateAtlas(int width, int height, out Atlas atlas)
    {
        var size = (width, height);
        if (_atlasesBySize.TryGetValue(size, out List<Atlas>? compatible))
        {
            for (int i = 0; i < compatible.Count; i++)
            {
                Atlas candidate = compatible[i];
                if (candidate.IsReusable && candidate.AvailableLayers != 0)
                {
                    ClearPendingAllocation(width, height);
                    atlas = candidate;
                    return true;
                }
            }
        }

        int capacity = CalculateLayerCapacity(width, height, _maximumArrayLayers);
        long requestedBytes = checked((long)width * height * 4L * capacity);
        if (_frameAtlasCreationCount >= MaximumAtlasCreationsPerFrame
            || !CanAllocateAtlas(requestedBytes))
        {
            SetPendingAllocation(width, height, requestedBytes);
            atlas = null!;
            return false;
        }

        CompositeTextureArrayResource resource = _backend.Create(width, height, capacity);
        atlas = new Atlas
        {
            Resource = resource,
            Slots = new Wb.TextureAtlasSlotAllocator(capacity),
        };
        if (compatible is null)
        {
            compatible = new List<Atlas>();
            _atlasesBySize.Add(size, compatible);
        }
        compatible.Add(atlas);
        _atlases.Add(atlas);
        _allocatedBytes = checked(_allocatedBytes + resource.Bytes);
        _frameAtlasCreationCount++;
        ClearPendingAllocation(width, height);
        return true;
    }

    private bool CanAllocateAtlas(long requestedBytes)
    {
        if (FitsWithinPhysicalBudget(requestedBytes))
            return true;

        return !HasReclaimableStorage();
    }

    private bool FitsWithinPhysicalBudget(long requestedBytes)
    {
        if (requestedBytes > _physicalBudgetBytes)
            return _allocatedBytes == 0;
        return _allocatedBytes <= _physicalBudgetBytes - requestedBytes;
    }

    private bool HasPendingAllocationPressure() =>
        _pendingAtlasAllocationBytes != 0
        && !FitsWithinPhysicalBudget(_pendingAtlasAllocationBytes);

    private bool HasReclaimableStorage()
    {
        if (_unowned.Count != 0)
            return true;
        for (int i = 0; i < _atlases.Count; i++)
        {
            Atlas candidate = _atlases[i];
            if (!candidate.Deleted
                && (candidate.ReleaseRequested
                    || candidate.PendingRetirements != 0
                    || candidate.IsGpuSafeEmpty))
            {
                return true;
            }
        }
        return false;
    }

    private void SetPendingAllocation(int width, int height, long bytes)
    {
        _pendingAtlasWidth = width;
        _pendingAtlasHeight = height;
        _pendingAtlasAllocationBytes = bytes;
    }

    private void ClearPendingAllocation(int width, int height)
    {
        if (_pendingAtlasWidth != width || _pendingAtlasHeight != height)
            return;
        _pendingAtlasWidth = 0;
        _pendingAtlasHeight = 0;
        _pendingAtlasAllocationBytes = 0;
    }

    private int EvictCompatibleUnowned(int width, int height, int maximum)
    {
        _evictionScratch.Clear();
        foreach ((CompositeTextureKey key, Entry entry) in _entries)
        {
            if (_evictionScratch.Count == maximum)
                break;
            if (entry.Atlas.Resource.Width == width
                && entry.Atlas.Resource.Height == height
                && _unowned.Contains(key))
            {
                _evictionScratch.Add(key);
            }
        }

        int evicted = 0;
        for (int i = 0; i < _evictionScratch.Count; i++)
        {
            CompositeTextureKey key = _evictionScratch[i];
            if (!_unowned.TryTake(key))
                continue;
            EvictEntry(key);
            evicted++;
        }
        return evicted;
    }

    private void EvictEntry(CompositeTextureKey key)
    {
        if (!_entries.Remove(key, out Entry? entry))
            return;

        Atlas atlas = entry.Atlas;
        atlas.EntryCount--;
        atlas.PendingRetirements++;
        int layer = entry.Layer;
        _retirementLedger.Retire(new RetryableGpuResourceRelease(
            () => atlas.Slots.Return(layer),
            () => atlas.PendingRetirements--));
    }

    private bool CompleteOnePendingAtlasRelease()
    {
        for (int i = 0; i < _atlases.Count; i++)
        {
            Atlas atlas = _atlases[i];
            if (!atlas.ReleaseRequested)
                continue;
            DeleteAtlas(atlas);
            return true;
        }
        return false;
    }

    private void DeleteOneGpuSafeEmptyAtlas((int Width, int Height)? preserveSize = null)
    {
        Atlas? oldest = null;
        for (int i = 0; i < _atlases.Count; i++)
        {
            Atlas candidate = _atlases[i];
            if (preserveSize is { } preserve
                && candidate.Resource.Width == preserve.Width
                && candidate.Resource.Height == preserve.Height)
            {
                continue;
            }
            if (candidate.IsReusable
                && candidate.IsGpuSafeEmpty
                && (oldest is null || candidate.LastUseSequence < oldest.LastUseSequence))
            {
                oldest = candidate;
            }
        }

        if (oldest is not null)
            DeleteAtlas(oldest);
    }

    private void DeleteAtlas(Atlas atlas, bool requireGpuSafeEmpty = true)
    {
        if (atlas.ReleaseStage == AtlasReleaseStage.Accounted)
            return;
        if (requireGpuSafeEmpty && !atlas.IsGpuSafeEmpty)
            throw new InvalidOperationException("Cannot delete a composite array while a layer is live or retiring.");

        atlas.ReleaseRequested = true;
        if (atlas.ReleaseStage == AtlasReleaseStage.Resident)
        {
            try
            {
                _backend.MakeNonResident(atlas.Resource);
                atlas.ReleaseStage = AtlasReleaseStage.NonResident;
                RemoveFromReusableAtlasIndex(atlas);
            }
            catch (GpuResourceMutationException error) when (error.MutationCommitted)
            {
                atlas.ReleaseStage = AtlasReleaseStage.NonResident;
                RemoveFromReusableAtlasIndex(atlas);
                throw;
            }
        }
        if (atlas.ReleaseStage == AtlasReleaseStage.NonResident)
        {
            try
            {
                _backend.Delete(atlas.Resource);
                atlas.ReleaseStage = AtlasReleaseStage.Deleted;
            }
            catch (GpuResourceMutationException error) when (error.MutationCommitted)
            {
                atlas.ReleaseStage = AtlasReleaseStage.Deleted;
                throw;
            }
        }
        if (atlas.ReleaseStage == AtlasReleaseStage.Deleted)
        {
            _allocatedBytes = checked(_allocatedBytes - atlas.Resource.Bytes);
            _atlases.Remove(atlas);
            atlas.ReleaseStage = AtlasReleaseStage.Accounted;
        }
    }

    private void RevokeAtlasResidencyForDispose(Atlas atlas)
    {
        atlas.ReleaseRequested = true;
        if (atlas.ReleaseStage != AtlasReleaseStage.Resident)
            return;
        try
        {
            _backend.MakeNonResident(atlas.Resource);
            atlas.ReleaseStage = AtlasReleaseStage.NonResident;
            RemoveFromReusableAtlasIndex(atlas);
        }
        catch (GpuResourceMutationException error) when (error.MutationCommitted)
        {
            atlas.ReleaseStage = AtlasReleaseStage.NonResident;
            RemoveFromReusableAtlasIndex(atlas);
            throw;
        }
    }

    private void RemoveFromReusableAtlasIndex(Atlas atlas)
    {
        var size = (atlas.Resource.Width, atlas.Resource.Height);
        if (!_atlasesBySize.TryGetValue(size, out List<Atlas>? compatible))
            return;
        compatible.Remove(atlas);
        if (compatible.Count == 0)
            _atlasesBySize.Remove(size);
    }

    private static void ValidateDecodedTexture(DecodedTexture decoded)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(decoded.Width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(decoded.Height, 1);
        long expected = checked((long)decoded.Width * decoded.Height * 4L);
        if (decoded.Rgba8.LongLength != expected)
            throw new ArgumentException(
                $"Decoded RGBA texture has {decoded.Rgba8.LongLength} bytes; expected {expected}.",
                nameof(decoded));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposeRequested = true;
        _retirementLedger.RetryPendingPublications();

        // GameWindow drains frame-flight fences before TextureCache teardown.
        // Release every handle first, then delete any backing array. A failed
        // stage leaves its exact atlas/stage reachable for a later Dispose.
        List<Exception>? failures = null;
        for (int i = 0; i < _atlases.Count; i++)
        {
            Atlas atlas = _atlases[i];
            try { RevokeAtlasResidencyForDispose(atlas); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
        }
        if (failures is not null)
            throw new AggregateException("One or more composite-array residency releases failed.", failures);

        Atlas[] atlases = _atlases.ToArray();
        for (int i = 0; i < atlases.Length; i++)
        {
            try { DeleteAtlas(atlases[i], requireGpuSafeEmpty: false); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
        }
        if (failures is not null)
            throw new AggregateException("One or more composite-array deletions failed.", failures);

        _entries.Clear();
        _owners.Clear();
        _unowned.Clear();
        _atlasesBySize.Clear();
        _atlases.Clear();
        _allocatedBytes = 0;
        _pendingAtlasAllocationBytes = 0;
        _disposed = true;
    }

    private void ThrowIfUnavailable() =>
        ObjectDisposedException.ThrowIf(_disposeRequested || _disposed, this);
}
