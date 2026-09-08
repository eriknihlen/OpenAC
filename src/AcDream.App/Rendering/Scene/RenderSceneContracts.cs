using System.Numerics;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Scene;

internal readonly record struct RenderProjectionId
{
    private readonly ulong _value;

    private RenderProjectionId(ulong value) => _value = value;

    internal static RenderProjectionId FromRaw(ulong value) => new(value);

    internal ulong RawValue => _value;
    internal byte Domain => (byte)(_value >> 56);
    internal RenderSortKey ToSortKey() => new(_value);
    internal void AddTo(ref StableRenderHash128 hash) => hash.Add(_value);

    public int CompareTo(RenderProjectionId other) =>
        _value.CompareTo(other._value);

    public override string ToString() => $"projection:{_value:X16}";
}

internal readonly record struct RenderOwnerIncarnation
{
    private readonly ulong _value;

    private RenderOwnerIncarnation(ulong value) => _value = value;

    internal static RenderOwnerIncarnation FromRaw(ulong value) => new(value);

    internal ulong RawValue => _value;

    public int CompareTo(RenderOwnerIncarnation other) =>
        _value.CompareTo(other._value);

    public override string ToString() => $"incarnation:{_value}";
}

internal readonly record struct RenderSceneGeneration
{
    private readonly ulong _value;

    private RenderSceneGeneration(ulong value) => _value = value;

    internal static RenderSceneGeneration FromRaw(ulong value) => new(value);

    internal ulong RawValue => _value;

    public int CompareTo(RenderSceneGeneration other) =>
        _value.CompareTo(other._value);

    public override string ToString() => $"generation:{_value}";
}

internal readonly record struct RenderSpatialBucket
{
    private readonly ulong _value;

    private RenderSpatialBucket(ulong value) => _value = value;

    internal static RenderSpatialBucket FromRaw(ulong value) => new(value);

    internal ulong RawValue => _value;

    public int CompareTo(RenderSpatialBucket other) =>
        _value.CompareTo(other._value);

    public override string ToString() => $"bucket:{_value:X16}";
}

internal readonly record struct RenderAssetHandle
{
    private readonly ulong _value;

    private RenderAssetHandle(ulong value) => _value = value;

    internal static RenderAssetHandle FromRaw(ulong value) => new(value);

    internal ulong RawValue => _value;

    public int CompareTo(RenderAssetHandle other) =>
        _value.CompareTo(other._value);

    public override string ToString() => $"asset:{_value:X16}";
}

internal enum RenderProjectionClass : byte
{
    OutdoorStatic,
    IndoorCellStatic,
    LiveDynamicRoot,
    ActiveAnimatedStatic,
    EquippedChild,
}

[Flags]
internal enum RenderProjectionFlags : uint
{
    None = 0,
    Draw = 1 << 0,
    Hidden = 1 << 1,
    AncestorHidden = 1 << 2,
    Translucent = 1 << 3,
    Selectable = 1 << 4,
    LightCandidate = 1 << 5,
    PortalStraddling = 1 << 6,
    SpatiallyResident = 1 << 7,
}

[Flags]
internal enum RenderDirtyMask : ushort
{
    None = 0,
    Transform = 1 << 0,
    Appearance = 1 << 1,
    Flags = 1 << 2,
    SpatialResidency = 1 << 3,
    WorldBounds = 1 << 4,
    SortKey = 1 << 5,
    All = ushort.MaxValue,
}

internal readonly record struct RenderTransform(
    Vector3 Position,
    Quaternion Rotation,
    float UniformScale,
    Matrix4x4 LocalToWorld)
{
    public RenderTransform(Matrix4x4 localToWorld)
        : this(
            localToWorld.Translation,
            Quaternion.Identity,
            1.0f,
            localToWorld)
    {
    }

    public static RenderTransform FromRoot(
        Vector3 position,
        Quaternion rotation,
        float uniformScale) =>
        new(
            position,
            rotation,
            uniformScale,
            Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(position));
}

internal readonly record struct PreviousRenderTransform(Matrix4x4 LocalToWorld);

internal readonly record struct RenderMeshSet(
    RenderAssetHandle Handle,
    int MeshCount,
    ulong Revision);

internal readonly record struct RenderMaterialVariant(
    ulong PaletteKey,
    ulong TextureReplacementKey,
    float Opacity);

internal readonly record struct RenderSpatialResidency(
    RenderSpatialBucket Bucket,
    uint OwnerLandblockId,
    uint FullCellId);

internal readonly record struct RenderWorldBounds(Vector3 Minimum, Vector3 Maximum);

internal readonly record struct RenderDegradeState(byte Level, uint Revision);

internal readonly record struct RenderSortKey(ulong Value);

