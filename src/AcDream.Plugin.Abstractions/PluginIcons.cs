namespace AcDream.Plugin.Abstractions;

public static class PluginIcons
{
    private const uint BareIndexBoundary = 0x01000000u;

    private const uint RenderSurfaceBlock = 0x06000000u;

    public static uint Normalize(uint idOrIndex) =>
        idOrIndex == 0u
            ? 0u
            : idOrIndex < BareIndexBoundary
                ? RenderSurfaceBlock + idOrIndex
                : idOrIndex;
}
