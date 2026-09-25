using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Navigation;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

public sealed class HeadlessSessionNavigationTests
{
    /// <summary>
    /// Plugins in a headless session get the runtime's navigation, the same implementation the
    /// graphical client hands out, rather than the default that refuses everything.
    /// </summary>
    [Fact]
    public void PluginsInAHeadlessSessionGetTheRuntimesNavigation()
    {
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            HeadlessSessionHostTests.Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new HeadlessSessionHostTests.FixtureSessionOperations());

        INavigationAutomation navigation = host.Plugins.Host.Automation.Navigation;

        Assert.IsType<RuntimeNavigationAutomation>(navigation);
        Assert.Equal(PluginNavigationCommandStatus.Unavailable, navigation.FaceHeading(90f));

        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);
        host.Tick(0.015d);

        Assert.NotEqual(PluginNavigationCommandStatus.Unavailable, navigation.FaceHeading(90f));
    }

    [Fact]
    public async Task PluginHostPathPreviewValidatesArgumentsBeforeReadingTheWorld()
    {
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            HeadlessSessionHostTests.Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new HeadlessSessionHostTests.FixtureSessionOperations());
        IPluginHost pluginHost = host.Plugins.Host;
        INavigationAutomation navigation = pluginHost.Automation.Navigation;

        Assert.Equal(PluginNavigationPlanStatus.InvalidTarget,
            (await navigation.PreviewPathAsync(0u)).Status);
        Assert.Equal(PluginNavigationPlanStatus.InvalidTarget,
            (await navigation.PreviewPathAsync(0x70000001u, 0f)).Status);
        Assert.Equal(PluginNavigationPlanStatus.InvalidTarget,
            (await navigation.PreviewPathAsync(0x70000001u, float.NaN)).Status);
        Assert.Equal(PluginNavigationPlanStatus.InvalidTarget,
            (await navigation.PreviewPathAsync(new PluginNavigationPosition(
                0xA9B40001u, double.NaN, 0d, 0d, 0f, true))).Status);
    }

    /// <summary>
    /// What /nav answers goes to the chat log, where it can be read back and copied, never to
    /// the transient notices shown over the world.
    /// </summary>
    [Fact]
    public void NavigationCommandsAnswerInTheChatLog()
    {
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            HeadlessSessionHostTests.Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new HeadlessSessionHostTests.FixtureSessionOperations());
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);
        host.Tick(0.015d);

        Assert.True(host.Plugins.Host.Automation.Chat.Submit("/nav status"));
        host.Tick(0.015d);

        Assert.Contains(
            host.Runtime.CommunicationOwner.Chat.Snapshot(),
            entry => entry.Text.Contains("Navigation: no walk has been asked for", StringComparison.Ordinal));
        Assert.DoesNotContain(
            host.Runtime.CommunicationOwner.SpewBox.Snapshot(),
            entry => entry.Text.Contains("Navigation:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The windowless client registers the navigation verbs on the one
    /// registry it hands plugins -- the plugin surface's own -- so a line
    /// typed at it finds them, and the two verbs that need something drawn
    /// say in plain words that there is nothing to draw on rather than being
    /// missing.
    /// </summary>
    [Fact]
    public void TheGridAndTheRouteAnswerInPlainWordsWithoutAWindow()
    {
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            HeadlessSessionHostTests.Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new HeadlessSessionHostTests.FixtureSessionOperations());
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);
        host.Tick(0.015d);

        Assert.True(host.Plugins.Host.Automation.Chat.Submit("/nav grid"));
        Assert.True(host.Plugins.Host.Automation.Chat.Submit("/nav route 0x80000001"));
        host.Tick(0.015d);

        string[] lines = host.Runtime.CommunicationOwner.Chat.Snapshot()
            .Select(static entry => entry.Text)
            .ToArray();
        Assert.Contains(
            lines,
            line => line.Contains(
                "Navigation: this client has nothing to draw the grid on",
                StringComparison.Ordinal));
        Assert.Contains(
            lines,
            line => line.Contains(
                "Navigation: this client has nothing to draw a route on",
                StringComparison.Ordinal));
    }

    /// <summary>A session that loaded no game data has nothing to plan walks over, so it says so.</summary>
    [Fact]
    public void WalksAreUnavailableInASessionWithoutGameData()
    {
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            HeadlessSessionHostTests.Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new HeadlessSessionHostTests.FixtureSessionOperations());
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);
        host.Tick(0.015d);

        Assert.Equal(
            PluginNavigationCommandStatus.Unavailable,
            host.Plugins.Host.Automation.Navigation.GoTo(0x70000001u, 2.5f));
    }
}
