namespace AcDream.App.Rendering;

internal readonly record struct FramePacingPolicy(
    bool UseVSync,
    double? SoftwareLimitHz)
{
    internal const double FallbackRefreshHz = 60d;

    public static FramePacingPolicy Resolve(
        bool requestedVSync,
        bool uncappedRendering,
        int? monitorRefreshHz)
    {
        if (uncappedRendering)
            return new FramePacingPolicy(UseVSync: false, SoftwareLimitHz: null);

        if (requestedVSync)
            return new FramePacingPolicy(UseVSync: true, SoftwareLimitHz: null);

        double refreshHz = monitorRefreshHz is > 0
            ? monitorRefreshHz.Value
            : FallbackRefreshHz;
        return new FramePacingPolicy(UseVSync: false, SoftwareLimitHz: refreshHz);
    }
}
