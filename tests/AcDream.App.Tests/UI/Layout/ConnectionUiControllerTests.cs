using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Net;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.UI.Layout;

public sealed class ConnectionUiControllerTests
{
    [Fact]
    public void FastConnectionShowsCompletedMetersUntilMinimumPresentationTime()
    {
        double now = 10d;
        var host = new UiRoot();
        ImportedLayout layout = FixtureLoader.LoadConnectionScreen();
        var view = new View { Snapshot = new(RuntimeConnectionStatus.Connecting, 0f, 0f) };
        using var controller = ConnectionUiController.Bind(host, layout,
            new ConnectionRuntimeBindings(() => view, () => { }), "checking", "done", () => now)!;
        controller.Tick();
        now = 10.1d;
        view.Snapshot = new(RuntimeConnectionStatus.Ready, 1f, 1f);
        controller.Tick();
        Assert.True(controller.Root.Visible);
        Assert.Equal(1f, Assert.IsType<UiMeter>(layout.FindElement(ConnectionUiController.UpdateMeterId)).Fill());
        now = 12d;
        controller.Tick();
        Assert.False(controller.Root.Visible);
        controller.Tick();
        Assert.False(controller.Root.Visible);
        Assert.Null(host.FixedCanvasSize);
        view.Snapshot = new(RuntimeConnectionStatus.Connecting, 0f, 0f);
        controller.Tick();
        Assert.True(controller.Root.Visible);
    }

    [Fact]
    public void SlowConnectionDoesNotAddAnotherDelayAfterReadiness()
    {
        double now = 0d;
        var host = new UiRoot();
        var view = new View { Snapshot = new(RuntimeConnectionStatus.Connecting, 0f, 0f) };
        using var controller = ConnectionUiController.Bind(host, FixtureLoader.LoadConnectionScreen(),
            new ConnectionRuntimeBindings(() => view, () => { }), "checking", "done", () => now)!;
        controller.Tick();
        now = 5d;
        view.Snapshot = new(RuntimeConnectionStatus.Ready, 1f, 1f);
        controller.Tick();
        Assert.False(controller.Root.Visible);
    }

    [Fact]
    public void DirectLaunchSkipsProgressButStillShowsUnsupportedUpdates()
    {
        var host = new UiRoot();
        var view = new View { Snapshot = new(RuntimeConnectionStatus.Connecting, 0f, 0f) };
        using var controller = ConnectionUiController.Bind(host, FixtureLoader.LoadConnectionScreen(),
            new ConnectionRuntimeBindings(() => view, () => { }, ShowProgress: false), "checking", "done")!;
        controller.Tick();
        Assert.False(controller.Root.Visible);
        view.Snapshot = new(RuntimeConnectionStatus.Ready, 1f, 1f);
        controller.Tick();
        Assert.False(controller.Root.Visible);
        Assert.Null(host.FixedCanvasSize);
        view.Snapshot = new(RuntimeConnectionStatus.Unsupported, 1f, 0f, "Update required.");
        controller.Tick();
        Assert.True(controller.Root.Visible);
        Assert.True(controller.ErrorText.Parent!.Visible);
    }

    [Fact]
    public void BackdropCoversTheCanvasBehindAllAuthoredArtwork()
    {
        var host = new UiRoot();
        ImportedLayout layout = FixtureLoader.LoadConnectionScreen();
        using var controller = Bind(host, layout, new View());
        var backdrop = Assert.IsType<UiPanel>(Assert.Single(controller.Root.Children,
            child => child.Name == "ConnectionBackdrop"));
        Assert.Equal(0f, backdrop.Left);
        Assert.Equal(0f, backdrop.Top);
        Assert.Equal(controller.Root.Width, backdrop.Width);
        Assert.Equal(controller.Root.Height, backdrop.Height);
        Assert.Equal(new Vector4(0f, 0f, 0f, 1f), backdrop.BackgroundColor);
        Assert.All(controller.Root.Children.Where(child => child != backdrop),
            child => Assert.True(child.ZOrder > backdrop.ZOrder));
    }

    [Fact]
    public void ConnectionAndCheckUseActualProgressAndAuthoredTextStates()
    {
        var host = new UiRoot();
        ImportedLayout layout = FixtureLoader.LoadConnectionScreen();
        var view = new View { Snapshot = new(RuntimeConnectionStatus.Connecting, 0f, 0f) };
        using var controller = Bind(host, layout, view);
        controller.Tick();
        Assert.True(controller.Root.Visible);
        Assert.Equal(new Vector2(800f, 600f), host.FixedCanvasSize);
        var connection = Assert.IsType<UiMeter>(layout.FindElement(ConnectionUiController.ConnectionMeterId));
        var update = Assert.IsType<UiMeter>(layout.FindElement(ConnectionUiController.UpdateMeterId));
        Assert.Equal(0f, connection.Fill());
        Assert.Equal(0f, update.Fill());
        Assert.Equal("Connecting...", Text(layout, ConnectionUiController.ConnectionTextId));

        view.Snapshot = new(RuntimeConnectionStatus.CheckingData, 1f, 0f);
        controller.Tick();
        Assert.Equal(1f, connection.Fill());
        Assert.Equal(0f, update.Fill());
        Assert.Equal("Connected!", Text(layout, ConnectionUiController.ConnectionTextId));
        Assert.Equal("Looking for data to patch...", Text(layout, ConnectionUiController.UpdateTextId));

        view.Snapshot = new(RuntimeConnectionStatus.Ready, 1f, 1f);
        controller.Tick();
        Assert.Equal(1f, update.Fill());
        Assert.False(controller.Root.Visible);
        Assert.Null(host.FixedCanvasSize);
    }