internal interface IRenderTraversalOrderSource
{
    bool TryGetTraversalSortKey(
        WorldEntity entity,
        out RenderSortKey sortKey);
}

internal static class RenderTraversalSortKey
{
    public static RenderSortKey Compose(
        uint landblockOrder,
        int entityIndex)
    {
        if (landblockOrder == 0)
            throw new ArgumentOutOfRangeException(nameof(landblockOrder));
        if (entityIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(entityIndex));

        return new RenderSortKey(
            ((ulong)landblockOrder << 32)
            | checked((uint)entityIndex));
    }
}

internal readonly record struct RenderSourceMetadata(
    uint LocalEntityId,
    uint ServerGuid,
    uint SourceId,
    uint ParentCellId,
    uint EffectCellId,
    uint BuildingShellAnchorCellId,
    RenderSceneHash128 TransformFingerprint,
    RenderSceneHash128 GeometryFingerprint,
    RenderSceneHash128 AppearanceFingerprint,
    uint CurrentProjectionFlags = 0,
    RenderSceneHash128 DirectionalShadowTopologyFingerprint = default);

internal enum RenderCasterIdentityKind : byte
{
    Unclassified,
    OutdoorStatic,
    Building,
    LocalPlayer,
    RemotePlayer,
    NonPlayerCreature,
    OtherLiveDynamic,
    EquippedChild,
}

internal readonly record struct RenderEntityPayload(
    IReadOnlyList<MeshRef> MeshRefs,
    PaletteOverride? PaletteOverride,
    bool IsBuildingShell,
    RenderCasterIdentityKind CasterIdentity =
        RenderCasterIdentityKind.Unclassified);

internal readonly record struct RenderProjectionRecord(
    RenderProjectionId Id,
    RenderProjectionClass ProjectionClass,
    RenderOwnerIncarnation OwnerIncarnation,
    RenderTransform Transform,
    PreviousRenderTransform PreviousTransform,
    RenderMeshSet MeshSet,
    RenderMaterialVariant Material,
    RenderSpatialResidency Residency,
    RenderWorldBounds Bounds,
    RenderProjectionFlags Flags,
    RenderDegradeState DegradeState,
    RenderSortKey SortKey,
    RenderDirtyMask DirtyMask,
    RenderSourceMetadata Source = default,
    RenderEntityPayload EntityPayload = default);

internal enum RenderProjectionDeltaKind : byte
{
    Register,
    UpdateTransform,
    UpdateAppearance,
    UpdateFlags,
    Rebucket,
    Unregister,
}

internal readonly record struct RenderProjectionDelta(
    RenderProjectionDeltaKind Kind,
    RenderSceneGeneration Generation,
    ulong JournalSequence,
    RenderProjectionRecord Record)
{
    public static RenderProjectionDelta Register(
        RenderSceneGeneration generation,
        ulong journalSequence,
        in RenderProjectionRecord record) =>
        new(
            RenderProjectionDeltaKind.Register,
            generation,
            journalSequence,
            record);

    public static RenderProjectionDelta Update(
        RenderProjectionDeltaKind kind,
        RenderSceneGeneration generation,
        ulong journalSequence,
        in RenderProjectionRecord record)
    {
        if (kind is RenderProjectionDeltaKind.Register
            or RenderProjectionDeltaKind.Unregister)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Use Register or Unregister for structural deltas.");
        }

        return new RenderProjectionDelta(kind, generation, journalSequence, record);
    }

    public static RenderProjectionDelta Unregister(
        RenderSceneGeneration generation,
        ulong journalSequence,
        RenderProjectionId id,
        RenderOwnerIncarnation ownerIncarnation) =>
        new(
            RenderProjectionDeltaKind.Unregister,
            generation,
            journalSequence,
            new RenderProjectionRecord() with
            {
                Id = id,
                OwnerIncarnation = ownerIncarnation,
            });
}

internal readonly record struct DynamicProjectionUpdate(
    RenderProjectionId Id,
    RenderOwnerIncarnation OwnerIncarnation,
    RenderTransform Transform,
    RenderWorldBounds Bounds);

internal readonly ref struct DynamicProjectionSyncInput
{
    public DynamicProjectionSyncInput(
        RenderSceneGeneration generation,
        ReadOnlySpan<DynamicProjectionUpdate> updates)
    {
        Generation = generation;
        Updates = updates;
    }

    public RenderSceneGeneration Generation { get; }

    public ReadOnlySpan<DynamicProjectionUpdate> Updates { get; }
}

