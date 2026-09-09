using AcDream.Core.Net;
using AcDream.Runtime.Session;
using AcDream.UI.Abstractions;

namespace AcDream.App.Net;

internal sealed class LiveSessionAppSource
    : ILiveInWorldSource,
      ILiveWorldSessionSource,
      ILiveUiSessionTarget
{
    private readonly LiveSessionController _session;
    private readonly LiveSessionCommandSurface _commands;

    public LiveSessionAppSource(
        LiveSessionController session,
        LiveSessionCommandSurface commands)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    public bool IsInWorld => _session.IsInWorld;

    public WorldSession? CurrentSession => _session.CurrentSession;

    public ICommandBus Commands => _commands;
}

internal sealed class LiveSessionCommandSurface : IPluginCommandBus
{
    private readonly object _gate = new();
    private readonly Func<string, bool>? _tryHandlePluginCommand;
    private LiveSessionCommandRouter? _active;

    public LiveSessionCommandSurface(Func<string, bool>? tryHandlePluginCommand = null)
    {
        _tryHandlePluginCommand = tryHandlePluginCommand;
    }

    public ILiveSessionCommandRouting Attach(LiveSessionCommandRouter route)
    {
        ArgumentNullException.ThrowIfNull(route);
        lock (_gate)
        {
            if (_active is not null)
            {
                throw new InvalidOperationException(
                    "A graphical live-session command route is already attached.");
            }

            _active = route;
            return new RouteLease(this, route);
        }
    }

    public void Publish<T>(T command) where T : notnull
    {
        LiveSessionCommandRouter? route;
        lock (_gate)
            route = _active;
        route?.Publish(command);
    }

    public bool TryHandlePluginCommand(string commandLine) =>
        _tryHandlePluginCommand?.Invoke(commandLine) == true;

    private void Release(LiveSessionCommandRouter expected)
    {
        expected.Dispose();
        lock (_gate)
        {
            if (ReferenceEquals(_active, expected))
                _active = null;
        }
    }

    private sealed class RouteLease(
        LiveSessionCommandSurface owner,
        LiveSessionCommandRouter route)
        : ILiveSessionCommandRouting
    {
        private readonly object _gate = new();
        private LiveSessionCommandSurface? _owner = owner;

        public void Activate() => route.Activate();

        public void Dispose()
        {
            lock (_gate)
            {
                if (_owner is null)
                    return;

                // Retain the owner until the route has physically become
                // inert so a failed teardown remains retryable.
                _owner.Release(route);
                _owner = null;
            }
        }
    }
}
