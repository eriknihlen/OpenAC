namespace AcDream.App.Rendering;

internal sealed class StandaloneBindlessTextureResource
{
    public required uint SurfaceId { get; init; }

    /// <summary>GL texture name on the GL arm; zero on the RHI arm.</summary>
    public uint Name { get; init; }

    /// <summary>Resident bindless handle on the GL arm; zero on the RHI arm.</summary>
    public ulong Handle { get; init; }

    /// <summary>The device texture on the RHI arm; null on the GL arm.</summary>
    public Gpu.IGpuTexture? Texture { get; init; }

    public required Gpu.GpuTextureSlot Slot { get; init; }
    public required long Bytes { get; init; }

    /// <summary>
    /// Whether the resource names a texture at all. Exactly one arm must have
    /// filled it in; a resource that names neither would retire nothing and
    /// leak silently, which is what this guards.
    /// </summary>
    public bool IdentifiesATexture => (Name != 0 && Handle != 0) || Texture is not null;
}

internal interface IStandaloneBindlessTextureBackend
{
    void MakeNonResident(StandaloneBindlessTextureResource resource);
    void Delete(StandaloneBindlessTextureResource resource);
}

internal sealed class StandaloneBindlessTextureCache : IDisposable
{
    internal const long DefaultUnownedBudgetBytes = 32L * 1024 * 1024;
    internal const int DefaultMaximumUnownedCount = 256;
    internal const int DefaultMaximumEvictionsPerFrame = 1;

    private readonly IStandaloneBindlessTextureBackend _backend;
    private readonly GpuRetirementLedger _retirementLedger;
    private readonly OwnerScopedResourceRegistry<uint> _owners = new();
    private readonly BoundedUnownedResourceCache<uint> _unowned;
    private readonly Dictionary<uint, StandaloneBindlessTextureResource> _entries = new();
    private readonly HashSet<uint> _disposeResidencyReleased = [];
    private readonly HashSet<uint> _disposeDeleted = [];
    private long _allocatedBytes;
    private long _retiringBytes;
    private bool _disposeRequested;
    private bool _disposing;
    private bool _disposed;

