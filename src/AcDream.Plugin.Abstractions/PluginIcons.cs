namespace AcDream.Plugin.Abstractions;

/// <summary>
/// Turns the two ways of writing an icon id into the one the client uses.
/// The host applies this wherever it accepts an icon id, so plugins rarely
/// call it themselves.
/// </summary>
public static class PluginIcons
{
    private const uint BareIndexBoundary = 0x01000000u;

    private const uint RenderSurfaceBlock = 0x06000000u;

    /// <summary>
    /// Returns a full icon id: a bare index below <c>0x01000000</c> gets the
    /// client's image-block prefix added, a full id passes through
    /// unchanged, and 0 (meaning "no icon") stays 0.
    /// </summary>
    public static uint Normalize(uint idOrIndex) =>
        idOrIndex == 0u
            ? 0u
            : idOrIndex < BareIndexBoundary
                ? RenderSurfaceBlock + idOrIndex
                : idOrIndex;
}
