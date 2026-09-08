using System.Numerics;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Scene;

internal static class RenderProjectionRecordFactory
{
    private const float DefaultAabbRadius = 5.0f;

    public static RenderProjectionRecord ProjectEntity(
        RenderProjectionId id,
        RenderProjectionClass projectionClass,
        RenderOwnerIncarnation incarnation,
        uint ownerLandblockId,
        uint fullCellId,
        WorldEntity entity,
        bool spatiallyVisible,
        RenderCasterIdentityKind casterIdentity =
            RenderCasterIdentityKind.Unclassified)
    {
        ArgumentNullException.ThrowIfNull(entity);
        CurrentRenderProjectionFingerprint fingerprint =
            CurrentRenderSceneOracle.CreateProjectionFingerprint(
                ownerLandblockId,
                entity);
        RenderProjectionFlags flags = RenderProjectionFlags.Selectable;
        if (spatiallyVisible)
            flags |= RenderProjectionFlags.SpatiallyResident;
        if (entity.MeshRefs.Count > 0
            && spatiallyVisible
            && entity.IsDrawVisible
            && entity.IsAncestorDrawVisible)
        {
            flags |= RenderProjectionFlags.Draw;
        }
        if (!entity.IsDrawVisible)
            flags |= RenderProjectionFlags.Hidden;
        if (!entity.IsAncestorDrawVisible)
            flags |= RenderProjectionFlags.AncestorHidden;

        RenderTransform transform = RenderTransform.FromRoot(
            entity.Position,
            entity.Rotation,
            entity.Scale);
        (Vector3 minimum, Vector3 maximum) = CalculateBounds(entity);
        return new RenderProjectionRecord(
            id,
            projectionClass,
            incarnation,
            transform,
            new PreviousRenderTransform(transform.LocalToWorld),
            new RenderMeshSet(
                RenderAssetHandle.FromRaw(
                    fingerprint.Geometry.Low ^ fingerprint.Geometry.High),
                entity.MeshRefs.Count,
                0),
            new RenderMaterialVariant(
                fingerprint.Appearance.Low,
                fingerprint.Appearance.High,
                1.0f),
            new RenderSpatialResidency(
                RenderSpatialBucket.FromRaw(fullCellId),
                ownerLandblockId,
                fullCellId),
            new RenderWorldBounds(minimum, maximum),
            flags,
            default,
            id.ToSortKey(),
            RenderDirtyMask.All,
            new RenderSourceMetadata(
                entity.Id,
                entity.ServerGuid,
                entity.SourceGfxObjOrSetupId,
                entity.ParentCellId ?? 0,
                entity.EffectCellId ?? 0,
                entity.BuildingShellAnchorCellId ?? 0,
                fingerprint.Transform,
                fingerprint.Geometry,
                fingerprint.Appearance,
                fingerprint.Flags,
                CurrentRenderSceneOracle
                    .CreateDirectionalShadowTopologyFingerprint(entity)),
            new RenderEntityPayload(
                entity.MeshRefs,
                entity.PaletteOverride,
                entity.IsBuildingShell,
                casterIdentity));
    }

    private static (Vector3 Minimum, Vector3 Maximum) CalculateBounds(
        WorldEntity entity)
    {
        Vector3 position = entity.Position;
        if (entity.HasLocalBounds)
        {
            Vector3 localMin = entity.LocalBoundMin;
            Vector3 localMax = entity.LocalBoundMax;
            Vector3 minimum = default;
            Vector3 maximum = default;
            for (int cornerIndex = 0; cornerIndex < 8; cornerIndex++)
            {
                Vector3 corner = new(
                    (cornerIndex & 1) == 0 ? localMin.X : localMax.X,
                    (cornerIndex & 2) == 0 ? localMin.Y : localMax.Y,
                    (cornerIndex & 4) == 0 ? localMin.Z : localMax.Z);
                Vector3 transformed =
                    Vector3.Transform(corner, entity.Rotation);
                if (cornerIndex == 0)
                {
                    minimum = transformed;
                    maximum = transformed;
                }
                else
                {
                    minimum = Vector3.Min(minimum, transformed);
                    maximum = Vector3.Max(maximum, transformed);
                }
            }

            Vector3 margin = new(DefaultAabbRadius);
            return (
                position + minimum - margin,
                position + maximum + margin);
        }

        float radius = DefaultAabbRadius;
        for (int i = 0; i < entity.MeshRefs.Count; i++)
        {
            radius = MathF.Max(
                radius,
                DefaultAabbRadius
                + entity.MeshRefs[i].PartTransform.Translation.Length());
        }

        Vector3 extent = new(radius);
        return (position - extent, position + extent);
    }
}