internal readonly record struct RenderProjectionCounts(
    int Total,
    int OutdoorStatic,
    int IndoorCellStatic,
    int LiveDynamicRoot,
    int ActiveAnimatedStatic,
    int EquippedChild)
{
    public int For(RenderProjectionClass projectionClass) =>
        projectionClass switch
        {
            RenderProjectionClass.OutdoorStatic => OutdoorStatic,
            RenderProjectionClass.IndoorCellStatic => IndoorCellStatic,
            RenderProjectionClass.LiveDynamicRoot => LiveDynamicRoot,
            RenderProjectionClass.ActiveAnimatedStatic => ActiveAnimatedStatic,
            RenderProjectionClass.EquippedChild => EquippedChild,
            _ => throw new ArgumentOutOfRangeException(
                nameof(projectionClass),
                projectionClass,
                null),
        };
}

internal enum RenderSceneIndex : byte
{
    OutdoorStatic,
    IndoorCellStatic,
    Dynamic,
    OutdoorDynamic,
    PortalStraddlingDynamic,
    Translucent,
    Selectable,
    LightCandidate,
    Dirty,
}

internal readonly record struct RenderSceneIndexCounts(
    int OutdoorStatic,
    int IndoorCellStatic,
    int Dynamic,
    int OutdoorDynamic,
    int PortalStraddlingDynamic,
    int Translucent,
    int Selectable,
    int LightCandidate,
    int Dirty)
{
    public int For(RenderSceneIndex index) =>
        index switch
        {
            RenderSceneIndex.OutdoorStatic => OutdoorStatic,
            RenderSceneIndex.IndoorCellStatic => IndoorCellStatic,
            RenderSceneIndex.Dynamic => Dynamic,
            RenderSceneIndex.OutdoorDynamic => OutdoorDynamic,
            RenderSceneIndex.PortalStraddlingDynamic =>
                PortalStraddlingDynamic,
            RenderSceneIndex.Translucent => Translucent,
            RenderSceneIndex.Selectable => Selectable,
            RenderSceneIndex.LightCandidate => LightCandidate,
            RenderSceneIndex.Dirty => Dirty,
            _ => throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                null),
        };
}

internal readonly record struct RenderDeltaApplyResult(
    long Applied,
    long Registered,
    long Updated,
    long Replaced,
    long Unregistered,
    long RejectedGeneration,
    long RejectedOutOfOrderSequence,
    long RejectedStaleIncarnation,
    long RejectedMissing)
{
    public long Rejected =>
        RejectedGeneration
        + RejectedOutOfOrderSequence
        + RejectedStaleIncarnation
        + RejectedMissing;
}

internal readonly record struct RenderSceneMemoryAccounting(
    int EntityCount,
    int ArchEntityCapacity,
    int ArchetypeCount,
    int AllocatedChunkCount,
    long EstimatedChunkPayloadBytes,
    int ProjectionLookupCapacity,
    long EstimatedProjectionLookupBytes,
    long EstimatedIndexBytes,
    long EstimatedJournalBufferBytes,
    long EstimatedSynchronizationSourceBytes)
{
    public long TotalEstimatedBytes =>
        EstimatedChunkPayloadBytes
        + EstimatedProjectionLookupBytes
        + EstimatedIndexBytes
        + EstimatedJournalBufferBytes
        + EstimatedSynchronizationSourceBytes;
}

internal readonly record struct RenderSceneDigest(
    RenderSceneGeneration Generation,
    RenderProjectionCounts Counts,
    RenderSceneHash128 Hash);

internal static class DirectionalShadowTransformChangeJournal
{
    internal const int Capacity = 16_384;
}

internal readonly struct DirectionalShadowTransformSnapshot
{
    internal DirectionalShadowTransformSnapshot(
        RenderProjectionId id,
        RenderProjectionClass projectionClass,
        RenderTransform transform,
        RenderEntityPayload entityPayload)
    {
        Id = id;
        ProjectionClass = projectionClass;
        Transform = transform;
        EntityPayload = entityPayload;
    }

    internal readonly RenderProjectionId Id;
    internal readonly RenderProjectionClass ProjectionClass;
    internal readonly RenderTransform Transform;
    internal readonly RenderEntityPayload EntityPayload;

    internal static DirectionalShadowTransformSnapshot Capture(
        in RenderProjectionRecord projection) =>
        new(
            projection.Id,
            projection.ProjectionClass,
            projection.Transform,
            projection.EntityPayload);
}

internal readonly record struct DirectionalShadowTransformChanges(
    ulong LatestRevision,
    int Count,
    bool RequiresFullRefresh,
    int UpdateTransformCount = 0,
    int UpdateAppearanceCount = 0,
    int DynamicSynchronizationCount = 0,
    int ActiveAnimatedStaticCount = 0,
    int LiveDynamicRootCount = 0,
    int EquippedChildCount = 0);