    [Fact]
    public void RequiredUpdatesRemainVisibleWithExplanationAndCancel()
    {
        var host = new UiRoot();
        ImportedLayout layout = FixtureLoader.LoadConnectionScreen();
        string explanation = new UnsupportedDataUpdateException().Message;
        var view = new View { Snapshot = new(RuntimeConnectionStatus.Unsupported, 1f, 0f, explanation) };
        int exits = 0;
        using var controller = Bind(host, layout, view, () => exits++);
        controller.Tick();
        Assert.True(controller.Root.Visible);
        Assert.True(controller.ErrorText.Parent!.Visible);
        var lines = controller.ErrorText.LinesProvider();
        Assert.True(lines.Count > 1);
        Assert.Equal(explanation, string.Join(" ", lines.Select(line => line.Text)));
        Assert.All(lines, line => Assert.True(line.Text.Length * 8f <= controller.ErrorText.Width));
        Assert.Equal(0f, Assert.IsType<UiMeter>(layout.FindElement(ConnectionUiController.UpdateMeterId)).Fill());
        Assert.IsType<UiButton>(layout.FindElement(ConnectionUiController.CancelId)).OnClick!();
        Assert.Equal(1, exits);
        controller.Tick();
        Assert.True(controller.Root.Visible);
    }

    [Fact]
    public void ReconnectClearsPriorErrorAndDoesNotKeepCompletedMeters()
    {
        var host = new UiRoot();
        ImportedLayout layout = FixtureLoader.LoadConnectionScreen();
        var view = new View { Snapshot = new(RuntimeConnectionStatus.Failed, 1f, 0f, "Connection failed.") };
        using var controller = Bind(host, layout, view);
        controller.Tick();
        Assert.True(controller.ErrorText.Parent!.Visible);
        view.Snapshot = new(RuntimeConnectionStatus.Connecting, 0f, 0f);
        controller.Tick();
        Assert.False(controller.ErrorText.Parent.Visible);
        Assert.Equal(string.Empty, Assert.Single(controller.ErrorText.LinesProvider()).Text);
        Assert.Equal(0f, Assert.IsType<UiMeter>(layout.FindElement(ConnectionUiController.ConnectionMeterId)).Fill());
        Assert.Equal("Update progress", Text(layout, ConnectionUiController.UpdateTextId));
    }

    [Fact]
    public void InactiveAndDisposalReleaseCanvasAndCancelCallback()
    {
        var host = new UiRoot();
        ImportedLayout layout = FixtureLoader.LoadConnectionScreen();
        var view = new View { Snapshot = new(RuntimeConnectionStatus.CheckingData, 1f, 0f) };
        var controller = Bind(host, layout, view);
        controller.Tick();
        view.Snapshot = default;
        controller.Tick();
        Assert.False(controller.Root.Visible);
        Assert.Null(host.FixedCanvasSize);
        view.Snapshot = new(RuntimeConnectionStatus.Connecting, 0f, 0f);
        controller.Tick();
        controller.Dispose();
        controller.Dispose();
        Assert.Null(host.FixedCanvasSize);
        Assert.DoesNotContain(controller.Root, host.Children);
        Assert.Null(Assert.IsType<UiButton>(layout.FindElement(ConnectionUiController.CancelId)).OnClick);
    }

    [Fact]
    public void MissingMeterRefusesPartialScreenWithoutMounting()
    {
        var host = new UiRoot();
        ImportedLayout layout = FixtureLoader.LoadConnectionScreen();
        var incomplete = new ImportedLayout(layout.Root, []);
        Assert.Null(ConnectionUiController.Bind(host, incomplete,
            new ConnectionRuntimeBindings(() => new View(), () => { }), "checking", "done"));
        Assert.Empty(host.Children);
    }

    private static ConnectionUiController Bind(UiRoot host, ImportedLayout layout, View view, Action? exit = null)
        => ConnectionUiController.Bind(host, layout,
            new ConnectionRuntimeBindings(() => view, exit ?? (() => { })),
            "Looking for data to patch...", "Patching Done!", minimumVisibleSeconds: 0d)!;

    private static string Text(ImportedLayout layout, uint id)
        => Assert.Single(Assert.IsType<UiText>(layout.FindElement(id)).LinesProvider()).Text;

    private sealed class View : IRuntimeConnectionView
    {
        public RuntimeConnectionSnapshot Snapshot { get; set; }
    }
}
