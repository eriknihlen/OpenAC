using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

// ScopedPluginHost.Ui forwards client-window control straight to the
// host's own IScopedUiRegistry (BufferedUiRegistry in production) without
// threading the plugin's owner through -- these are the client's own global
// retained windows, not one of this plugin's registered views. A forwarder
// that was never written would fall through to the interface's default
// (returns false for every window), which is indistinguishable from "the
// window doesn't exist" unless the fake below returns something else.
public sealed class ScopedUiRegistryClientWindowTests
{
    [Fact]
    public void ToggleClientWindowForwardsTheExactArgumentAndResult()
    {
        var inner = new FakeScopedUiRegistry();
        var host = new StubHost(inner);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        bool result = scoped.Ui.ToggleClientWindow(PluginClientWindow.Inventory);

        Assert.True(result);
        Assert.Equal(PluginClientWindow.Inventory, inner.LastToggled);

        scoped.Dispose();
    }

    [Fact]
    public void ShowClientWindowForwardsTheExactArgumentAndResult()
    {
        var inner = new FakeScopedUiRegistry();
        var host = new StubHost(inner);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        bool result = scoped.Ui.ShowClientWindow(PluginClientWindow.Character);

        Assert.True(result);
        Assert.Equal(PluginClientWindow.Character, inner.LastShown);

        scoped.Dispose();
    }

    [Fact]
    public void HideClientWindowForwardsTheExactArgumentAndResult()
    {
        var inner = new FakeScopedUiRegistry();
        var host = new StubHost(inner);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        bool result = scoped.Ui.HideClientWindow(PluginClientWindow.Spellbook);

        Assert.True(result);
        Assert.Equal(PluginClientWindow.Spellbook, inner.LastHidden);

        scoped.Dispose();
    }

    [Fact]
    public void IsClientWindowVisibleForwardsTheExactArgumentAndResult()
    {
        var inner = new FakeScopedUiRegistry { VisibleWindow = PluginClientWindow.Map };
        var host = new StubHost(inner);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        Assert.True(scoped.Ui.IsClientWindowVisible(PluginClientWindow.Map));
        Assert.False(scoped.Ui.IsClientWindowVisible(PluginClientWindow.Options));

        scoped.Dispose();
    }

    [Fact]
    public void UnavailableWindowReportsFalseThroughTheScope()
    {
        var inner = new FakeScopedUiRegistry { NextResult = false };
        var host = new StubHost(inner);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        Assert.False(scoped.Ui.ToggleClientWindow(PluginClientWindow.Journal));
        Assert.False(scoped.Ui.ShowClientWindow(PluginClientWindow.Journal));
        Assert.False(scoped.Ui.HideClientWindow(PluginClientWindow.Journal));

        scoped.Dispose();
    }

    private sealed class StubHost(IScopedUiRegistry ui) : IPluginHost
    {
        public bool HasUi => true;
        public IPluginLogger Log { get; } = new SilentLogger();
        public IGameState State { get; } = new EmptyGameState();
        public IEvents Events { get; } = new WorldEvents();
        public ISelectionService Selection { get; } = new InertSelection();
        public IUiRegistry Ui { get; } = ui;
        public IAutomationSurface Automation { get; } = NoOpAutomationSurface.Instance;

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
            public event Action<SelectionChangedEvent>? Changed { add { } remove { } }
            public bool Select(uint objectId) => false;
            public bool Clear() => false;
        }
    }

    private sealed class FakeScopedUiRegistry : IScopedUiRegistry
    {
        internal bool NextResult { get; set; } = true;
        internal PluginClientWindow? LastToggled { get; private set; }
        internal PluginClientWindow? LastShown { get; private set; }
        internal PluginClientWindow? LastHidden { get; private set; }
        internal PluginClientWindow? VisibleWindow { get; set; }

        public void AddMarkupPanel(string markupPath, object binding)
        {
        }

        public IDisposable RegisterMarkupPanel(string markupPath, object binding) =>
            NoOpUiRegistration.Instance;

        public bool ToggleClientWindow(PluginClientWindow window)
        {
            LastToggled = window;
            return NextResult;
        }

        public bool ShowClientWindow(PluginClientWindow window)
        {
            LastShown = window;
            return NextResult;
        }

        public bool HideClientWindow(PluginClientWindow window)
        {
            LastHidden = window;
            return NextResult;
        }

        public bool IsClientWindowVisible(PluginClientWindow window) =>
            VisibleWindow == window;
    }
}
