using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

// A host whose navigation can tell plugins apart hands each plugin its own
// view through the scoped host, and lets that plugin's walks and pauses go
// when the plugin is disposed. A host whose navigation cannot is forwarded
// as it is.
public sealed class ScopedNavigationTests
{
    [Fact]
    public void APluginGetsItsOwnNavigationViewAndItIsReleasedWhenThePluginGoes()
    {
        var navigation = new ScopingNavigation();
        var host = new StubHost(navigation);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        INavigationAutomation first = scoped.Automation.Navigation;
        INavigationAutomation again = scoped.Automation.Navigation;

        Assert.Same(first, again);
        Assert.NotSame(navigation, first);
        Assert.Equal(["example.plugin"], navigation.Scoped);
        Assert.Empty(navigation.Released);

        scoped.Dispose();

        Assert.Equal(["example.plugin"], navigation.Released);
    }

    [Fact]
    public void AHostWhoseNavigationCannotTellPluginsApartIsForwardedAsItIs()
    {
        var host = new StubHost(NoOpAutomationSurface.Instance.Navigation);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        Assert.Same(host.Automation.Navigation, scoped.Automation.Navigation);

        scoped.Dispose();
    }

    [Fact]
    public void ANavigationSwappedMidSessionReleasesTheOldOneAndScopesTheNew()
    {
        var first = new ScopingNavigation();
        var second = new ScopingNavigation();
        var host = new StubHost(first);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        _ = scoped.Automation.Navigation;
        host.Navigation = second;
        _ = scoped.Automation.Navigation;

        Assert.Equal(["example.plugin"], first.Released);
        Assert.Equal(["example.plugin"], second.Scoped);
        scoped.Dispose();
        Assert.Equal(["example.plugin"], second.Released);
    }

    private sealed class ScopingNavigation : INavigationAutomation, IScopedNavigationSource
    {
        public List<string> Scoped { get; } = [];
        public List<string> Released { get; } = [];

        public INavigationAutomation ScopeTo(string ownerId)
        {
            Scoped.Add(ownerId);
            return new View();
        }

        public void Release(string ownerId) => Released.Add(ownerId);

        public PluginNavigationSnapshot Snapshot => default;

        public bool TryGetObject(uint objectId, out PluginNavigationObject value)
        {
            value = default;
            return false;
        }

        public bool TryFindObject(
            string name,
            in PluginNavigationPosition near,
            double maximumDistanceMeters,
            out PluginNavigationObject value)
        {
            value = default;
            return false;
        }

        public PluginNavigationCommandStatus SetMovementIntent(in PluginMovementIntent intent) =>
            PluginNavigationCommandStatus.Unavailable;

        public PluginNavigationCommandStatus ClearMovementIntent() =>
            PluginNavigationCommandStatus.Unavailable;

        private sealed class View : INavigationAutomation
        {
            public PluginNavigationSnapshot Snapshot => default;

            public bool TryGetObject(uint objectId, out PluginNavigationObject value)
            {
                value = default;
                return false;
            }

            public bool TryFindObject(
                string name,
                in PluginNavigationPosition near,
                double maximumDistanceMeters,
                out PluginNavigationObject value)
            {
                value = default;
                return false;
            }

            public PluginNavigationCommandStatus SetMovementIntent(in PluginMovementIntent intent) =>
                PluginNavigationCommandStatus.Unavailable;

            public PluginNavigationCommandStatus ClearMovementIntent() =>
                PluginNavigationCommandStatus.Unavailable;
        }
    }

    private sealed class StubHost(INavigationAutomation navigation) : IPluginHost
    {
        public INavigationAutomation Navigation { get; set; } = navigation;

        public bool HasUi => false;
        public IPluginLogger Log { get; } = new InertLogger();
        public IGameState State { get; } = new EmptyGameState();
        public IEvents Events { get; } = new WorldEvents();
        public ISelectionService Selection { get; } = new InertSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IPluginStorage Storage => NoOpPluginStorage.Instance;
        public IPluginStorage VtankProfiles => NoOpPluginStorage.Instance;
        public IPluginCommandRegistry Commands => NoOpPluginCommandRegistry.Instance;
        public IAutomationSurface Automation => new Surface(this);

        private sealed class Surface(StubHost host) : IAutomationSurface
        {
            public bool IsAvailable => false;
            public ICharacterInfo Character => NoOpAutomationSurface.Instance.Character;
            public ISpellCatalog Spells => NoOpAutomationSurface.Instance.Spells;
            public IMagicCommands Magic => NoOpAutomationSurface.Instance.Magic;
            public IPluginChat Chat => NoOpAutomationSurface.Instance.Chat;
            public INavigationAutomation Navigation => host.Navigation;
        }
    }

    private sealed class InertLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? error = null) { }
    }

    private sealed class EmptyGameState : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities { get; } = [];
    }

    private sealed class InertSelection : ISelectionService
    {
        public uint? SelectedObjectId => null;
        public uint? PreviousObjectId => null;
        public event Action<SelectionChangedEvent>? Changed;
        public bool Select(uint objectId)
        {
            Changed?.Invoke(default);
            return false;
        }
        public bool Clear() => false;
    }
}
