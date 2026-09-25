using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

/// <summary>
/// A plugin shows and hides only its own windows: its scope forwards the
/// request with its own owner, and a host that draws nothing answers false.
///
/// Mutation check (2026-09-25): forwarding HidePanel as a show turned
/// <see cref="APluginsShowAndHideReachTheHostUnderItsOwnName"/> red.
/// </summary>
public sealed class ScopedUiRegistryPanelControlTests
{
    [Fact]
    public void APluginsShowAndHideReachTheHostUnderItsOwnName()
    {
        var ui = new RecordingUiRegistry();
        using var scope = new ScopedPluginHost(new StubHost(ui), "acdream.test", "Test");

        Assert.True(scope.Ui.ShowPanel("main"));
        Assert.False(scope.Ui.HidePanel("main"));

        Assert.Equal(
            [("show", "acdream.test", "main"), ("hide", "acdream.test", "main")],
            ui.Calls);
    }

    [Fact]
    public void AHostThatDrawsNothingAnswersFalse()
    {
        using var scope = new ScopedPluginHost(
            new StubHost(NoOpUiRegistry.Instance), "acdream.test", "Test");

        Assert.False(scope.Ui.ShowPanel("main"));
        Assert.False(scope.Ui.HidePanel("main"));
    }

    private sealed class RecordingUiRegistry : IScopedUiRegistry
    {
        public List<(string Verb, string Owner, string View)> Calls { get; } = [];

        public void AddMarkupPanel(string markupPath, object binding)
        {
        }

        public IDisposable RegisterMarkupPanel(string markupPath, object binding) =>
            NoOpUiRegistration.Instance;

        public bool ShowPanel(PluginUiOwner owner, string viewName)
        {
            Calls.Add(("show", owner.Id, viewName));
            return true;
        }

        public bool HidePanel(PluginUiOwner owner, string viewName)
        {
            Calls.Add(("hide", owner.Id, viewName));
            return false;
        }
    }

    private sealed class StubHost(IUiRegistry ui) : IPluginHost
    {
        public bool HasUi => true;
        public IPluginLogger Log { get; } = new SilentLogger();
        public IGameState State { get; } = new EmptyGameState();
        public IEvents Events { get; } = new WorldEvents();
        public ISelectionService Selection { get; } = new SelectionState();
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
    }
}
