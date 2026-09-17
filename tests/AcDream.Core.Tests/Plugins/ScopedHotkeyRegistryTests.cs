using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

// ScopedPluginHost.Hotkeys must scope the registration id by the plugin's
// own id (so two plugins registering "attack" do not collide on the host's
// persisted-override key) and must revoke every hotkey the plugin
// registered when the scoped host itself is disposed.
public sealed class ScopedHotkeyRegistryTests
{
    [Fact]
    public void RegisterScopesTheIdByPluginId()
    {
        var recording = new RecordingHotkeyRegistry();
        var host = new StubHotkeyHost(recording);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        scoped.Hotkeys.Register("attack", "Attack", default, () => { });

        Assert.Equal("example.plugin:attack", Assert.Single(recording.RegisteredIds));

        scoped.Dispose();
    }

    [Fact]
    public void DisposingTheScopedHostRevokesEveryHotkeyItRegistered()
    {
        var recording = new RecordingHotkeyRegistry();
        var host = new StubHotkeyHost(recording);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        IPluginHotkeyRegistration first =
            scoped.Hotkeys.Register("one", "One", default, () => { });
        IPluginHotkeyRegistration second =
            scoped.Hotkeys.Register("two", "Two", default, () => { });

        Assert.Equal(2, recording.LiveCount);

        scoped.Dispose();

        Assert.Equal(0, recording.LiveCount);
    }

    [Fact]
    public void DisposingOneRegistrationDoesNotRevokeTheOthers()
    {
        var recording = new RecordingHotkeyRegistry();
        var host = new StubHotkeyHost(recording);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        IPluginHotkeyRegistration first =
            scoped.Hotkeys.Register("one", "One", default, () => { });
        scoped.Hotkeys.Register("two", "Two", default, () => { });

        first.Dispose();

        Assert.Equal(1, recording.LiveCount);

        scoped.Dispose();
    }

    private sealed class RecordingHotkeyRegistry : IHotkeyRegistry
    {
        internal List<string> RegisteredIds { get; } = [];
        internal int LiveCount { get; private set; }

        public IPluginHotkeyRegistration Register(
            string id,
            string displayName,
            PluginKeyChord defaultChord,
            Action handler)
        {
            RegisteredIds.Add(id);
            LiveCount++;
            return new Handle(this);
        }

        private sealed class Handle(RecordingHotkeyRegistry owner)
            : IPluginHotkeyRegistration
        {
            private bool _disposed;
            public bool IsBound => true;
            public PluginKeyChord EffectiveChord => default;
            public void Rebind(PluginKeyChord chord) { }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                owner.LiveCount--;
            }
        }
    }

    private sealed class StubHotkeyHost(IHotkeyRegistry hotkeys) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new SilentLogger();
        public IGameState State { get; } = new EmptyGameState();
        public IEvents Events { get; } = new AcDream.Core.Plugins.WorldEvents();
        public ISelectionService Selection { get; } = new InertSelection();
        public IUiRegistry Ui { get; } = NoOpUiRegistry.Instance;
        public IAutomationSurface Automation { get; } = NoOpAutomationSurface.Instance;
        public IPluginStorage Storage { get; } = NoOpPluginStorage.Instance;
        public IPluginClipboard Clipboard { get; } = NoOpPluginClipboard.Instance;
        public IHotkeyRegistry Hotkeys { get; } = hotkeys;

        private sealed class SilentLogger : IPluginLogger
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
}
