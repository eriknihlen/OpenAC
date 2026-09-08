using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Physics;

public static class ShadowShapeBuilder
{
    public static IReadOnlyList<ShadowShape> FromSetup(
        Setup setup,
        float entScale,
        Func<uint, bool> hasPhysicsBsp,
        IReadOnlyList<Frame>? partPoseOverride = null,
        IReadOnlyList<uint>? effectivePartGfxObjIds = null,
        Func<uint, ShadowPartGeometry?>? physicsBspBounds = null)
    {
        if (setup is null) throw new ArgumentNullException(nameof(setup));
        if (hasPhysicsBsp is null) throw new ArgumentNullException(nameof(hasPhysicsBsp));

        var result = new List<ShadowShape>();

        bool anyPhysicsBspPart = false;
        for (int i = 0; i < setup.Parts.Count; i++)
        {
            if (hasPhysicsBsp(EffectivePartGfxObjId(setup, effectivePartGfxObjIds, i)))
            {
                anyPhysicsBspPart = true;
                break;
            }
        }

        // Steps 1 and 2 run ONLY for an object with no physics-BSP part.
        if (!anyPhysicsBspPart)
        {
            foreach (var cyl in setup.CylSpheres)
            {
                if (cyl.Radius <= 0f) continue;
                float baseHeight = cyl.Height > 0f ? cyl.Height : cyl.Radius * 4f;
                result.Add(ShadowShape.Cylinder(
                    gfxObjId:      0u,
                    localPosition: new Vector3(cyl.Origin.X, cyl.Origin.Y, cyl.Origin.Z) * entScale,
                    localRotation: Quaternion.Identity,
                    scale:         entScale,
                    radius:        cyl.Radius * entScale,
                    cylHeight:     baseHeight * entScale));
            }

            if (setup.CylSpheres.Count == 0)
            {
                foreach (var sph in setup.Spheres)
                {
                    if (sph.Radius <= 0f) continue;
                    result.Add(ShadowShape.Sphere(
                        gfxObjId:      0u,
                        localPosition: new Vector3(sph.Origin.X, sph.Origin.Y, sph.Origin.Z) * entScale,
                        localRotation: Quaternion.Identity,
                        scale:         entScale,
                        radius:        sph.Radius * entScale));
                }
            }
        }

        AnimationFrame? placementFrame = ResolvePlacementFrame(setup);
        for (int i = 0; i < setup.Parts.Count; i++)
        {
            uint gfxId = EffectivePartGfxObjId(setup, effectivePartGfxObjIds, i);
            if (!hasPhysicsBsp(gfxId)) continue;

            Frame partFrame;
            if (partPoseOverride is not null && i < partPoseOverride.Count)
                partFrame = partPoseOverride[i];
            else if (placementFrame is not null && i < placementFrame.Frames.Count)
                partFrame = placementFrame.Frames[i];
            else
                partFrame = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity };

            ShadowPartGeometry geometry =
                physicsBspBounds?.Invoke(gfxId)
                ?? ShadowPartGeometry.Create(
                    new FlatCollisionSphere(Vector3.Zero, 2f),
                    null);

            result.Add(ShadowShape.Bsp(
                gfxObjId:      gfxId,
                localPosition: new Vector3(partFrame.Origin.X, partFrame.Origin.Y, partFrame.Origin.Z) * entScale,
                localRotation: partFrame.Orientation,
                scale:         entScale,
                localGeometry: geometry));
        }

