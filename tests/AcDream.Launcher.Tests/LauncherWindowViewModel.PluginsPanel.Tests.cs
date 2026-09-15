using System.Net;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;
using AcDream.Launcher.ViewModels;
using AcDream.Platform;

namespace AcDream.Launcher.Tests;

public sealed partial class LauncherWindowViewModelTests
{
    private static readonly Uri PluginListUri = new("https://example.test/plugins.json");

    [Fact]
    public async Task RateLimitedListSkipsPerPluginRequests()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.hello", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.hello", "shaneedwards/openac-plugin-hello", "0.1.0");
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : throw new InvalidOperationException(
                "A per-plugin request should not follow a rate-limited list: " + request.RequestUri));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        Assert.True(viewModel.Plugins.IsRateLimited);
        Assert.Equal("GitHub is rate limiting; try later.", viewModel.Plugins.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OfflineWithNoCacheShowsAnError()
    {
        using var fixture = new PluginPanelFixture();
        var handler = new RoutedHandler(_ =>
            throw new HttpRequestException("connection reset"));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        Assert.False(viewModel.Plugins.IsRateLimited);
        Assert.False(viewModel.Plugins.IsUsingCachedList);
        Assert.Equal("Could not reach the plugin list.", viewModel.Plugins.Error);
    }

    [Fact]
    public async Task CacheFallbackShowsTheListAge()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteCachedList(DateTimeOffset.UtcNow.AddHours(-3));
        var handler = new RoutedHandler(_ =>
            throw new HttpRequestException("connection reset"));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        Assert.True(viewModel.Plugins.IsUsingCachedList);
        Assert.Contains("3 hour(s)", viewModel.Plugins.ListAgeText, StringComparison.Ordinal);
        Assert.Null(viewModel.Plugins.Error);
    }

    [Fact]
    public async Task UpdateAndRemoveAreOfferedOnlyForLauncherManagedPlugins()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        fixture.WriteManifest("someone.manual", "1.0.0", ["headless"]);
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        Assert.True(managed.CanRemove);
        Assert.NotNull(managed.RemoveCommand);

        PluginInstalledRowViewModel manual = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "someone.manual");
        Assert.False(manual.CanRemove);
        Assert.Null(manual.RemoveCommand);
    }

    [Fact]
    public async Task UpdateWithheldReasonExplainsWhyNoUpdateIsOffered()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.managed", "0.1.0", "0.1.0", ["headless"]);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == GitHubReleaseLocator.LatestAsset(
                "shaneedwards/openac-plugin-hello", "plugin.json"))
            {
                return Ok(remoteManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        Assert.False(managed.UpdateAvailable);
        Assert.False(managed.HasUpdateWithheldReason);
        Assert.Null(managed.UpdateWithheldReason);
        Assert.False(managed.HasUpdateChip);
        Assert.Null(managed.UpdateChipText);
    }

    [Fact]
    public async Task NewerCompatibleReleaseShowsAnUpdateChipWithItsVersion()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.managed", "0.1.2", "0.1.0", ["headless"]);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == GitHubReleaseLocator.LatestAsset(
                "shaneedwards/openac-plugin-hello", "plugin.json"))
            {
                return Ok(remoteManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        Assert.True(managed.UpdateAvailable);
        Assert.True(managed.HasUpdateChip);
        Assert.Equal("Update available: v0.1.2", managed.UpdateChipText);
    }

    [Fact]
    public async Task UpdateWithheldReasonExplainsABlockedRelease()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.managed", "0.2.0", "0.1.0", ["headless"]);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(System.Text.Encoding.UTF8.GetBytes("""
                    {
                      "schemaVersion": 1,
                      "plugins": [
                        { "id": "edwards.discoverable", "name": "edwards.discoverable",
                          "author": "Shane Edwards", "description": "Test fixture.",
                          "repo": "shaneedwards/openac-plugin-hello" }
                      ],
                      "blocked": [
                        { "id": "edwards.managed", "versions": ["0.2.0"], "reason": "test" }
                      ]
                    }
                    """));
            }

            if (request.RequestUri == GitHubReleaseLocator.LatestAsset(
                "shaneedwards/openac-plugin-hello", "plugin.json"))
            {
                return Ok(remoteManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        Assert.False(managed.UpdateAvailable);
        Assert.True(managed.HasUpdateWithheldReason);
        Assert.Equal("the plugin is blocked", managed.UpdateWithheldReason);
    }

    [Fact]
    public async Task RefreshDiscoverDetailsAsyncFillsInLatestVersionAndFetchesItOnce()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless", "graphical"]);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == GitHubReleaseLocator.LatestAsset(
                "shaneedwards/openac-plugin-hello", "plugin.json"))
            {
                return Ok(remoteManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        Assert.False(row.HasLatestVersion);

        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.Equal("0.2.0", row.LatestVersion);
        Assert.Equal("Client not installed", row.Compatibility);
        Assert.False(row.CompatibilityIsWarning);
        int detailRequests = handler.Requests.Count(uri => uri == GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json"));
        Assert.Equal(1, detailRequests);

        // A second Check pass and a second refresh must not re-fetch what the session already has.
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.Equal("0.2.0", Assert.Single(viewModel.Plugins.Discover).LatestVersion);
        Assert.Equal(1, handler.Requests.Count(uri => uri == GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json")));
    }

    [Fact]
    public async Task EscapeClosesTheInstallDialogWithoutInstalling()
    {
        using var fixture = new PluginPanelFixture();
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : throw new InvalidOperationException(
                "Escape must not trigger a network call: " + request.RequestUri));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        row.InstallCommand.Execute(null);
        Assert.True(viewModel.Plugins.InstallDialog.IsOpen);

        viewModel.CloseActiveModal();

        Assert.False(viewModel.Plugins.InstallDialog.IsOpen);
        Assert.False(viewModel.IsModalOpen);
        Assert.Equal(1, handler.Requests.Count(uri => uri == PluginListUri));
    }

    [Fact]
    public async Task InstallIsEnabledInsideTheOpenInstallDialogWithNoTick()
    {
        using var fixture = new PluginPanelFixture();
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : throw new InvalidOperationException(
                "Opening the dialog must not trigger a network call: " + request.RequestUri));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        Assert.Single(viewModel.Plugins.Discover).InstallCommand.Execute(null);
        PluginInstallDialogViewModel dialog = viewModel.Plugins.InstallDialog;
        Assert.True(dialog.IsOpen);
        Assert.True(viewModel.IsModalOpen);

        Assert.True(dialog.ConfirmCommand.CanExecute(null));
        Assert.Equal(
            "Plugins are made by third parties, not OpenAC. Installing one is your choice and "
            + "your responsibility. Only install plugins from authors you trust.",
            dialog.WarningText);
    }

    [Fact]
    public async Task UnlistedInstallNoticeAddsANotOnTheListLine()
    {
        using var fixture = new PluginPanelFixture();
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : request.RequestUri == GitHubReleaseLocator.LatestAsset(
                "someone/unlisted-plugin", "plugin.json")
                ? Ok(PluginPanelFixture.ManifestJson("someone.unlisted", "0.1.0", "0.1.0", ["headless"]))
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        viewModel.Plugins.AddFromUrlText = "https://github.com/someone/unlisted-plugin";
        await viewModel.Plugins.AddFromUrlCommand.ExecuteAsync();

        PluginInstallDialogViewModel dialog = viewModel.Plugins.InstallDialog;
        Assert.True(dialog.IsOpen);
        Assert.True(dialog.ConfirmCommand.CanExecute(null));
        Assert.Equal(
            "Plugins are made by third parties, not OpenAC. Installing one is your choice and "
            + "your responsibility. Only install plugins from authors you trust.\n"
            + "This plugin is not on the OpenAC plugin list.",
            dialog.WarningText);
    }

    [Fact]
    public async Task UpdateDialogShowsNoEnableChoice()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.managed", "0.2.0", "0.1.0", ["headless"]);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == GitHubReleaseLocator.LatestAsset(
                "shaneedwards/openac-plugin-hello", "plugin.json"))
            {
                return Ok(remoteManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        managed.UpdateCommand!.Execute(null);

        PluginInstallDialogViewModel dialog = viewModel.Plugins.InstallDialog;
        Assert.True(dialog.IsOpen);
        Assert.True(dialog.IsUpdate);
        Assert.False(dialog.ShowEnableChoice);
    }

    [Fact]
    public async Task InstalledRowSourceBadgesDescribeWhereThePluginCameFrom()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord(
            "edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0", PluginInstallSource.Listed);
        fixture.WriteManifest("someone.unlisted", "0.1.0", ["headless"]);
        fixture.AddRecord(
            "someone.unlisted", "someone/unlisted-plugin", "0.1.0", PluginInstallSource.Unlisted);
        fixture.WriteManifest("someone.manual", "1.0.0", ["headless"]);
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        Assert.Equal(
            "Listed",
            Assert.Single(viewModel.Plugins.Installed, row => row.Id == "edwards.managed").SourceBadge);
        Assert.Equal(
            "Unlisted",
            Assert.Single(viewModel.Plugins.Installed, row => row.Id == "someone.unlisted").SourceBadge);
        Assert.Equal(
            "Manual",
            Assert.Single(viewModel.Plugins.Installed, row => row.Id == "someone.manual").SourceBadge);
    }

    [Fact]
    public async Task BlockedInstalledPluginShowsAPrefixedBadge()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(System.Text.Encoding.UTF8.GetBytes("""
                {
                  "schemaVersion": 1,
                  "plugins": [
                    { "id": "edwards.discoverable", "name": "edwards.discoverable",
                      "author": "Shane Edwards", "description": "Test fixture.",
                      "repo": "shaneedwards/openac-plugin-hello" }
                  ],
                  "blocked": [
                    { "id": "edwards.managed", "versions": ["0.1.0"], "reason": "test" }
                  ]
                }
                """))
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        Assert.Equal("test", managed.Blocked);
        Assert.True(managed.IsBlocked);
        Assert.Equal("Blocked: test", managed.BlockedText);
        Assert.False(managed.HasUpdateChip);
        Assert.Null(managed.UpdateChipText);
    }

    [Fact]
    public async Task DiscoverHidesABlockedListedPluginAndMakesNoDetailRequestForIt()
    {
        using var fixture = new PluginPanelFixture();
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson(blockDiscoverId: true))
            : throw new InvalidOperationException(
                "A blocked, not-installed plugin must never be fetched: " + request.RequestUri));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.Empty(viewModel.Plugins.Discover);
    }

    [Fact]
    public async Task DiscoverKeepsAPluginBlockedOnAnOlderVersionUntilItsLatestClears()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless", "graphical"]);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(System.Text.Encoding.UTF8.GetBytes("""
                    {
                      "schemaVersion": 1,
                      "plugins": [
                        { "id": "edwards.discoverable", "name": "edwards.discoverable",
                          "author": "Shane Edwards", "description": "Test fixture.",
                          "repo": "shaneedwards/openac-plugin-hello" }
                      ],
                      "blocked": [
                        { "id": "edwards.discoverable", "versions": ["0.1.0"], "reason": "test" }
                      ]
                    }
                    """));
            }

            if (request.RequestUri == GitHubReleaseLocator.LatestAsset(
                "shaneedwards/openac-plugin-hello", "plugin.json"))
            {
                return Ok(remoteManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);

        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.Single(viewModel.Plugins.Discover);
        Assert.Equal("0.2.0", row.LatestVersion);
    }

    [Fact]
    public async Task DiscoverHidesAPluginOnceItsLatestVersionIsFoundBlocked()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless", "graphical"]);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(System.Text.Encoding.UTF8.GetBytes("""
                    {
                      "schemaVersion": 1,
                      "plugins": [
                        { "id": "edwards.discoverable", "name": "edwards.discoverable",
                          "author": "Shane Edwards", "description": "Test fixture.",
                          "repo": "shaneedwards/openac-plugin-hello" }
                      ],
                      "blocked": [
                        { "id": "edwards.discoverable", "versions": ["0.2.0"], "reason": "test" }
                      ]
                    }
                    """));
            }

            if (request.RequestUri == GitHubReleaseLocator.LatestAsset(
                "shaneedwards/openac-plugin-hello", "plugin.json"))
            {
                return Ok(remoteManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        Assert.Single(viewModel.Plugins.Discover);

        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.Empty(viewModel.Plugins.Discover);
    }

    [Fact]
    public async Task EscapeClosesTheRemoveDialogWithoutRemoving()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        managed.RemoveCommand!.Execute(null);
        Assert.True(viewModel.Plugins.IsRemoveDialogOpen);

        viewModel.CloseActiveModal();

        Assert.False(viewModel.Plugins.IsRemoveDialogOpen);
        Assert.False(viewModel.IsModalOpen);
        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, "edwards.managed")));
    }

    [Fact]
    public async Task AddFromUrlForAnAlreadyInstalledRepoShowsAMessageWithNoExtraRequest()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.hello", "0.1.1", ["headless"]);
        fixture.AddRecord("edwards.hello", "shaneedwards/openac-plugin-hello", "0.1.1");
        byte[] currentManifest = PluginPanelFixture.ManifestJson(
            "edwards.hello", "0.1.1", "0.1.0", ["headless"]);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            // Check itself already fetches this manifest to evaluate updates; Add from URL must
            // resolve the repo match from the record store alone, not fetch a second time.
            if (request.RequestUri == manifestUri)
            {
                return Ok(currentManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        viewModel.Plugins.AddFromUrlText = "https://github.com/shaneedwards/openac-plugin-hello";
        await viewModel.Plugins.AddFromUrlCommand.ExecuteAsync();

        Assert.False(viewModel.Plugins.InstallDialog.IsOpen);
        Assert.Equal("edwards.hello is already installed.", viewModel.Plugins.StatusText);
        Assert.False(viewModel.Plugins.HasError);
        Assert.Equal(string.Empty, viewModel.Plugins.AddFromUrlText);
        Assert.Equal(1, handler.Requests.Count(uri => uri == PluginListUri));
        Assert.Equal(1, handler.Requests.Count(uri => uri == manifestUri));
    }

    [Fact]
    public async Task AddFromUrlMatchesAnAlreadyInstalledRepoCaseInsensitively()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.hello", "0.1.1", ["headless"]);
        fixture.AddRecord("edwards.hello", "shaneedwards/openac-plugin-hello", "0.1.1");
        byte[] currentManifest = PluginPanelFixture.ManifestJson(
            "edwards.hello", "0.1.1", "0.1.0", ["headless"]);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Ok(currentManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        viewModel.Plugins.AddFromUrlText = "https://github.com/ShaneEdwards/Openac-Plugin-Hello";
        await viewModel.Plugins.AddFromUrlCommand.ExecuteAsync();

        Assert.False(viewModel.Plugins.InstallDialog.IsOpen);
        Assert.Equal("edwards.hello is already installed.", viewModel.Plugins.StatusText);
        Assert.False(viewModel.Plugins.HasError);
        Assert.Equal(1, handler.Requests.Count(uri => uri == manifestUri));
    }

    [Fact]
    public async Task AddFromUrlRefusesAManifestIdAlreadyInstalledFromADifferentRepo()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.hello", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.hello", "shaneedwards/openac-plugin-hello", "0.1.0");
        byte[] otherManifest = PluginPanelFixture.ManifestJson(
            "edwards.hello", "0.1.0", "0.1.0", ["headless"]);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == GitHubReleaseLocator.LatestAsset(
                "someone/other-plugin", "plugin.json"))
            {
                return Ok(otherManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        viewModel.Plugins.AddFromUrlText = "https://github.com/someone/other-plugin";
        await viewModel.Plugins.AddFromUrlCommand.ExecuteAsync();

        Assert.False(viewModel.Plugins.InstallDialog.IsOpen);
        Assert.Equal(
            "'edwards.hello' is already installed from 'shaneedwards/openac-plugin-hello'.",
            viewModel.Plugins.Error);
        Assert.False(viewModel.Plugins.HasStatusText);
    }

    [Fact]
    public async Task AddFromUrlRefusesAPluginBlockedForAllVersions()
    {
        using var fixture = new PluginPanelFixture();
        byte[] manifest = PluginPanelFixture.ManifestJson(
            "edwards.hello", "0.1.0", "0.1.0", ["headless"]);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(System.Text.Encoding.UTF8.GetBytes("""
                    {
                      "schemaVersion": 1,
                      "plugins": [
                        { "id": "edwards.discoverable", "name": "edwards.discoverable",
                          "author": "Shane Edwards", "description": "Test fixture.",
                          "repo": "shaneedwards/openac-plugin-hello" }
                      ],
                      "blocked": [
                        { "id": "edwards.hello", "versions": ["*"], "reason": "test" }
                      ]
                    }
                    """));
            }

            if (request.RequestUri == manifestUri)
            {
                return Ok(manifest);
            }

            throw new InvalidOperationException(
                "A blocked plugin must never be downloaded: " + request.RequestUri);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        viewModel.Plugins.AddFromUrlText = "https://github.com/shaneedwards/openac-plugin-hello";
        await viewModel.Plugins.AddFromUrlCommand.ExecuteAsync();

        Assert.False(viewModel.Plugins.InstallDialog.IsOpen);
        Assert.Equal("'edwards.hello' is blocked: test", viewModel.Plugins.Error);
        Assert.False(viewModel.Plugins.HasStatusText);
    }

    [Fact]
    public async Task AddFromUrlRefusesAPluginBlockedForItsFetchedLatestVersion()
    {
        using var fixture = new PluginPanelFixture();
        byte[] manifest = PluginPanelFixture.ManifestJson(
            "edwards.hello", "0.2.0", "0.1.0", ["headless"]);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(System.Text.Encoding.UTF8.GetBytes("""
                    {
                      "schemaVersion": 1,
                      "plugins": [
                        { "id": "edwards.discoverable", "name": "edwards.discoverable",
                          "author": "Shane Edwards", "description": "Test fixture.",
                          "repo": "shaneedwards/openac-plugin-hello" }
                      ],
                      "blocked": [
                        { "id": "edwards.hello", "versions": ["0.2.0"], "reason": "test" }
                      ]
                    }
                    """));
            }

            if (request.RequestUri == manifestUri)
            {
                return Ok(manifest);
            }

            throw new InvalidOperationException(
                "A blocked plugin must never be downloaded: " + request.RequestUri);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        viewModel.Plugins.AddFromUrlText = "https://github.com/shaneedwards/openac-plugin-hello";
        await viewModel.Plugins.AddFromUrlCommand.ExecuteAsync();

        Assert.False(viewModel.Plugins.InstallDialog.IsOpen);
        Assert.Equal("'edwards.hello' is blocked: test", viewModel.Plugins.Error);
        Assert.False(viewModel.Plugins.HasStatusText);
    }

    [Fact]
    public async Task RemovingAPluginClearsAStaleStatusLine()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.hello", "0.1.1", ["headless"]);
        fixture.AddRecord("edwards.hello", "shaneedwards/openac-plugin-hello", "0.1.1");
        byte[] currentManifest = PluginPanelFixture.ManifestJson(
            "edwards.hello", "0.1.1", "0.1.0", ["headless"]);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Ok(currentManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        viewModel.Plugins.AddFromUrlText = "https://github.com/shaneedwards/openac-plugin-hello";
        await viewModel.Plugins.AddFromUrlCommand.ExecuteAsync();
        Assert.True(viewModel.Plugins.HasStatusText);

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.hello");
        managed.RemoveCommand!.Execute(null);
        viewModel.Plugins.ConfirmRemoveCommand.Execute(null);

        Assert.False(viewModel.Plugins.HasStatusText);
    }

    [Fact]
    public async Task CheckNowClearsAStaleStatusLine()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.hello", "0.1.1", ["headless"]);
        fixture.AddRecord("edwards.hello", "shaneedwards/openac-plugin-hello", "0.1.1");
        byte[] currentManifest = PluginPanelFixture.ManifestJson(
            "edwards.hello", "0.1.1", "0.1.0", ["headless"]);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Ok(currentManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        viewModel.Plugins.AddFromUrlText = "https://github.com/shaneedwards/openac-plugin-hello";
        await viewModel.Plugins.AddFromUrlCommand.ExecuteAsync();
        Assert.True(viewModel.Plugins.HasStatusText);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        Assert.False(viewModel.Plugins.HasStatusText);
    }

    [Fact]
    public async Task PluginCommandsReenableAfterAnyDialogCloses()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        PluginDiscoverRowViewModel discover = Assert.Single(viewModel.Plugins.Discover);
        viewModel.Plugins.AddFromUrlText = "https://github.com/someone/other-plugin";

        int checkNowNotifications = 0;
        int addFromUrlNotifications = 0;
        viewModel.Plugins.CheckNowCommand.CanExecuteChanged += (_, _) => checkNowNotifications++;
        viewModel.Plugins.AddFromUrlCommand.CanExecuteChanged += (_, _) => addFromUrlNotifications++;

        // Cancel the install dialog.
        discover.InstallCommand.Execute(null);
        viewModel.CloseActiveModal();
        Assert.True(checkNowNotifications > 0);
        Assert.True(addFromUrlNotifications > 0);
        Assert.True(managed.RemoveCommand!.CanExecute(null));
        Assert.True(discover.InstallCommand.CanExecute(null));
        Assert.True(viewModel.Plugins.CheckNowCommand.CanExecute(null));
        Assert.True(viewModel.Plugins.AddFromUrlCommand.CanExecute(null));

        // Cancel the remove dialog.
        checkNowNotifications = 0;
        managed.RemoveCommand!.Execute(null);
        viewModel.CloseActiveModal();
        Assert.True(checkNowNotifications > 0);
        Assert.True(managed.RemoveCommand.CanExecute(null));
        Assert.True(discover.InstallCommand.CanExecute(null));
        Assert.True(viewModel.Plugins.CheckNowCommand.CanExecute(null));
        Assert.True(viewModel.Plugins.AddFromUrlCommand.CanExecute(null));

        // A refused install leaves the dialog open with an error; Cancel from there still
        // re-enables every Plugins command.
        checkNowNotifications = 0;
        discover.InstallCommand.Execute(null);
        await viewModel.Plugins.InstallDialog.ConfirmCommand.ExecuteAsync();
        Assert.True(viewModel.Plugins.InstallDialog.IsOpen);
        Assert.True(viewModel.Plugins.InstallDialog.HasError);
        viewModel.Plugins.InstallDialog.CancelCommand.Execute(null);
        Assert.True(checkNowNotifications > 0);
        Assert.True(managed.RemoveCommand.CanExecute(null));
        Assert.True(discover.InstallCommand.CanExecute(null));
        Assert.True(viewModel.Plugins.CheckNowCommand.CanExecute(null));
        Assert.True(viewModel.Plugins.AddFromUrlCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReopeningCharacterOptionsAfterARemoveShowsTheAbsentRowNotMissing()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        var character = new LauncherCharacterSnapshot(
            "Local ACE", "testaccount", "+Holder", "0x50000001",
            LaunchMode.Headless, ["edwards.managed"], [], false, "Ready");
        using var orchestrator = new FakeLauncherOrchestrator
        {
            ServersOverride =
            [
                new LauncherServerSnapshot("Local ACE", "127.0.0.1", 9000,
                [
                    new LauncherAccountSnapshot("Local ACE", "testaccount", [character],
                        HasRunningActivity: false, ActivityStatus: "Ready"),
                ]),
            ],
        };
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        LauncherAccountServerRowViewModel row = viewModel.Accounts[0].Servers[0];
        row.SelectedCharacter = "+Holder";
        row.OptionsCommand.Execute(null);
        CharacterPluginChoiceViewModel ticked = Assert.Single(viewModel.CharacterPluginChoices);
        Assert.Equal("edwards.managed", ticked.Id);
        Assert.False(ticked.IsMissing);
        Assert.True(ticked.IsChecked);
        viewModel.CloseActiveModal();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, r => r.Id == "edwards.managed");
        managed.RemoveCommand!.Execute(null);
        viewModel.Plugins.ConfirmRemoveCommand.Execute(null);

        // The real orchestrator persists the strip and raises StateChanged synchronously
        // (MutateProfiles); the fake only records the call, so the test applies the same result
        // here before reopening.
        orchestrator.ServersOverride =
        [
            orchestrator.ServersOverride![0] with
            {
                Accounts = [orchestrator.ServersOverride[0].Accounts[0] with
                {
                    Characters = [character with { Plugins = [] }],
                }],
            },
        ];
        orchestrator.RaiseStateChanged();

        row.SelectedCharacter = "+Holder";
        row.OptionsCommand.Execute(null);

        Assert.Empty(viewModel.CharacterPluginChoices);
        viewModel.SaveCharacterSettingsCommand.Execute(null);
        var saved = orchestrator.SettingsUpdates.Last(update => update.Character == "+Holder");
        Assert.Empty(saved.Plugins);
    }

    [Fact]
    public void EnableForCharactersSkipsCharactersTheInstalledHostsDoNotSupport()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.headless-only", "0.1.0", ["headless"]);
        var handler = new RoutedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator
        {
            ServersOverride =
            [
                new LauncherServerSnapshot("Local ACE", "127.0.0.1", 9000,
                [
                    new LauncherAccountSnapshot("Local ACE", "testaccount",
                    [
                        new LauncherCharacterSnapshot(
                            "Local ACE", "testaccount", "+Graphical", "0x50000001",
                            LaunchMode.Gui, [], [], false, "Ready"),
                        new LauncherCharacterSnapshot(
                            "Local ACE", "testaccount", "+Headless", "0x50000002",
                            LaunchMode.Headless, [], [], false, "Ready"),
                    ],
                    HasRunningActivity: false,
                    ActivityStatus: "Ready"),
                ]),
            ],
        };
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        IReadOnlyList<PluginCharacterOption> all =
        [
            new("Local ACE", "testaccount", "+Graphical", "+Graphical (testaccount@Local ACE)"),
            new("Local ACE", "testaccount", "+Headless", "+Headless (testaccount@Local ACE)"),
        ];
        viewModel.Plugins.EnableForCharacters("edwards.headless-only", all);

        var update = Assert.Single(orchestrator.SettingsUpdates);
        Assert.Equal("+Headless", update.Character);
        Assert.Contains("edwards.headless-only", update.Plugins);
        Assert.Contains("+Graphical", viewModel.Plugins.Error);
        Assert.Contains("does not support that launch mode", viewModel.Plugins.Error);
    }

    [Fact]
    public async Task RemovingStripsTheIdFromEveryCharacterThatHadItAndLeavesOtherIds()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator
        {
            ServersOverride =
            [
                new LauncherServerSnapshot("Local ACE", "127.0.0.1", 9000,
                [
                    new LauncherAccountSnapshot("Local ACE", "testaccount",
                    [
                        new LauncherCharacterSnapshot(
                            "Local ACE", "testaccount", "+Holder", "0x50000001",
                            LaunchMode.Headless, ["edwards.managed", "other.plugin"], [], false, "Ready"),
                        new LauncherCharacterSnapshot(
                            "Local ACE", "testaccount", "+AlsoHolder", "0x50000002",
                            LaunchMode.Headless, ["edwards.managed"], [], false, "Ready"),
                        new LauncherCharacterSnapshot(
                            "Local ACE", "testaccount", "+Untouched", "0x50000003",
                            LaunchMode.Headless, ["other.plugin"], [], false, "Ready"),
                    ],
                    HasRunningActivity: false,
                    ActivityStatus: "Ready"),
                ]),
            ],
        };
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        managed.RemoveCommand!.Execute(null);
        viewModel.Plugins.ConfirmRemoveCommand.Execute(null);

        Assert.Equal(2, orchestrator.SettingsUpdates.Count);
        var holder = Assert.Single(orchestrator.SettingsUpdates, update => update.Character == "+Holder");
        Assert.Equal(["other.plugin"], holder.Plugins);
        var alsoHolder = Assert.Single(orchestrator.SettingsUpdates, update => update.Character == "+AlsoHolder");
        Assert.Empty(alsoHolder.Plugins);
        Assert.DoesNotContain(orchestrator.SettingsUpdates, update => update.Character == "+Untouched");
    }

    [Fact]
    public async Task ReinstallWithNoneAfterARemoveLeavesEveryListWithoutTheId()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator
        {
            ServersOverride =
            [
                new LauncherServerSnapshot("Local ACE", "127.0.0.1", 9000,
                [
                    new LauncherAccountSnapshot("Local ACE", "testaccount",
                    [
                        new LauncherCharacterSnapshot(
                            "Local ACE", "testaccount", "+Holder", "0x50000001",
                            LaunchMode.Headless, ["edwards.managed"], [], false, "Ready"),
                    ],
                    HasRunningActivity: false,
                    ActivityStatus: "Ready"),
                ]),
            ],
        };
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        managed.RemoveCommand!.Execute(null);
        viewModel.Plugins.ConfirmRemoveCommand.Execute(null);

        // The install dialog's "None" choice never calls EnableForCharacters (its own tests
        // confirm this), so a reinstall like the LP-11 report never re-adds the id itself; the
        // removed id staying off the list depends entirely on the remove having stripped it.
        var update = Assert.Single(orchestrator.SettingsUpdates, u => u.Character == "+Holder");
        Assert.DoesNotContain("edwards.managed", update.Plugins, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFailedRemoveStripsNothing()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator
        {
            ServersOverride =
            [
                new LauncherServerSnapshot("Local ACE", "127.0.0.1", 9000,
                [
                    new LauncherAccountSnapshot("Local ACE", "testaccount",
                    [
                        new LauncherCharacterSnapshot(
                            "Local ACE", "testaccount", "+Holder", "0x50000001",
                            LaunchMode.Headless, ["edwards.managed"], [], false, "Ready"),
                    ],
                    HasRunningActivity: false,
                    ActivityStatus: "Ready"),
                ]),
            ],
        };
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        managed.RemoveCommand!.Execute(null);

        var barrier = new UpdateSessionBarrier(fixture.Paths.DataDirectory);
        Assert.True(barrier.TryAcquireExclusive(out UpdateSessionBarrier.ExclusiveLease? lease));
        using (lease)
        {
            viewModel.Plugins.ConfirmRemoveCommand.Execute(null);
        }

        Assert.Equal(PluginInstaller.SessionLeaseRefusal, viewModel.Plugins.Error);
        Assert.Empty(orchestrator.SettingsUpdates);
        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, "edwards.managed")));
    }

    private sealed class PluginPanelFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "acdream-plugins-panel-tests",
            Guid.NewGuid().ToString("N"));

        public PluginPanelFixture()
        {
            Paths = new ApplicationPathSet(
                Path.Combine(_root, "config"),
                Path.Combine(_root, "data"),
                Path.Combine(_root, "cache"),
                null);
        }

        public ApplicationPathSet Paths { get; }

        public void WriteManifest(string id, string version, IReadOnlyList<string> hosts)
        {
            string directory = Path.Combine(Paths.PluginsDirectory, id);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(
                Path.Combine(directory, "plugin.json"),
                ManifestJson(id, version, "0.1.0", hosts));
        }

        public static byte[] ManifestJson(
            string id, string version, string minHostVersion, IReadOnlyList<string> hosts)
        {
            string hostsJson = string.Join(", ", hosts.Select(host => $"\"{host}\""));
            return System.Text.Encoding.UTF8.GetBytes($$"""
                {
                  "id": "{{id}}",
                  "displayName": "{{id}}",
                  "version": "{{version}}",
                  "entryDll": "{{id}}.dll",
                  "apiVersion": 1,
                  "minHostVersion": "{{minHostVersion}}",
                  "hosts": [{{hostsJson}}]
                }
                """);
        }

        public void AddRecord(
            string id, string repo, string version, PluginInstallSource source = PluginInstallSource.Listed)
        {
            InstalledPluginRecordStore store = InstalledPluginRecordStore.ForApplicationPaths(Paths);
            store.Load();
            store.Records.Add(new InstalledPluginRecord(
                id,
                repo,
                source,
                version,
                "v" + version,
                new string('a', 64),
                DateTimeOffset.UtcNow,
                null));
            store.Save();
        }

        public byte[] ListJson(string discoverId = "edwards.discoverable", bool blockDiscoverId = false) =>
            System.Text.Encoding.UTF8.GetBytes($$"""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "{{discoverId}}", "name": "{{discoverId}}", "author": "Shane Edwards",
                  "description": "Test fixture.", "repo": "shaneedwards/openac-plugin-hello" }
              ],
              "blocked": [
                {{(blockDiscoverId
                  ? $$"""{ "id": "{{discoverId}}", "versions": ["*"], "reason": "test" }"""
                  : "")}}
              ]
            }
            """);

        public void WriteCachedList(DateTimeOffset writeTime)
        {
            Directory.CreateDirectory(Paths.CacheDirectory);
            string path = Path.Combine(Paths.CacheDirectory, "plugins.json");
            File.WriteAllBytes(path, ListJson());
            File.SetLastWriteTimeUtc(path, writeTime.UtcDateTime);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body),
    };

    private sealed class RoutedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
