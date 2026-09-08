using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

public static class DollEntityBuilder
{
    public const uint DollServerGuid = 0xDA11_D011u;

    public const uint DollRenderId = 0xDA11_D012u;

    private const float _headingDegrees = 191.367905f;
    private static readonly float _headingRad = -_headingDegrees * (MathF.PI / 180f);
    private static readonly Quaternion _dollRotation =
        Quaternion.CreateFromAxisAngle(new Vector3(0f, 0f, 1f), _headingRad);

    public static WorldEntity Build(
        uint setupId,
        IReadOnlyList<MeshRef> meshRefs,
        uint? basePaletteId = null,
        IReadOnlyList<(uint SubPaletteId, byte Offset, byte Length)>? subPalettes = null,
        IReadOnlyList<(byte PartIndex, uint GfxObjId)>? partOverrides = null)
    {
        // --- palette override (mirrors GameWindow:3395-3405) ---
        // Only build when there are sub-palette overlays — same gate as GameWindow.
        PaletteOverride? paletteOverride = null;
        if (subPalettes is { Count: > 0 } spList)
        {
            var ranges = new PaletteOverride.SubPaletteRange[spList.Count];
            for (int i = 0; i < spList.Count; i++)
                ranges[i] = new PaletteOverride.SubPaletteRange(
                    spList[i].SubPaletteId,
                    spList[i].Offset,
                    spList[i].Length);
            paletteOverride = new PaletteOverride(
                BasePaletteId: basePaletteId ?? 0u,
                SubPalettes: ranges);
        }

        // --- part overrides (mirrors GameWindow:3407-3418) ---
        PartOverride[] entityPartOverrides;
        if (partOverrides is null or { Count: 0 })
        {
            entityPartOverrides = Array.Empty<PartOverride>();
        }
        else
        {
            entityPartOverrides = new PartOverride[partOverrides.Count];
            for (int i = 0; i < partOverrides.Count; i++)
                entityPartOverrides[i] = new PartOverride(
                    partOverrides[i].PartIndex,
                    partOverrides[i].GfxObjId);
        }

        return new WorldEntity
        {
            Id = DollRenderId,
            ServerGuid = DollServerGuid,
            SourceGfxObjOrSetupId = setupId,
            Position = Vector3.Zero,
            Rotation = _dollRotation,
            MeshRefs = meshRefs,
            PaletteOverride = paletteOverride,
            PartOverrides = entityPartOverrides,
            ParentCellId = null,
        };
    }
}
