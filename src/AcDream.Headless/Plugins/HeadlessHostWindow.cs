using AcDream.Plugin.Abstractions;

namespace AcDream.Headless.Plugins;

/// <summary>
/// A headless session has no OS window, so Minimize/Restore/IsMinimized
/// keep the interface's inert defaults. RequestClose still has somewhere
/// real to go: it ends this plugin's own session -- not the whole
/// process -- through the same Stop()+terminal-status path a policy
/// deciding it is done already leaves for the process's final disposal;
/// every other session hosted by the same process keeps running
/// untouched. The process as a whole still ends when its last session
/// ends, exactly as it does today; only the console's own /quit and a
/// SIGINT/SIGTERM cancel the process-wide token that stops every session
/// at once.
/// </summary>
internal sealed class HeadlessHostWindow(Func<bool>? requestGracefulStop)
    : IHostWindow
{
    private const string NoWindowNotice = "the host has no window";
    private const string NotStoppedNotice = "the session could not be stopped";

    private readonly Func<bool>? _requestGracefulStop = requestGracefulStop;

    public HostWindowResult RequestClose()
    {
        if (_requestGracefulStop is null)
            return new HostWindowResult(HostWindowStatus.Unavailable, NoWindowNotice);

        bool accepted;
        try
        {
            // Guarded the same way the graphical host's RequestClose is:
            // a throw from the wired route (a disposed dependency it
            // reached mid-teardown, for instance) must report Unavailable
            // rather than escape into the plugin that called this.
            accepted = _requestGracefulStop();
        }
        catch
        {
            accepted = false;
        }

        return accepted
            ? new HostWindowResult(HostWindowStatus.Done)
            : new HostWindowResult(HostWindowStatus.Unavailable, NotStoppedNotice);
    }
}
