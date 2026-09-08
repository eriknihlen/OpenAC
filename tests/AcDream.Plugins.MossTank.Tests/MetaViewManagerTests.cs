using AcDream.Plugin.Abstractions;
using AcDream.Plugins.MossTank.Expressions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class MetaViewManagerTests
{
    private const string Markup =
        "<panel w=\"120\" h=\"60\" title=\"Meta\" visible=\"{WindowAvailable}\" />";

    [Fact]
    public void CreateReplaceDestroyAndDestroyAllOwnTheRegistrationTokens()
    {
        var ui = new RecordingUiRegistry();
        var manager = new MetaViewManager(new StubHost(ui));

        Assert.True(manager.Create("Status", Markup));
        Registration first = Assert.Single(ui.Registrations);
        Assert.Equal("Status", first.Descriptor.Title);
        Assert.True(first.Descriptor.ShowInSidePanel);
        Assert.True(manager.Create("Status", Markup));
        Assert.True(first.Disposed);
        Assert.Equal(1, manager.Count);

        Assert.True(manager.Destroy("Status"));
        Assert.True(ui.Registrations[^1].Disposed);
        Assert.False(manager.Destroy("Status"));

        Assert.True(manager.Create("One", Markup));
        Assert.True(manager.Create("Two", Markup));
        manager.DestroyAll();
        Assert.Equal(0, manager.Count);
        Assert.All(ui.Registrations[^2..], static registration =>
            Assert.True(registration.Disposed));
    }

    [Fact]
    public void InvalidMarkupAndTheOfficialViewBoundaryArePreserved()
    {
        var manager = new MetaViewManager(new StubHost(new RecordingUiRegistry()));

        Assert.False(manager.Create("Bad", "not xml"));
        Assert.False(manager.Create("Bad", "<label />"));
        for (int index = 0; index < 6; index++)
            Assert.True(manager.Create($"View {index}", Markup));

        Assert.Equal(6, manager.Count);
        Assert.False(manager.Create("Seventh", Markup));
        Assert.False(manager.Create("View 0", Markup));
    }

    [Fact]
    public void StatusHudReusesOneShelfWindowAndOwnsItsLifetime()
    {
        var ui = new RecordingUiRegistry();
        var manager = new StatusHudManager(new StubHost(ui));

        Assert.True(manager.Update("State", "Hunting"));
        Registration registration = Assert.Single(ui.Registrations);
        Assert.Equal("VTank Meta Status", registration.Descriptor.Title);
        Assert.True(registration.Descriptor.ShowInSidePanel);
        Assert.Equal(["State: Hunting"], manager.Rows);
        Assert.Equal([0xE8DEC3u], manager.RowColors);

        Assert.True(manager.Update("State", "Resting", 0x00FF00u));
        Assert.True(manager.Update("Target", "Drudge", 0xFF9900u));
        Assert.Single(ui.Registrations);
        Assert.Equal(["State: Resting", "Target: Drudge"], manager.Rows);
        Assert.Equal([0x00FF00u, 0xFF9900u], manager.RowColors);

        manager.Destroy();
        Assert.True(registration.Disposed);
        Assert.Equal(0, manager.Count);
        Assert.Empty(manager.Rows);
        Assert.Empty(manager.RowColors);
    }

    [Fact]
    public void UtilityBeltUiExpressionsUseThePluginScopedViewRegistry()
    {
        var ui = new RecordingUiRegistry();
        using var expressions = new MossTankExpressionRuntime(new StubHost(ui));

        Assert.True(expressions.Evaluate("uiviewexists['Status']").IsTruthy);
        Assert.True(expressions.Evaluate("uiviewvisible['Status']").IsTruthy);
        Assert.True(expressions.Evaluate(
            "uisetlabel[uigetcontrol['Status','Action'],'Run']").IsTruthy);
        Assert.Equal("Run", ui.Label);
        Assert.True(expressions.Evaluate(
            "uisetvisible[uigetcontrol['Status','Action'],0]").IsTruthy);
        Assert.False(ui.ControlVisible);
    }

    private sealed class StubHost(IUiRegistry ui) : IPluginHost
    {
        public bool HasUi => true;
        public IPluginLogger Log { get; } = new StubLogger();
        public IGameState State { get; } = new StubState();
        public IEvents Events { get; } = new StubEvents();
        public ISelectionService Selection { get; } = new StubSelection();
        public IUiRegistry Ui { get; } = ui;
        public IAutomationSurface Automation => NoOpAutomationSurface.Instance;
    }

    private sealed class RecordingUiRegistry : IUiRegistry
    {
        public List<Registration> Registrations { get; } = [];
        public string Label { get; private set; } = string.Empty;
        public bool ControlVisible { get; private set; } = true;

        public void AddMarkupPanel(string markupPath, object binding)
        {
        }

        public IDisposable RegisterPanelContent(
            PluginPanelDescriptor descriptor,
            string markupContent,
            object binding)
        {
            var registration = new Registration(descriptor, markupContent);
            Registrations.Add(registration);
            return registration;
        }

        public bool ViewExists(string viewName) => viewName == "Status";
        public bool IsViewVisible(string viewName) => viewName == "Status";
        public bool ControlExists(string viewName, string controlName) =>
            viewName == "Status" && controlName == "Action";
        public bool SetControlLabel(
            string viewName,
            string controlName,
            string label)
        {
            if (!ControlExists(viewName, controlName))
                return false;
            Label = label;
            return true;
        }
        public bool SetControlVisible(
            string viewName,
            string controlName,
            bool visible)
        {
            if (!ControlExists(viewName, controlName))
                return false;
            ControlVisible = visible;
            return true;
        }
    }

    private sealed class Registration(
        PluginPanelDescriptor descriptor,
        string markup) : IDisposable
    {
        public PluginPanelDescriptor Descriptor { get; } = descriptor;
        public string Markup { get; } = markup;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class StubLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class StubState : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities => [];
    }

    private sealed class StubEvents : IEvents
    {
        public event Action<WorldEntitySnapshot> EntitySpawned
        {
            add { }
            remove { }
        }

        public event Action<double> Tick
        {
            add { }
            remove { }
        }
    }

    private sealed class StubSelection : ISelectionService
    {
        public uint? SelectedObjectId => null;
        public uint? PreviousObjectId => null;
        public event Action<SelectionChangedEvent> Changed
        {
            add { }
            remove { }
        }

        public bool Select(uint objectId) => false;
        public bool Clear() => false;
    }
}