internal enum DirectionalShadowTransformChangeKind : byte
{
    UpdateTransform,
    UpdateAppearance,
    DynamicSynchronization,
}

internal sealed class RenderSceneDigestBuffer
{
    internal List<RenderProjectionRecord> Records { get; } = [];

    internal int Capacity => Records.Capacity;
}

internal interface IRenderSceneQuerySource
{
    RenderProjectionCounts GetCounts(RenderSceneGeneration generation);
    RenderSceneIndexCounts GetIndexCounts(RenderSceneGeneration generation);
    ulong GetIndexRevision(RenderSceneGeneration generation);
    ulong GetDirectionalShadowTopologyRevision(
        RenderSceneGeneration generation);
    ulong GetDirectionalShadowTransformRevision(
        RenderSceneGeneration generation);

    DirectionalShadowTransformChanges CopyDirectionalShadowTransformChanges(
        RenderSceneGeneration generation,
        ulong afterRevision,
        Span<DirectionalShadowTransformSnapshot> destination);

    bool TryGet(
        RenderSceneGeneration generation,
        RenderProjectionId id,
        out RenderProjectionRecord record);

    bool TryGetByLocalEntityId(
        RenderSceneGeneration generation,
        uint localEntityId,
        out RenderProjectionRecord record);

    int CopyById(
        RenderSceneGeneration generation,
        ReadOnlySpan<RenderProjectionId> ids,
        Span<RenderProjectionRecord> destination);

    int CopyTo(
        RenderSceneGeneration generation,
        RenderProjectionClass? projectionClass,
        Span<RenderProjectionRecord> destination);

    int CopyIndexTo(
        RenderSceneGeneration generation,
        RenderSceneIndex index,
        Span<RenderProjectionRecord> destination);

}

internal readonly struct RenderSceneQuery
{
    private readonly IRenderSceneQuerySource? _source;

    internal RenderSceneQuery(
        IRenderSceneQuerySource source,
        RenderSceneGeneration generation)
    {
        _source = source;
        Generation = generation;
    }

    public RenderSceneGeneration Generation { get; }

    public RenderProjectionCounts Counts =>
        Source.GetCounts(Generation);

    public RenderSceneIndexCounts IndexCounts =>
        Source.GetIndexCounts(Generation);

    public ulong IndexRevision =>
        Source.GetIndexRevision(Generation);

    public ulong DirectionalShadowTopologyRevision =>
        Source.GetDirectionalShadowTopologyRevision(Generation);

    public ulong DirectionalShadowTransformRevision =>
        Source.GetDirectionalShadowTransformRevision(Generation);

    public DirectionalShadowTransformChanges CopyDirectionalShadowTransformChanges(
        ulong afterRevision,
        Span<DirectionalShadowTransformSnapshot> destination) =>
        Source.CopyDirectionalShadowTransformChanges(
            Generation,
            afterRevision,
            destination);

    public bool TryGet(
        RenderProjectionId id,
        out RenderProjectionRecord record) =>
        Source.TryGet(Generation, id, out record);

    public bool TryGetByLocalEntityId(
        uint localEntityId,
        out RenderProjectionRecord record) =>
        Source.TryGetByLocalEntityId(Generation, localEntityId, out record);

    public int CopyById(
        ReadOnlySpan<RenderProjectionId> ids,
        Span<RenderProjectionRecord> destination) =>
        Source.CopyById(Generation, ids, destination);

    public int CopyTo(Span<RenderProjectionRecord> destination) =>
        Source.CopyTo(Generation, null, destination);

    public int CopyTo(
        RenderProjectionClass projectionClass,
        Span<RenderProjectionRecord> destination) =>
        Source.CopyTo(Generation, projectionClass, destination);

    public int CopyIndexTo(
        RenderSceneIndex index,
        Span<RenderProjectionRecord> destination) =>
        Source.CopyIndexTo(Generation, index, destination);

    private IRenderSceneQuerySource Source =>
        _source
        ?? throw new InvalidOperationException("The render-scene query is uninitialized.");
}

internal interface IRenderScene : IDisposable
{
    RenderSceneGeneration Generation { get; }

    RenderProjectionCounts Counts { get; }

    RenderSceneMemoryAccounting Memory { get; }

    RenderDeltaApplyResult Apply(ReadOnlySpan<RenderProjectionDelta> deltas);

    void SynchronizeDynamicSources(in DynamicProjectionSyncInput input);

    RenderSceneDigest BuildDigest(RenderSceneDigestBuffer reuse);

    RenderSceneQuery OpenQuery();

    void ClearDirty();

    void Clear(RenderSceneGeneration replacementGeneration);
}
