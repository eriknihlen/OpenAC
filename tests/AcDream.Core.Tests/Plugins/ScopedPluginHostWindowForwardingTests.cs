using System.IO;
using System.Reflection;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

// IPluginHost.Window has a default interface implementation that falls
// back to NoOpHostWindow, exactly like Clipboard/VtankProfiles/Log/State
// already do. That means a forwarder ScopedPluginHost never writes
// compiles clean and silently hands every plugin the no-op window instead
// of the host's real one -- the same trap ScopedAutomationSurfaceTests and
// ScopedEventsTests exist to catch for their own interfaces. This test
// does the same for IPluginHost's own direct-forward members: it walks the
// interface by reflection and requires every member not named in the
// explicitly-wrapped set to (a) be the exact same object the real host
// returns, and (b) not be reference-equal to the interface's own default
// value. Both checks matter: (a) alone would pass if a stub's own value
// happens to coincide with the interface default -- exactly what
// VtankProfiles/Clipboard would do if they were stubbed with the shared
// NoOp singletons, silently defeating the whole test -- so every stub
// property below is a distinct fake instance instead. A future member
// that legitimately needs its own wrapper (like Events, Selection, Ui,
// Automation, Storage, Commands, LootClassifiers, Hotkeys, and the
// per-plugin SessionSettings already do) is added to the wrapped set
// deliberately, in the same commit that adds the wrapper -- not silently
// skipped.
public sealed class ScopedPluginHostWindowForwardingTests
{
    private static readonly HashSet<string> WrappedMembers =
    [
        nameof(IPluginHost.HasUi), // bool value type; no host identity to preserve
        nameof(IPluginHost.Events),
        nameof(IPluginHost.Selection),
        nameof(IPluginHost.Ui),
        nameof(IPluginHost.Storage),
        nameof(IPluginHost.Commands),
        nameof(IPluginHost.LootClassifiers),
        nameof(IPluginHost.Hotkeys),
        nameof(IPluginHost.Automation),
        nameof(IPluginHost.SessionSettings),
    ];

    [Fact]
    public void EveryDirectForwardMemberIsTheHostsOwnObjectNotTheInterfaceDefault()
    {
        var host = new StubHost();
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");
        var minimal = new MinimalPluginHost();

        PropertyInfo[] properties = typeof(IPluginHost).GetProperties();
        Assert.True(
            properties.Length >= 14,
            "IPluginHost should still have every member this test knows about.");

        var checkedMembers = new List<string>();
        foreach (PropertyInfo property in properties)
        {
            if (WrappedMembers.Contains(property.Name))
                continue;

            object? innerValue = property.GetValue(host);
            object? scopedValue = property.GetValue(scoped);
            object? defaultValue = property.GetValue(minimal);

            Assert.True(
                ReferenceEquals(innerValue, scopedValue),
                "IPluginHost." + property.Name + " is not forwarded: the "
                    + "scoped host returned a different object than the "
                    + "real host's own property.");
            Assert.False(
                ReferenceEquals(defaultValue, scopedValue),
                "IPluginHost." + property.Name + " forwarding cannot be "
                    + "trusted: the stub's own value is reference-equal to "
                    + "the interface's own no-op default, so an unforwarded "
                    + "property reading straight from the default would "
                    + "have passed the check above too.");
            checkedMembers.Add(property.Name);
        }

        Assert.True(
            checkedMembers.Count == 6,
            "Expected exactly 6 direct-forward members (Log, State, "
                + "VtankProfiles, Clipboard, Window, Resources); found "
                + checkedMembers.Count + ": "
                + string.Join(", ", checkedMembers)
                + ". Update WrappedMembers deliberately if a member's "
                + "forwarding strategy changed.");
        Assert.Contains(nameof(IPluginHost.Window), checkedMembers);

        scoped.Dispose();
    }

    // Every required member returns a real (non-throwing) value so the
    // reflection loop can read it without special-casing; Events,
    // Selection, Ui, and Automation are wrapped members the loop skips
    // entirely, so they are left throwing on purpose -- reaching them
    // here would itself be a test bug.
    private sealed class MinimalPluginHost : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new InertLogger();
        public IGameState State { get; } = new InertGameState();
        public IEvents Events => throw new NotSupportedException();
        public ISelectionService Selection => throw new NotSupportedException();
        public IUiRegistry Ui => throw new NotSupportedException();
        public IAutomationSurface Automation => throw new NotSupportedException();

        private sealed class InertLogger : IPluginLogger
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception? error = null) { }
        }

        private sealed class InertGameState : IGameState
        {
            public IReadOnlyList<WorldEntitySnapshot> Entities { get; } = [];
        }
    }

    private sealed class StubHost : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new SilentLogger();
        public IGameState State { get; } = new EmptyGameState();
        public IEvents Events { get; } = new AcDream.Core.Plugins.WorldEvents();
        public ISelectionService Selection { get; } = new InertSelection();
        public IUiRegistry Ui { get; } = NoOpUiRegistry.Instance;
        public IAutomationSurface Automation { get; } = NoOpAutomationSurface.Instance;
        public IPluginStorage Storage { get; } = NoOpPluginStorage.Instance;

        // Distinct fakes, not the shared NoOp singletons: coinciding with
        // the interface default would defeat the check that scoped values
        // are not just accidentally equal to the default.
        public IPluginStorage VtankProfiles { get; } = new FakePluginStorage();
        public IPluginClipboard Clipboard { get; } = new FakeClipboard();
        public IHostWindow Window { get; } = new FakeHostWindow();
        public IPluginResourceCatalog Resources { get; } = new FakeResourceCatalog();

        private sealed class FakePluginStorage : IPluginStorage;
        private sealed class FakeClipboard : IPluginClipboard;
        private sealed class FakeHostWindow : IHostWindow;
        private sealed class FakeResourceCatalog : IPluginResourceCatalog
        {
            public Stream? OpenRead(string resourceId) => null;
            public IReadOnlyList<string> List(string prefix = "") => [];
            public IReadOnlyList<string> ListDataFiles(string relativeDirectory) => [];
        }

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
