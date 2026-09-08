using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Physics;

internal sealed record LiveEntityCollisionRegistration(
    uint EntityId,
    uint SourceId,
    Vector3 EntityWorldPosition,
    Quaternion EntityWorldRotation,
    IReadOnlyList<ShadowShape> Shapes,
    uint State,
    EntityCollisionFlags Flags,
    float WorldOffsetX,
    float WorldOffsetY,
    uint LandblockId,
    uint SeedCellId,
    IReadOnlyList<ShadowShape> RenderParts);

internal sealed class LiveEntityCollisionBuilder
{
    private readonly Func<uint, ShadowPartGeometry?> _physicsBspBounds;
    private readonly Func<uint, bool> _hasPhysicsBsp;
    private readonly LiveEntityDefaultPoseResolver _defaultPose;

    private readonly Func<uint, GfxObjPhysics?> _getGfxObj;

    private readonly Func<uint, GfxObjVisualBounds?> _getVisualBounds;

    public LiveEntityCollisionBuilder(
        PhysicsDataCache physicsData,
        LiveEntityDefaultPoseResolver defaultPose)
        : this(
            id =>
            {
                FlatGfxObjCollisionAsset? asset = physicsData.GetFlatGfxObj(id);
                FlatPhysicsBsp? flat = asset?.PhysicsBsp;
                return flat is { RootIndex: >= 0 }
                    ? ShadowPartGeometry.Create(
                        flat.Nodes[flat.RootIndex].BoundingSphere,
                        asset!.VisualBounds)
                    : (ShadowPartGeometry?)null;
            },
            defaultPose,
            id => physicsData.GetGfxObj(id),
            id => physicsData.GetVisualBounds(id))
    {
        ArgumentNullException.ThrowIfNull(physicsData);
    }

    internal LiveEntityCollisionBuilder(
        Func<uint, ShadowPartGeometry?> physicsBspBounds,
        LiveEntityDefaultPoseResolver defaultPose,
        Func<uint, GfxObjPhysics?>? getGfxObj = null,
        Func<uint, GfxObjVisualBounds?>? getVisualBounds = null)
    {
        _physicsBspBounds = physicsBspBounds
            ?? throw new ArgumentNullException(nameof(physicsBspBounds));
        _hasPhysicsBsp = id => _physicsBspBounds(id) is not null;
        _defaultPose = defaultPose
            ?? throw new ArgumentNullException(nameof(defaultPose));
        _getGfxObj = getGfxObj ?? (_ => null);
        _getVisualBounds = getVisualBounds ?? (_ => null);
    }

    public static uint[] ResolveEffectivePartIdentities(
        IReadOnlyList<uint> postAnimPartGfxObjIds,
        Func<uint, uint> resolveSlotZero)
    {
        ArgumentNullException.ThrowIfNull(postAnimPartGfxObjIds);
        ArgumentNullException.ThrowIfNull(resolveSlotZero);
        var result = new uint[postAnimPartGfxObjIds.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = resolveSlotZero(postAnimPartGfxObjIds[i]);
        return result;
    }

    public LiveEntityCollisionRegistration? Build(
        WorldEntity entity,
        Setup setup,
        IReadOnlyList<uint> effectivePartGfxObjIds,
        WorldSession.EntitySpawn spawn,
        uint expectedServerGuid,
        ulong expectedGeneration,
        WorldEntity expectedEntity,
        PhysicsStateFlags expectedFinalPhysicsState,
        Vector3 worldOrigin,
        bool retainEmptyPayload = false)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(effectivePartGfxObjIds);
        ArgumentNullException.ThrowIfNull(expectedEntity);
        if (spawn.Position is not { } position)
            return null;
        if (spawn.Guid != expectedServerGuid
            || spawn.InstanceSequence != expectedGeneration
            || !ReferenceEquals(expectedEntity, entity))
        {
            throw new InvalidOperationException(
                "Live collision construction requires the exact materialized record.");
        }

        float scale = spawn.ObjScale ?? 1f;
        IReadOnlyList<Frame>? defaultPose = _defaultPose.Resolve(
            spawn.MotionTableId ?? 0u,
            setup.Parts.Count);
        IReadOnlyList<ShadowShape> shapes = ShadowShapeBuilder.FromSetup(
            setup,
            scale,
            _hasPhysicsBsp,
            partPoseOverride: defaultPose,
            effectivePartGfxObjIds: effectivePartGfxObjIds,
            physicsBspBounds: _physicsBspBounds);

        IReadOnlyList<ShadowShape> renderParts = ShadowShapeBuilder.FromSetupRenderParts(
            setup,
            scale,
            effectivePartGfxObjIds,
            defaultPose,
            _getGfxObj,
            _getVisualBounds);

        if (shapes.Count == 0 && renderParts.Count == 0 && !retainEmptyPayload)
            return null;

        EntityCollisionFlags flags = EntityCollisionFlags.HasWeenie;
        if (spawn.ObjectDescriptionFlags is { } descriptionFlags)
            flags |= EntityCollisionFlagsExt.FromPwdBitfield(descriptionFlags);
        if (spawn.ItemType == (uint)ItemType.Creature)
            flags |= EntityCollisionFlags.IsCreature;

        return new LiveEntityCollisionRegistration(
            entity.Id,
            entity.SourceGfxObjOrSetupId,
            entity.Position,
            entity.Rotation,
            shapes,
            (uint)expectedFinalPhysicsState,
            flags,
            worldOrigin.X,
            worldOrigin.Y,
            position.LandblockId,
            position.LandblockId,
            renderParts);
    }

    public static void Register(
        ShadowObjectRegistry registry,
        LiveEntityCollisionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(registration);
        registry.RegisterMultiPart(
            registration.EntityId,
            registration.EntityWorldPosition,
            registration.EntityWorldRotation,
            registration.Shapes,
            registration.State,
            registration.Flags,
            registration.WorldOffsetX,
            registration.WorldOffsetY,
            registration.LandblockId,
            registration.SeedCellId,
            isStatic: false,
            partArray: registration.RenderParts);

        if (!PhysicsDiagnostics.ProbeBuildingEnabled)
            return;

        int cylinders = 0;
        int bsps = 0;
        foreach (ShadowShape shape in registration.Shapes)
        {
            if (shape.CollisionType == ShadowCollisionType.Cylinder)
                cylinders++;
            else
                bsps++;
        }
        Console.WriteLine(FormattableString.Invariant(
            $"[entity-source] id=0x{registration.EntityId:X8} entityId=0x{registration.EntityId:X8} src=0x{registration.SourceId:X8} gfxObj=0x{registration.SourceId:X8} lb=0x{registration.LandblockId:X8} shapes=cyl{cylinders}+bsp{bsps} note=server-spawn-root state=0x{registration.State:X8} flags={registration.Flags}"));
    }

    public static void ReconcileAppearance(
        ShadowObjectRegistry registry,
        uint entityId,
        LiveEntityCollisionRegistration? registration,
        bool suspendIfNew)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (registration is null)
            return;

        if (registration.EntityId != entityId)
            throw new InvalidOperationException("Collision replacement belongs to a different live entity.");

        registry.ReplaceMultiPartPayload(
            registration.EntityId,
            registration.EntityWorldPosition,
            registration.EntityWorldRotation,
            registration.Shapes,
            registration.State,
            registration.Flags,
            registration.WorldOffsetX,
            registration.WorldOffsetY,
            registration.LandblockId,
            registration.SeedCellId,
            isStatic: false,
            suspendIfNew,
            partArray: registration.RenderParts);
    }
}
