using System.Numerics;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Scene;

internal readonly record struct RenderInstanceCandidate(
    RenderProjectionId ProjectionId,
    uint LocalEntityId,
    uint ServerGuid,
    uint SourceId,
    uint ParentCellId,
    Matrix4x4 RootWorld,
    Vector3 Position,
    Quaternion Rotation,
    float Scale,
    RenderWorldBounds Bounds,
    PaletteOverride? PaletteOverride,
    bool IsBuildingShell,
    bool Animated,
    int MeshPartCount,
    uint TupleLandblockId)
{
    internal uint Id => LocalEntityId;

    internal uint? ParentCell =>
        ParentCellId == 0 ? null : ParentCellId;

    internal uint CacheLandblockId =>
        ParentCellId != 0
            ? (ParentCellId & 0xFFFF0000u) | 0xFFFFu
            : TupleLandblockId;

    internal static RenderInstanceCandidate FromWorldEntity(
        WorldEntity entity,
        bool animated,
        int meshPartCount,
        uint tupleLandblockId)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (meshPartCount < 0)
            throw new ArgumentOutOfRangeException(nameof(meshPartCount));
        if (entity.AabbDirty)
            entity.RefreshAabb();

        return new RenderInstanceCandidate(
            ProjectionId: default,
            LocalEntityId: entity.Id,
            ServerGuid: entity.ServerGuid,
            SourceId: entity.SourceGfxObjOrSetupId,
            ParentCellId: entity.ParentCellId ?? 0u,
            RootWorld:
                Matrix4x4.CreateFromQuaternion(entity.Rotation)
                * Matrix4x4.CreateTranslation(entity.Position),
            Position: entity.Position,
            Rotation: entity.Rotation,
            Scale: entity.Scale,
            Bounds: new RenderWorldBounds(
                entity.AabbMin,
                entity.AabbMax),
            PaletteOverride: entity.PaletteOverride,
            IsBuildingShell: entity.IsBuildingShell,
            Animated: animated,
            MeshPartCount: meshPartCount,
            TupleLandblockId: tupleLandblockId);
    }

    internal static RenderInstanceCandidate FromProjection(
        in RenderProjectionRecord projection,
        uint tupleLandblockId,
        bool animated = false)
    {
        RenderEntityPayload payload = projection.EntityPayload;
        return new RenderInstanceCandidate(
            ProjectionId: projection.Id,
            LocalEntityId: projection.Source.LocalEntityId,
            ServerGuid: projection.Source.ServerGuid,
            SourceId: projection.Source.SourceId,
            ParentCellId: projection.Source.ParentCellId,
            RootWorld: projection.Transform.LocalToWorld,
            Position: projection.Transform.Position,
            Rotation: projection.Transform.Rotation,
            Scale: projection.Transform.UniformScale,
            Bounds: projection.Bounds,
            PaletteOverride: payload.PaletteOverride,
            IsBuildingShell: payload.IsBuildingShell,
            Animated: animated,
            MeshPartCount: payload.MeshRefs?.Count ?? 0,
            TupleLandblockId: tupleLandblockId);
    }

    internal static RenderInstanceCandidate FromFrame(
        in RenderFrameEntityCandidate source,
        uint tupleLandblockId)
    {
        RenderProjectionRecord projection = source.Projection;
        return new RenderInstanceCandidate(
            ProjectionId: projection.Id,
            LocalEntityId: projection.Source.LocalEntityId,
            ServerGuid: projection.Source.ServerGuid,
            SourceId: projection.Source.SourceId,
            ParentCellId: projection.Source.ParentCellId,
            RootWorld: projection.Transform.LocalToWorld,
            Position: projection.Transform.Position,
            Rotation: projection.Transform.Rotation,
            Scale: projection.Transform.UniformScale,
            Bounds: projection.Bounds,
            PaletteOverride:
                projection.EntityPayload.PaletteOverride,
            IsBuildingShell:
                projection.EntityPayload.IsBuildingShell,
            Animated: source.Animated,
            MeshPartCount: source.MeshPartCount,
            TupleLandblockId: tupleLandblockId);
    }
}

internal readonly record struct RenderInstanceTuple(
    RenderInstanceCandidate Candidate,
    int MeshRefIndex,
    MeshRef MeshRef);
