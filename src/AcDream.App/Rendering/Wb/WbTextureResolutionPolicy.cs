namespace AcDream.App.Rendering.Wb;

internal enum WbTextureResolutionKind : byte
{
    SharedAtlas,
    OriginalTextureOverride,
    PaletteComposite,
}

internal static class WbTextureResolutionPolicy
{
    public static WbTextureResolutionKind Select(
        bool hasOriginalTextureOverride,
        bool hasPaletteOverride,
        bool sourceIsPaletteIndexed)
    {
        if (hasPaletteOverride && sourceIsPaletteIndexed)
            return WbTextureResolutionKind.PaletteComposite;

        return hasOriginalTextureOverride
            ? WbTextureResolutionKind.OriginalTextureOverride
            : WbTextureResolutionKind.SharedAtlas;
    }
}
