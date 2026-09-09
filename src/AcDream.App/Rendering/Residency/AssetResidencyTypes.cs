namespace AcDream.App.Rendering.Residency;

internal enum ResidencyDomain : byte
{
    ObjectMeshes,
    PreparedMeshCpu,
    MeshStaging,
    GlobalMeshArena,
    CompositeTextures,
    StandaloneTextures,
    PreparedPackage,
    Animations,
    Audio,
    AlphaScratch,
}

internal enum ResidencyPriority : byte
{
    Speculative,
    Far,
    Near,
    Visible,
    DestinationCritical,
}

internal enum AssetResidencyState : byte
{
    Absent,
    Requested,
    Prepared,
    UploadPending,
    Resident,
    Retiring,
    Cancelled,
    Missing,
    Corrupt,
    Failed,
}

internal enum ResidencyOwnerKind : byte
{
    Session,
    Landblock,
    Entity,
    ParticleEmitter,
    UserInterface,
    Tooling,
}

internal readonly record struct ResidencyAssetKey(
    ResidencyDomain Domain,
    ulong ContentKey,
    ulong Variant = 0);

internal readonly record struct AssetHandle<TAsset>(uint Index, ushort Generation)
{
    public bool IsValid => Index != 0 && Generation != 0;

    internal AssetReference Untyped =>
        new(Index, Generation, typeof(TAsset).TypeHandle);
}

internal readonly record struct OwnerToken(uint Index, ushort Generation)
{
    public bool IsValid => Index != 0 && Generation != 0;
}

internal readonly record struct AssetLease<TAsset>(
    AssetHandle<TAsset> Handle,
    OwnerToken Owner);

internal readonly record struct AssetReference(
    uint Index,
    ushort Generation,
    RuntimeTypeHandle AssetType)
{
    public bool IsValid =>
        Index != 0
        && Generation != 0
        && !AssetType.Equals(default(RuntimeTypeHandle));
}