        return result;
    }

    public static List<ShadowShape> FromLandblockBspParts(
        IReadOnlyList<MeshRef> meshRefs,
        bool isBuildingShell,
        Func<uint, GfxObjPhysics?> getGfxObj)
    {
        if (getGfxObj is null) throw new ArgumentNullException(nameof(getGfxObj));

        var shapes = new List<ShadowShape>();
        if (isBuildingShell || meshRefs is null) return shapes;

        foreach (var meshRef in meshRefs)
        {
            var phys = getGfxObj(meshRef.GfxObjId);
            if (phys is null) continue;
            FlatPhysicsBsp? flat = phys.FlatPhysicsBsp;
            bool hasFlat = flat is { RootIndex: >= 0 };
            if (!hasFlat && phys.BSP?.Root is null)
                continue; // graph-only fixture seam until I6 referee removal

            if (!Matrix4x4.Decompose(meshRef.PartTransform,
                    out var pScale, out var pRot, out var pPos))
            {
                pScale = Vector3.One;
                pRot = Quaternion.Identity;
                pPos = new Vector3(meshRef.PartTransform.M41,
                                   meshRef.PartTransform.M42,
                                   meshRef.PartTransform.M43);
            }

            float partScale = pScale.X > 0f ? pScale.X : 1f;   // AC objects are uniformly scaled
            FlatCollisionSphere localBounds =
                hasFlat
                    ? flat!.Nodes[flat.RootIndex].BoundingSphere
                    : new FlatCollisionSphere(
                        phys.BoundingSphere?.Origin ?? Vector3.Zero,
                        phys.BoundingSphere?.Radius ?? 1f);

            ShadowPartGeometry geometry =
                ShadowPartGeometry.Create(localBounds, phys.VisualBounds);

            shapes.Add(ShadowShape.Bsp(
                gfxObjId:      meshRef.GfxObjId,
                localPosition: pPos,
                localRotation: pRot,
                scale:         partScale,
                localGeometry: geometry));
        }

        return shapes;
    }

    public static List<ShadowShape> FromStaticRenderParts(
        IReadOnlyList<MeshRef> meshRefs,
        Func<uint, GfxObjPhysics?> getGfxObj,
        Func<uint, GfxObjVisualBounds?> getVisualBounds,
        out bool hasPhysicsBsp)
    {
        ArgumentNullException.ThrowIfNull(meshRefs);
        ArgumentNullException.ThrowIfNull(getGfxObj);
        ArgumentNullException.ThrowIfNull(getVisualBounds);

        hasPhysicsBsp = false;
        var parts = new List<ShadowShape>(meshRefs.Count);
        foreach (MeshRef meshRef in meshRefs)
        {
            GfxObjPhysics? physics = getGfxObj(meshRef.GfxObjId);
            bool partHasPhysicsBsp = physics?.FlatPhysicsBsp is { RootIndex: >= 0 }
                || physics?.BSP?.Root is not null;
            hasPhysicsBsp |= partHasPhysicsBsp;

            FlatGfxObjVisualBounds? flatBounds = physics?.VisualBounds;
            if (flatBounds is null && getVisualBounds(meshRef.GfxObjId) is { } visual)
            {
                flatBounds = new FlatGfxObjVisualBounds(
                    visual.Min,
                    visual.Max,
                    visual.Center,
                    visual.Radius,
                    visual.HalfExtents);
            }
            if (flatBounds is not { } bounds)
                continue;

            if (!Matrix4x4.Decompose(
                    meshRef.PartTransform,
                    out Vector3 partScaleVector,
                    out Quaternion partRotation,
                    out Vector3 partPosition))
            {
                partScaleVector = Vector3.One;
                partRotation = Quaternion.Identity;
                partPosition = meshRef.PartTransform.Translation;
            }

            float partScale = partScaleVector.X > 0f ? partScaleVector.X : 1f;
            FlatCollisionSphere sphere;
            if (partHasPhysicsBsp)
            {
                FlatPhysicsBsp? flat = physics!.FlatPhysicsBsp;
                sphere = flat is { RootIndex: >= 0 }
                    ? flat.Nodes[flat.RootIndex].BoundingSphere
                    : new FlatCollisionSphere(
                        physics.BoundingSphere?.Origin ?? bounds.Center,
                        physics.BoundingSphere?.Radius ?? bounds.Radius);
            }
            else
            {
                sphere = new FlatCollisionSphere(bounds.Center, bounds.Radius);
            }

            parts.Add(ShadowShape.Bsp(
                meshRef.GfxObjId,
                partPosition,
                partRotation,
                partScale,
                ShadowPartGeometry.Create(sphere, bounds)));
        }

        return parts;
    }

    public static List<ShadowShape> FromSetupRenderParts(
        Setup setup,
        float entScale,
        IReadOnlyList<uint>? effectivePartGfxObjIds,
        IReadOnlyList<Frame>? partPoseOverride,
        Func<uint, GfxObjPhysics?> getGfxObj,
        Func<uint, GfxObjVisualBounds?> getVisualBounds)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(getGfxObj);
        ArgumentNullException.ThrowIfNull(getVisualBounds);

        var parts = new List<ShadowShape>(setup.Parts.Count);
        AnimationFrame? placementFrame = ResolvePlacementFrame(setup);
        for (int i = 0; i < setup.Parts.Count; i++)
        {
            uint gfxId = EffectivePartGfxObjId(setup, effectivePartGfxObjIds, i);

            GfxObjPhysics? physics = getGfxObj(gfxId);
            bool partHasPhysicsBsp = physics?.FlatPhysicsBsp is { RootIndex: >= 0 }
                || physics?.BSP?.Root is not null;

            FlatGfxObjVisualBounds? flatBounds = physics?.VisualBounds;
            if (flatBounds is null && getVisualBounds(gfxId) is { } visual)
            {
                flatBounds = new FlatGfxObjVisualBounds(
                    visual.Min,
                    visual.Max,
                    visual.Center,
                    visual.Radius,
                    visual.HalfExtents);
            }
            if (flatBounds is not { } bounds)
                continue;

            Frame partFrame;
            if (partPoseOverride is not null && i < partPoseOverride.Count)
                partFrame = partPoseOverride[i];
            else if (placementFrame is not null && i < placementFrame.Frames.Count)
                partFrame = placementFrame.Frames[i];
            else
                partFrame = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity };

            FlatCollisionSphere sphere;
            if (partHasPhysicsBsp)
            {
                FlatPhysicsBsp? flat = physics!.FlatPhysicsBsp;
                sphere = flat is { RootIndex: >= 0 }
                    ? flat.Nodes[flat.RootIndex].BoundingSphere
                    : new FlatCollisionSphere(
                        physics.BoundingSphere?.Origin ?? bounds.Center,
                        physics.BoundingSphere?.Radius ?? bounds.Radius);
            }
            else
            {
                sphere = new FlatCollisionSphere(bounds.Center, bounds.Radius);
            }

            parts.Add(ShadowShape.Bsp(
                gfxId,
                new Vector3(partFrame.Origin.X, partFrame.Origin.Y, partFrame.Origin.Z) * entScale,
                partFrame.Orientation,
                entScale,
                ShadowPartGeometry.Create(sphere, bounds)));
        }

        return parts;
    }

    private static uint EffectivePartGfxObjId(
        Setup setup,
        IReadOnlyList<uint>? effectivePartGfxObjIds,
        int index)
        => effectivePartGfxObjIds is not null && index < effectivePartGfxObjIds.Count
            ? effectivePartGfxObjIds[index]
            : (uint)setup.Parts[index];

    private static AnimationFrame? ResolvePlacementFrame(Setup setup)
    {
        if (setup.PlacementFrames.TryGetValue(Placement.Resting, out var resting)) return resting;
        if (setup.PlacementFrames.TryGetValue(Placement.Default, out var def))     return def;
        foreach (var kvp in setup.PlacementFrames) return kvp.Value;
        return null;
    }
}
