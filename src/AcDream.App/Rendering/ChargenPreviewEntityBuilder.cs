using System.Collections.Generic;
using System.Numerics;
using AcDream.Content;
using AcDream.Core.CharGen;
using AcDream.Core.Meshing;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Rendering;

internal readonly record struct ChargenPreviewDrawablePart(
    int SetupPartIndex,
    uint GfxObjId,
    Vector3 DefaultScale,
    IReadOnlyDictionary<uint, uint>? SurfaceOverrides);

internal sealed class ChargenPreviewAnimatedBuild
{
    public required WorldEntity Entity { get; init; }
    public required IReadOnlyList<ChargenPreviewDrawablePart> DrawableParts { get; init; }

    public required IReadOnlyList<MeshRef> RestMeshRefs { get; init; }

    public Animation? IdleAnimation { get; init; }
    public int IdleLowFrame { get; init; }
    public int IdleHighFrame { get; init; }
}

internal static class ChargenPreviewEntityBuilder
{
    public const uint PreviewServerGuid = 0xDA11_D031u;

    public const uint PreviewRenderId = 0xDA11_D032u;

    public const uint PreviewBackdropServerGuid = 0xDA11_D033u;

    public const uint PreviewBackdropRenderId = 0xDA11_D034u;

    public const uint SummaryPreviewRenderId = 0xDA11_D035u;

    public const uint SummaryPreviewBackdropRenderId = 0xDA11_D036u;

    private static uint ResolveRestPoseEnum(uint heritageId) => heritageId switch
    {
        (uint)ChargenHeritageGroup.Olthoi => 0x10000011u,
        (uint)ChargenHeritageGroup.OlthoiAcid => 0x10000013u,
        _ => 0x10000005u,
    };

    private static uint ResolveIdleAnimEnum(uint heritageId) => heritageId switch
    {
        (uint)ChargenHeritageGroup.Olthoi => 0x10000011u,
        (uint)ChargenHeritageGroup.OlthoiAcid => 0x10000013u,
        _ => 0x10000006u,
    };

    public static WorldEntity? TryBuild(
        IDatReaderWriter dats,
        IAnimationLoader animations,
        ChargenAppearanceResult appearance,
        uint heritageId,
        Quaternion heading,
        object datLock,
        uint renderId = PreviewRenderId)
    {
        ChargenPreviewAnimatedBuild? build = TryBuildAnimated(
            dats, animations, appearance, heritageId, heading, datLock, renderId);
        if (build is null)
            return null;

        build.Entity.MeshRefs = build.RestMeshRefs;
        return build.Entity;
    }

    public static ChargenPreviewAnimatedBuild? TryBuildAnimated(
        IDatReaderWriter dats,
        IAnimationLoader animations,
        ChargenAppearanceResult appearance,
        uint heritageId,
        Quaternion heading,
        object datLock,
        uint renderId = PreviewRenderId)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(animations);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(datLock);

        uint setupId = appearance.SetupId;
        List<ChargenPreviewDrawablePart> drawableParts;
        List<MeshRef> restMeshRefs;
        Animation? idleAnimation;
        int idleLowFrame = 0, idleHighFrame = -1;

