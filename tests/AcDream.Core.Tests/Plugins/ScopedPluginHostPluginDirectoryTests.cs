using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

public sealed class ScopedPluginHostPluginDirectoryTests
{
    [Fact]
    public void ForwardsThePluginDirectoryToADirectoryAwareRegistry()
    {
        var registry = new RecordingDirectoryRegistry();
        using var scope = new ScopedPluginHost(
            new StubHost(registry),
            "acdream.icon",
            "Icon Plugin",
            "/plugins/icon");

        scope.Ui.AddPanel(
            new PluginPanelDescriptor("main", "Icon Plugin"),
            "main.xml",
            new object());

        Assert.Equal("/plugins/icon", registry.LastPluginDirectory);
    }

    [Fact]
    public void DoesNotForwardToARegistryThatIsOnlyIScopedUiRegistry()
    {
        var registry = new RecordingPlainRegistry();
        using var scope = new ScopedPluginHost(
            new StubHost(registry),
            "acdream.icon",
            "Icon Plugin",
            "/plugins/icon");

        scope.Ui.AddPanel(
            new PluginPanelDescriptor("main", "Icon Plugin"),
            "main.xml",
            new object());

        Assert.True(registry.Registered);
    }

    private sealed class StubHost(IUiRegistry ui) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new StubLogger();
        public IGameState State { get; } = new WorldGameState();
        public IEvents Events { get; } = new WorldEvents();
        public ISelectionService Selection { get; } = new SelectionState();
        public IUiRegistry Ui { get; } = ui;
        public IAutomationSurface Automation => NoOpAutomationSurface.Instance;
    }

    private sealed class StubLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class RecordingPlainRegistry : IScopedUiRegistry
    {
        internal bool Registered { get; private set; }

        public void AddMarkupPanel(string markupPath, object binding) { }

        public IDisposable RegisterMarkupPanel(string markupPath, object binding) =>
            NoOpUiRegistration.Instance;

        public IDisposable RegisterPanel(
            PluginUiOwner owner,
            PluginPanelDescriptor descriptor,
            string markupPath,
            object binding)
        {
            Registered = true;
            return NoOpUiRegistration.Instance;
        }
    }

    private sealed class RecordingDirectoryRegistry : IScopedUiRegistry, IPluginDirectoryUiRegistry
    {
        internal string? LastPluginDirectory { get; private set; }

        public void AddMarkupPanel(string markupPath, object binding) { }

        public IDisposable RegisterMarkupPanel(string markupPath, object binding) =>
            NoOpUiRegistration.Instance;

        public IDisposable RegisterPanel(
            PluginUiOwner owner,
            PluginPanelDescriptor descriptor,
            string markupPath,
            object binding)
        {
            // The directory-aware overload below must win whenever it exists.
            LastPluginDirectory = "should not be reached";
            return NoOpUiRegistration.Instance;
        }

        public IDisposable RegisterPanel(
            PluginUiOwner owner,
            string? pluginDirectory,
            PluginPanelDescriptor descriptor,
            string markupPath,
            object binding)
        {
            LastPluginDirectory = pluginDirectory;
            return NoOpUiRegistration.Instance;
        }

        public IDisposable RegisterPanelContent(
            PluginUiOwner owner,
            string? pluginDirectory,
            PluginPanelDescriptor descriptor,
            string markupContent,
            object binding)
        {
            LastPluginDirectory = pluginDirectory;
            return NoOpUiRegistration.Instance;
        }
    }
}
