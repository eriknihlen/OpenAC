namespace AcDream.App.Rendering;

/// <summary>Startup-resolved diagnostic gates for final PartArray presentation.</summary>
internal sealed record AnimationPresentationDiagnostics(
    bool RemoteVelocityEnabled,
    bool DumpMotionEnabled,
    Func<double> Now)
{
    public static AnimationPresentationDiagnostics FromEnvironment() => new(
        RemoteVelocityEnabled:
            Environment.GetEnvironmentVariable("ACDREAM_REMOTE_VEL_DIAG") == "1",
        DumpMotionEnabled:
            Environment.GetEnvironmentVariable("ACDREAM_DUMP_MOTION") == "1",
        Now: static () =>
            (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds);

    public static AnimationPresentationDiagnostics Disabled { get; } = new(
        false,
        false,
        static () => 0d);
}