        lock (datLock)
        {
            Setup? setup = dats.Get<Setup>(setupId);
            if (setup is null)
                return null;

            var flattened = new List<MeshRef>(SetupMesh.Flatten(setup));

            foreach (ChargenAnimPartChange change in appearance.ObjDesc.AnimPartChanges)
            {
                if (change.PartIndex < flattened.Count)
                    flattened[change.PartIndex] = new MeshRef(change.PartId, flattened[change.PartIndex].PartTransform);
            }

            // Rest pose: overwrite flattened's transforms with the held
            // final frame (no-op — keeps Setup-default transforms — if the
            // rest DID or its Animation don't resolve).
            ApplyHeldPoseTransforms(dats, animations, setup, ResolveRestPoseEnum(heritageId), flattened);

            Dictionary<int, Dictionary<uint, uint>>? surfaceOverrides =
                ResolveSurfaceOverrides(dats, flattened, appearance.ObjDesc.TextureChanges);

            drawableParts = new List<ChargenPreviewDrawablePart>(flattened.Count);
            restMeshRefs = new List<MeshRef>(flattened.Count);
            for (int partIndex = 0; partIndex < flattened.Count; partIndex++)
            {
                MeshRef part = flattened[partIndex];
                if (dats.Get<GfxObj>(part.GfxObjId) is null)
                    continue; // matches DatLiveEntityProjectionMaterializer's drawable filter.

                IReadOnlyDictionary<uint, uint>? overrides = null;
                if (surfaceOverrides is not null && surfaceOverrides.TryGetValue(partIndex, out var perPart))
                    overrides = perPart;

                restMeshRefs.Add(new MeshRef(part.GfxObjId, part.PartTransform) { SurfaceOverrides = overrides });

                Vector3 defaultScale = partIndex < setup.DefaultScale.Count
                    ? setup.DefaultScale[partIndex]
                    : Vector3.One;
                drawableParts.Add(new ChargenPreviewDrawablePart(partIndex, part.GfxObjId, defaultScale, overrides));
            }
            if (drawableParts.Count == 0)
                return null;

            // Idle DID: independent lookup, no mutation of flattened.
            uint idleDid = RetailHeldPose.ResolvePoseDid(dats, ResolveIdleAnimEnum(heritageId));
            idleAnimation = (idleDid >> 24) == 0x03u ? animations.LoadAnimation(idleDid) : null;
            if (idleAnimation is not null && idleAnimation.PartFrames.Count > 0)
            {
                idleLowFrame = 0;
                idleHighFrame = idleAnimation.PartFrames.Count - 1;
            }
            else
            {
                idleAnimation = null;
            }
        }

        var entity = new WorldEntity
        {
            Id = renderId,
            ServerGuid = PreviewServerGuid,
            SourceGfxObjOrSetupId = setupId,
            Position = Vector3.Zero,
            Rotation = heading,
            MeshRefs = restMeshRefs,
            PaletteOverride = BuildPaletteOverride(appearance),
            PartOverrides = BuildPartOverrides(appearance),
            ParentCellId = null,
        };

