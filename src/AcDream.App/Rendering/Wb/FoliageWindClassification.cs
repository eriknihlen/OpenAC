using AcDream.Core.Meshing;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Wb;

internal static class FoliageWindClassification
{
    internal const uint CutoutFoliageFlag = 0x2u;

    internal const uint TrunkFlag = 0x4u;

    internal static uint Classify(
        uint entityId,
        bool isExcluded,
        TranslucencyKind translucency,
        bool meshHasCutoutSubset)
    {
        if (!IsProceduralScenery(entityId) || isExcluded)
            return 0u;
        if (translucency == TranslucencyKind.ClipMap)
            return CutoutFoliageFlag;
        if (translucency == TranslucencyKind.Opaque && meshHasCutoutSubset)
            return TrunkFlag;
        return 0u;
    }

    internal static bool ComputeEntityHasCutoutSubset<T, TContext>(
        IReadOnlyList<T> setupParts,
        TContext context,
        Func<TContext, T, bool> hasCutoutSubset)
    {
        for (int i = 0; i < setupParts.Count; i++)
        {
            if (hasCutoutSubset(context, setupParts[i]))
                return true;
        }
        return false;
    }

    internal static bool IsProceduralScenery(uint entityId) =>
        ProceduralSceneryIdAllocator.IsInNamespace(entityId);
}