    public StandaloneBindlessTextureCache(
        IStandaloneBindlessTextureBackend backend,
        IGpuResourceRetirementQueue retirementQueue,
        long unownedBudgetBytes = DefaultUnownedBudgetBytes,
        int maximumUnownedCount = DefaultMaximumUnownedCount)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        ArgumentNullException.ThrowIfNull(retirementQueue);
        _retirementLedger = new GpuRetirementLedger(retirementQueue);
        _unowned = new BoundedUnownedResourceCache<uint>(
            unownedBudgetBytes,
            maximumUnownedCount);
    }

    internal int EntryCount => _entries.Count;
    internal int ActiveResourceCount => _owners.ResourceCount;
    internal int OwnerCount => _owners.OwnerCount;
    internal int UnownedEntryCount => _unowned.Count;
    internal long UnownedBytes => _unowned.ResidentBytes;
    internal long AllocatedBytes => _allocatedBytes;
    internal long RetiringBytes => _retiringBytes;
    internal long BudgetBytes => _unowned.BudgetBytes;
    internal int AwaitingRetirementPublicationCount =>
        _retirementLedger.AwaitingPublicationCount;

    public bool TryAcquire(
        uint ownerId,
        uint surfaceId,
        out StandaloneBindlessTextureResource resource)
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        ValidateOwnerAndSurface(ownerId, surfaceId);
        if (!_entries.TryGetValue(surfaceId, out resource!))
            return false;

        _owners.Acquire(ownerId, surfaceId);
        _unowned.MarkOwned(surfaceId);
        return true;
    }

    public void AddAndAcquire(
        uint ownerId,
        StandaloneBindlessTextureResource resource)
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        ArgumentNullException.ThrowIfNull(resource);
        ValidateOwnerAndSurface(ownerId, resource.SurfaceId);
        if (!resource.IdentifiesATexture)
        {
            throw new ArgumentException(
                "A standalone particle texture must name either a GL texture and its "
                + "resident handle or a device texture (campaign plan slice V6l).",
                nameof(resource));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resource.Bytes);

        if (!_entries.TryAdd(resource.SurfaceId, resource))
            throw new InvalidOperationException(
                $"Standalone particle surface 0x{resource.SurfaceId:X8} is already cached.");

        _owners.Acquire(ownerId, resource.SurfaceId);
        _allocatedBytes = checked(_allocatedBytes + resource.Bytes);
    }

    public void ReleaseOwner(uint ownerId)
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        if (ownerId == 0)
            return;

        IReadOnlyList<uint> newlyUnowned = _owners.ReleaseOwner(ownerId);
        for (int i = 0; i < newlyUnowned.Count; i++)
        {
            uint surfaceId = newlyUnowned[i];
            if (_entries.TryGetValue(surfaceId, out StandaloneBindlessTextureResource? resource))
                _unowned.MarkUnowned(surfaceId, resource.Bytes);
        }

    }

    internal void VisitEntries(Action<StandaloneBindlessTextureResource> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        foreach (StandaloneBindlessTextureResource resource in _entries.Values)
            visitor(resource);
    }

    public void Tick(int maximumEvictions = DefaultMaximumEvictionsPerFrame)
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEvictions);
        _retirementLedger.RetryPendingPublications();

        for (int i = 0; i < maximumEvictions; i++)
        {
            if (!_unowned.TryTakeOldestOverBudget(out uint surfaceId))
                break;
            if (!_entries.Remove(surfaceId, out StandaloneBindlessTextureResource? resource))
                continue;

            _retiringBytes = checked(_retiringBytes + resource.Bytes);
            _retirementLedger.Retire(new RetryableGpuResourceRelease(
                () => _backend.MakeNonResident(resource),
                () => _backend.Delete(resource),
                () => _retiringBytes = checked(
                    _retiringBytes - resource.Bytes),
                () => _allocatedBytes = checked(
                    _allocatedBytes - resource.Bytes)));
        }
    }

    private static void ValidateOwnerAndSurface(uint ownerId, uint surfaceId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(ownerId);
        ArgumentOutOfRangeException.ThrowIfZero(surfaceId);
    }

    public void Dispose()
    {
        if (_disposed || _disposing)
            return;
        _disposeRequested = true;
        _disposing = true;

        try
        {
            // GameWindow drains the frame-flight queue before TextureCache
            // teardown. Release every handle before deleting any texture so the
            // ARB_bindless_texture lifetime ordering remains explicit.
            List<Exception>? failures = null;
            try
            {
                _retirementLedger.RetryPendingPublications();
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }

            foreach (StandaloneBindlessTextureResource resource in _entries.Values)
            {
                if (_disposeResidencyReleased.Contains(resource.SurfaceId))
                    continue;
                try
                {
                    _backend.MakeNonResident(resource);
                    _disposeResidencyReleased.Add(resource.SurfaceId);
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }

            foreach (StandaloneBindlessTextureResource resource in _entries.Values)
            {
                if (!_disposeResidencyReleased.Contains(resource.SurfaceId)
                    || _disposeDeleted.Contains(resource.SurfaceId))
                {
                    continue;
                }

                try
                {
                    _backend.Delete(resource);
                    _disposeDeleted.Add(resource.SurfaceId);
                    _allocatedBytes = checked(
                        _allocatedBytes - resource.Bytes);
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }

            if (_disposeDeleted.Count != 0)
            {
                foreach (uint surfaceId in _disposeDeleted)
                    _entries.Remove(surfaceId);
            }

            if (_entries.Count == 0
                && _retirementLedger.AwaitingPublicationCount == 0)
            {
                _owners.Clear();
                _unowned.Clear();
                _disposeResidencyReleased.Clear();
                _disposeDeleted.Clear();
                _disposed = true;
            }

            if (failures is not null)
            {
                throw new AggregateException(
                    "One or more standalone particle textures failed to retire.",
                    failures);
            }
        }
        finally
        {
            _disposing = false;
        }
    }
}
