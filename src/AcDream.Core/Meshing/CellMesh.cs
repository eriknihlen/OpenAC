using System;
using System.Collections.Generic;
using AcDream.Core.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Core.Meshing;

public static class CellMesh
{
    public static bool HasDrawableGeometry(EnvCell envCell, CellStruct cellStruct, IDatObjectSource dats)
    {
        var slotIsTextured = new Dictionary<int, bool>();

        bool SlotIsTextured(int slot)
        {
            if (slotIsTextured.TryGetValue(slot, out bool cached))
                return cached;

            bool textured = false;
            if (slot >= 0 && slot < envCell.Surfaces.Count)
            {
                uint surfaceId = 0x08000000u | envCell.Surfaces[slot];
                if (dats.Get<Surface>(surfaceId) is { } surface)
                    textured = !RetailUntexturedSurfacePolicy.IsUntextured(surface.Type);
            }
            slotIsTextured[slot] = textured;
            return textured;
        }

        foreach (var poly in cellStruct.Polygons.Values)
        {
            // Same degenerate-fan gate as MeshExtractor.PrepareCellStructMeshData.
            if (poly.VertexIds.Count < 3) continue;

            ReadOnlySpan<CellStructSideCandidate> candidates =
                CellStructSideCandidates.GetCandidates((int)poly.SidesType);

            foreach (var candidate in candidates)
            {
                short surfaceIdxRaw = candidate.SurfaceSlot == CellStructPolygonSurfaceSide.Positive
                    ? poly.PosSurface
                    : poly.NegSurface;
                if (surfaceIdxRaw < 0) continue;

                if (SlotIsTextured(surfaceIdxRaw))
                    return true;
            }
        }

        return false;
    }
}
