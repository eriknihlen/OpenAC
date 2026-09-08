using DatReaderWriter.Enums;

namespace AcDream.Core.Meshing;

public static class RetailUntexturedSurfacePolicy
{
    public static bool IsUntextured(SurfaceType type) =>
        (type & (SurfaceType.Base1Image | SurfaceType.Base1ClipMap)) == 0;
}

public static class RetailUntexturedSubsetPolicy
{
    public static bool Draws(bool isBuildingShell, bool isUntextured) =>
        !isUntextured || !isBuildingShell;
}
