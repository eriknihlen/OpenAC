using System;
using AcDream.Core.Meshing;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Rendering;

public static class RetailParticleGeometryClassifier
{
    public static RetailParticleGeometryKind Classify(uint? firstDegradeMode)
        => firstDegradeMode is uint mode && mode != 1u
            ? RetailParticleGeometryKind.Billboard
            : RetailParticleGeometryKind.FullMesh;
}

public enum RetailParticleGeometryKind
{
    FullMesh,
    Billboard,
}

/// <summary>
/// Resolves authored particle-surface blending without allowing one malformed
/// Surface entry to abort the render frame. The fallback remains the batch's
/// already-decoded additive bit; it never fabricates geometry or a material.
/// </summary>
internal static class RetailParticleBlendResolver
{
    public static TranslucencyKind Resolve(
        uint surfaceId,
        bool batchIsAdditive,
        Func<uint, Surface?>? loadSurface,
        Action<string>? diagnostic = null)
    {
        TranslucencyKind fallback = batchIsAdditive
            ? TranslucencyKind.Additive
            : TranslucencyKind.AlphaBlend;
        if (surfaceId == 0 || loadSurface is null)
            return fallback;

        try
        {
            Surface? surface = loadSurface(surfaceId);
            return surface is null
                ? fallback
                : TranslucencyKindExtensions.FromSurfaceType(surface.Type);
        }
        catch (Exception ex)
        {
            diagnostic?.Invoke(
                $"[particle-material] Failed to decode Surface 0x{surfaceId:X8}: {ex.Message}");
            return fallback;
        }
    }
}
