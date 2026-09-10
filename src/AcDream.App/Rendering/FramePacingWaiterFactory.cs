using AcDream.App.Platform;

namespace AcDream.App.Rendering;

internal interface IFramePacingWaiterFactory
{
    IFramePacingWaiter Create();
}

/// <summary>
/// The sole startup-time platform selector for software frame waits.
/// Frame policy and deadline behavior remain platform-neutral.
/// </summary>
internal sealed class PlatformFramePacingWaiterFactory
    : IFramePacingWaiterFactory
{
    private readonly GraphicalHostOperatingSystem _operatingSystem;

    internal PlatformFramePacingWaiterFactory(
        GraphicalHostOperatingSystem operatingSystem)
    {
        _operatingSystem = operatingSystem;
    }

    internal static PlatformFramePacingWaiterFactory ForCurrentProcess() =>
        new(GraphicalHostPlatformServices.DetectOperatingSystem());

    public IFramePacingWaiter Create() =>
        _operatingSystem switch
        {
            GraphicalHostOperatingSystem.Windows =>
                WindowsHighResolutionFramePacingWaiter.Create(),
            GraphicalHostOperatingSystem.Linux =>
                LinuxMonotonicFramePacingWaiter.Create(),
            GraphicalHostOperatingSystem.MacOS =>
                MacMonotonicFramePacingWaiter.Create(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(_operatingSystem)),
        };
}