        return new ChargenPreviewAnimatedBuild
        {
            Entity = entity,
            DrawableParts = drawableParts,
            RestMeshRefs = restMeshRefs,
            IdleAnimation = idleAnimation,
            IdleLowFrame = idleLowFrame,
            IdleHighFrame = idleHighFrame,
        };
    }

    public static WorldEntity? TryBuildBackdrop(
        IDatReaderWriter dats,
        uint environmentSetupId,
        object datLock,
        uint renderId = PreviewBackdropRenderId)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(datLock);

        if (environmentSetupId == 0u)
            return null;

        lock (datLock)
        {
            Setup? setup = dats.Get<Setup>(environmentSetupId);
            if (setup is null)
                return null;

            var flattened = SetupMesh.Flatten(setup);
            var drawable = new List<MeshRef>(flattened.Count);
            foreach (MeshRef part in flattened)
            {
                if (dats.Get<GfxObj>(part.GfxObjId) is not null)
                    drawable.Add(part);
            }
            if (drawable.Count == 0)
                return null;

            return new WorldEntity
            {
                Id = renderId,
                ServerGuid = PreviewBackdropServerGuid,
                SourceGfxObjOrSetupId = environmentSetupId,
                Position = Vector3.Zero,
                Rotation = Quaternion.Identity,
                MeshRefs = drawable,
                ParentCellId = null,
            };
        }
    }

    private static PaletteOverride? BuildPaletteOverride(ChargenAppearanceResult appearance)
    {
        if (appearance.ObjDesc.SubPalettes.Count == 0)
            return null;

        var ranges = new PaletteOverride.SubPaletteRange[appearance.ObjDesc.SubPalettes.Count];
        for (int i = 0; i < appearance.ObjDesc.SubPalettes.Count; i++)
        {
            ChargenSubPalette sub = appearance.ObjDesc.SubPalettes[i];
            ranges[i] = new PaletteOverride.SubPaletteRange(sub.SubPaletteId, sub.Offset, sub.NumColors);
        }
        return new PaletteOverride(appearance.BasePaletteId, ranges);
    }

    private static PartOverride[] BuildPartOverrides(ChargenAppearanceResult appearance)
    {
        var partOverrides = new PartOverride[appearance.ObjDesc.AnimPartChanges.Count];
        for (int i = 0; i < appearance.ObjDesc.AnimPartChanges.Count; i++)
        {
            ChargenAnimPartChange change = appearance.ObjDesc.AnimPartChanges[i];
            partOverrides[i] = new PartOverride(change.PartIndex, change.PartId);
        }
        return partOverrides;
    }

    private static void ApplyHeldPoseTransforms(
        IDatReaderWriter dats,
        IAnimationLoader animations,
        Setup setup,
        uint poseEnum,
        List<MeshRef> flattened)
    {
        uint poseDid = RetailHeldPose.ResolvePoseDid(dats, poseEnum);
        if ((poseDid >> 24) != 0x03u)
            return;

        Animation? animation = animations.LoadAnimation(poseDid);
        if (animation is null || animation.PartFrames.Count == 0)
            return;

        var frame = animation.PartFrames[^1];
        for (int index = 0; index < flattened.Count; index++)
        {
            Vector3 scale = index < setup.DefaultScale.Count ? setup.DefaultScale[index] : Vector3.One;
            Vector3 origin = Vector3.Zero;
            Quaternion orientation = Quaternion.Identity;
            if (index < frame.Frames.Count)
            {
                origin = frame.Frames[index].Origin;
                orientation = frame.Frames[index].Orientation;
            }

            flattened[index] = new MeshRef(
                flattened[index].GfxObjId,
                RetailHeldPose.ComposePartTransform(scale, origin, orientation));
        }
    }

    private static Dictionary<int, Dictionary<uint, uint>>? ResolveSurfaceOverrides(
        IDatReaderWriter dats,
        IReadOnlyList<MeshRef> parts,
        IReadOnlyList<ChargenTextureChange> textureChanges)
    {
        if (textureChanges.Count == 0)
            return null;

        var oldToNewByPart = new Dictionary<int, Dictionary<uint, uint>>();
        foreach (ChargenTextureChange change in textureChanges)
        {
            if (!oldToNewByPart.TryGetValue(change.PartIndex, out var oldToNew))
            {
                oldToNew = [];
                oldToNewByPart.Add(change.PartIndex, oldToNew);
            }
            oldToNew[change.OldTextureId] = change.NewTextureId;
        }

        var result = new Dictionary<int, Dictionary<uint, uint>>();
        for (int partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            if (!oldToNewByPart.TryGetValue(partIndex, out var oldToNew))
                continue;

            GfxObj? gfx = dats.Get<GfxObj>(parts[partIndex].GfxObjId);
            if (gfx is null)
                continue;

            Dictionary<uint, uint>? resolved = null;
            foreach (var surfaceQid in gfx.Surfaces)
            {
                uint surfaceId = (uint)surfaceQid;
                Surface? surface = dats.Get<Surface>(surfaceId);
                if (surface is null)
                    continue;
                uint originalTexture = (uint)surface.OrigTextureId;
                if (originalTexture == 0 || !oldToNew.TryGetValue(originalTexture, out uint newTexture))
                    continue;

                (resolved ??= [])[surfaceId] = newTexture;
            }

            if (resolved is not null)
                result[partIndex] = resolved;
        }

        return result.Count == 0 ? null : result;
    }
}