internal readonly record struct ResidencyCharges(
    long LogicalBytes = 0,
    long CpuPreparedBytes = 0,
    long DecodedBytes = 0,
    long ScratchBytes = 0,
    long PinnedBytes = 0,
    long StagingBytes = 0,
    long GpuRequestedBytes = 0,
    long GpuResidentBytes = 0,
    long RetiringBytes = 0,
    long MappedVirtualBytes = 0)
{
    public static ResidencyCharges Zero => default;

    public long CommittedCpuBytes => checked(
        LogicalBytes
        + CpuPreparedBytes
        + DecodedBytes
        + ScratchBytes
        + StagingBytes);

    public long PhysicalGpuBytes => checked(
        GpuRequestedBytes
        + GpuResidentBytes
        + RetiringBytes);

    public bool IsZero => this == default;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(LogicalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(CpuPreparedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(DecodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(ScratchBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(PinnedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(StagingBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(GpuRequestedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(GpuResidentBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(RetiringBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MappedVirtualBytes);
    }

    public static ResidencyCharges operator +(
        ResidencyCharges left,
        ResidencyCharges right) =>
        new(
            checked(left.LogicalBytes + right.LogicalBytes),
            checked(left.CpuPreparedBytes + right.CpuPreparedBytes),
            checked(left.DecodedBytes + right.DecodedBytes),
            checked(left.ScratchBytes + right.ScratchBytes),
            checked(left.PinnedBytes + right.PinnedBytes),
            checked(left.StagingBytes + right.StagingBytes),
            checked(left.GpuRequestedBytes + right.GpuRequestedBytes),
            checked(left.GpuResidentBytes + right.GpuResidentBytes),
            checked(left.RetiringBytes + right.RetiringBytes),
            checked(left.MappedVirtualBytes + right.MappedVirtualBytes));
}

internal readonly record struct ResidencyEntrySnapshot(
    AssetReference Asset,
    ResidencyAssetKey Key,
    AssetResidencyState State,
    ResidencyPriority Priority,
    ulong WorldGeneration,
    long LastUsedFrame,
    long RebuildCost,
    int OwnerCount,
    ResidencyCharges Charges,
    string? Failure);

internal readonly record struct ResidencyTrimRequest(
    AssetReference Asset,
    ResidencyAssetKey Key,
    ResidencyCharges Charges);

internal readonly record struct ResidencyDomainSnapshot(
    ResidencyDomain Domain,
    int EntryCount,
    int OwnerCount,
    ResidencyCharges Charges,
    long BudgetBytes = 0,
    long CapacityBytes = 0,
    long UsedBytes = 0,
    long LargestFreeBytes = 0,
    long Hits = 0,
    long Misses = 0,
    long Evictions = 0)
{
    public long FreeBytes => Math.Max(0, CapacityBytes - UsedBytes);

    public long FragmentedFreeBytes =>
        Math.Max(0, FreeBytes - LargestFreeBytes);

    public void Validate()
    {
        if (!Enum.IsDefined(Domain))
            throw new ArgumentOutOfRangeException(nameof(Domain));
        ArgumentOutOfRangeException.ThrowIfNegative(EntryCount);
        ArgumentOutOfRangeException.ThrowIfNegative(OwnerCount);
        Charges.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(BudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(CapacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(UsedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(LargestFreeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(Hits);
        ArgumentOutOfRangeException.ThrowIfNegative(Misses);
        ArgumentOutOfRangeException.ThrowIfNegative(Evictions);
        if (UsedBytes > CapacityBytes && CapacityBytes != 0)
        {
            throw new InvalidOperationException(
                $"{Domain} residency uses {UsedBytes} bytes from "
                + $"{CapacityBytes} bytes of physical capacity.");
        }
        if (LargestFreeBytes > CapacityBytes)
        {
            throw new InvalidOperationException(
                $"{Domain} largest free range exceeds physical capacity.");
        }
    }
}

internal readonly record struct ResidencySnapshot(
    IReadOnlyList<ResidencyDomainSnapshot> Domains,
    ResidencyCharges TotalCharges)
{
    public ResidencyDomainSnapshot Get(ResidencyDomain domain) =>
        Domains?.FirstOrDefault(snapshot => snapshot.Domain == domain)
        ?? default;

    public int TotalEntries =>
        Domains?.Sum(static snapshot => snapshot.EntryCount) ?? 0;

    public int TotalOwners =>
        Domains?.Sum(static snapshot => snapshot.OwnerCount) ?? 0;

    public long TotalBudgetBytes =>
        Domains?.Sum(static snapshot => snapshot.BudgetBytes) ?? 0;

    public long TotalCapacityBytes =>
        Domains?.Sum(static snapshot => snapshot.CapacityBytes) ?? 0;

    public long TotalUsedBytes =>
        Domains?.Sum(static snapshot => snapshot.UsedBytes) ?? 0;

    public long TotalFragmentedFreeBytes =>
        Domains?.Sum(static snapshot => snapshot.FragmentedFreeBytes) ?? 0;

    public long TotalHits =>
        Domains?.Sum(static snapshot => snapshot.Hits) ?? 0;

    public long TotalMisses =>
        Domains?.Sum(static snapshot => snapshot.Misses) ?? 0;

    public long TotalEvictions =>
        Domains?.Sum(static snapshot => snapshot.Evictions) ?? 0;
}

internal interface IResidencyDomainSource
{
    ResidencyDomain Domain { get; }

    ResidencyDomainSnapshot CaptureResidency();
}

internal enum ResidencyObservationKind : byte
{
    Transition,
    Touch,
}

internal readonly record struct ResidencyObservation(
    AssetReference Asset,
    ResidencyObservationKind Kind,
    AssetResidencyState State,
    ResidencyCharges Charges,
    ResidencyPriority Priority,
    long Frame,
    string? Failure = null);
