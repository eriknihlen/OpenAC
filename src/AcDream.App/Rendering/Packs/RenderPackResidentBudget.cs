namespace AcDream.App.Rendering.Packs;

internal static class RenderPackResidentBudget
{
    internal const long ReferencePixels = 1920L * 1080L;

    internal static long Effective(
        long declaredBytesAt1080p,
        int viewportWidth,
        int viewportHeight,
        long hardwareCapBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(declaredBytesAt1080p);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(hardwareCapBytes);

        long pixels = checked((long)viewportWidth * viewportHeight);
        long scaled = pixels <= ReferencePixels
            ? declaredBytesAt1080p
            : checked((long)Math.Ceiling(
                (double)declaredBytesAt1080p * pixels / ReferencePixels));
        return Math.Min(scaled, hardwareCapBytes);
    }
}
