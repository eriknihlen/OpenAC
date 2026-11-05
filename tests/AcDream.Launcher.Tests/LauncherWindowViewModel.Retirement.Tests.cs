using System.Net;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.ViewModels;

namespace AcDream.Launcher.Tests;

public sealed partial class LauncherWindowViewModelTests
{
    [Fact]
    public void TheWindowSaysThisClientIsRetiredAndLinksToTheRustClientPage()
    {
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);

        Assert.Equal("OpenAC (C#) is retired.", viewModel.RetirementNoticeTitle);
        Assert.Equal(
            "The Rust client, OpenAC for Rust, replaces it: download it from "
                + "https://eriknihlen.github.io/OpenAC/docs/rust-client.html.",
            viewModel.RetirementNoticeText);
        Assert.Equal("Get the Rust client", viewModel.GetRustClientLabel);
        Assert.Equal(
            new Uri("https://eriknihlen.github.io/OpenAC/docs/rust-client.html"),
            viewModel.RustClientPage);
    }

    [Fact]
    public async Task ARetiredLauncherNeverReadsTheReleaseFeedButStillChecksPlugins()
    {
        using var fixture = new PluginPanelFixture();
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        var feed = new StartupOrderUpdater { ClientUpdateAvailable = true };
        using var viewModel = new LauncherWindowViewModel(
            orchestrator,
            new ImmediateUiDispatcher(),
            new FakeLauncherInstaller(),
            new RetiredLauncherUpdater(feed));
        viewModel.ConfigurePlugins(composition, () => null);
        viewModel.Initialize();
        DateTimeOffset start = DateTimeOffset.UtcNow;

        // Startup, the twenty-minute timer and the button.
        await viewModel.StartBackgroundInitializationAsync();
        viewModel.PollUpdateCheck(start.AddMinutes(30));
        int listRequests = handler.Requests.Count(uri => uri == PluginListUri);
        await viewModel.CheckForUpdatesCommand.ExecuteAsync();

        Assert.Equal(0, feed.CheckCalls);
        Assert.Equal(1, feed.InitializeCalls);
        Assert.True(viewModel.UpdatePrompt.StartupCheckSucceeded);
        Assert.Equal(LauncherRetirement.NoUpdatesStatus, viewModel.UpdatePrompt.Status);
        Assert.Equal("This client is retired; it gets no more updates.", viewModel.UpdatePrompt.Status);
        Assert.False(viewModel.ShowUpdateBanner);
        Assert.False(viewModel.UpdatePrompt.IsOpen);
        Assert.Equal(listRequests + 1, handler.Requests.Count(uri => uri == PluginListUri));
    }
}
