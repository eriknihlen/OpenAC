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
    public async Task UpdateIsOfferedOnlyForLauncherManagedPluginsButRemoveIsOfferedForDirectToo()
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

        PluginInstalledRowViewModel direct = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "someone.manual");
        Assert.True(direct.CanRemove);
        Assert.NotNull(direct.RemoveCommand);
        Assert.False(direct.UpdateAvailable);
        Assert.Null(direct.UpdateCommand);
    }

    [Fact]
    public async Task UpdateWithheldReasonExplainsWhyNoUpdateIsOffered()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.managed", "0.1.0", "0.1.0", ["headless"]);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.1.0", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.1.2", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
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

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
    public async Task UpdateWithheldReasonExplainsATooNewCapabilityVocabulary()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.managed", "0.2.0", "0.1.0", ["headless"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current + 1);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
        Assert.Equal("needs a newer launcher", managed.UpdateWithheldReason);
    }

    [Fact]
    public async Task UpdateIsWithheldWhenTheManifestNamesACapabilityThisLauncherDoesNotKnow()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.managed", "0.2.0", "0.1.0", ["headless"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """[{ "name": "notARealCapability", "note": "Something new." }]""");
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
        Assert.Equal("the update's manifest is not valid", managed.UpdateWithheldReason);
    }

    [Fact]
    public async Task ABlockedReleaseStaysBlockedEvenWhenItsVocabularyIsAlsoTooNew()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.managed", "0.2.0", "0.1.0", ["headless"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current + 1);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
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

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
    public async Task DiscoverStillListsAPluginWhoseLatestReleaseNeedsANewerLauncher()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless", "graphical"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current + 1);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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

        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        Assert.Equal("0.2.0", row.LatestVersion);
    }

    [Fact]
    public async Task RefreshDiscoverDetailsAsyncFillsInLatestVersionAndRefetchesOnlyOnANewCheck()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless", "graphical"]);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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

        // Nothing has asked Discover to resolve anything yet, so the row stays hidden.
        Assert.Empty(viewModel.Plugins.Discover);

        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        Assert.Equal("0.2.0", row.LatestVersion);
        Assert.Equal("Client not installed", row.Compatibility);
        Assert.False(row.CompatibilityIsWarning);
        int detailRequests = handler.Requests.Count(uri => uri == manifestUri);
        Assert.Equal(1, detailRequests);

        // Re-opening the tab, with no new Check in between, must not re-fetch what it already has.
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();
        Assert.Equal(1, handler.Requests.Count(uri => uri == manifestUri));

        // A Check pass the user asks for once Discover has been looked at re-resolves everything,
        // so a release published since the last look is picked up.
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        Assert.Equal("0.2.0", Assert.Single(viewModel.Plugins.Discover).LatestVersion);
        Assert.Equal(2, handler.Requests.Count(uri => uri == manifestUri));
    }

    [Fact]
    public async Task EscapeClosesTheInstallDialogWithoutInstalling()
    {
        using var fixture = new PluginPanelFixture();
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        Uri iconUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", LauncherPluginIcon.FileName);
        byte[] discoverableManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless"]);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
            {
                return Ok(discoverableManifest);
            }

            if (request.RequestUri == iconUri)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            throw new InvalidOperationException(
                "Escape must not trigger any other network call: " + request.RequestUri);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        row.InstallCommand.Execute(null);
        Assert.True(viewModel.Plugins.InstallDialog.IsOpen);

        int requestsBeforeEscape = handler.Requests.Count;
        viewModel.CloseActiveModal();

        Assert.False(viewModel.Plugins.InstallDialog.IsOpen);
        Assert.False(viewModel.IsModalOpen);
        Assert.Equal(requestsBeforeEscape, handler.Requests.Count);
        Assert.Equal(1, handler.Requests.Count(uri => uri == PluginListUri));
    }

    [Fact]
    public async Task InstallIsEnabledInsideTheOpenInstallDialogWithNoTick()
    {
        using var fixture = new PluginPanelFixture();
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        Uri iconUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", LauncherPluginIcon.FileName);
        byte[] discoverableManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless"]);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
            {
                return Ok(discoverableManifest);
            }

            if (request.RequestUri == iconUri)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            throw new InvalidOperationException(
                "Opening the dialog must not trigger any other network call: " + request.RequestUri);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

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
        Uri manifestUri = GitHubReleaseLocator.LatestAsset("someone/unlisted-plugin", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "someone/unlisted-plugin", "v0.1.0", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
            {
                return Ok(PluginPanelFixture.ManifestJson("someone.unlisted", "0.1.0", "0.1.0", ["headless"]));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

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
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
    public async Task UpdateThatChangesCapabilitiesMarksTheChipAsNew()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest(
            "edwards.managed", "0.1.0", ["headless"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """[{ "name": "network", "note": "Sends usage counts." }]""");
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.managed", "0.2.0", "0.1.0", ["headless"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """
                [
                  { "name": "network", "note": "Sends usage counts." },
                  { "name": "chat", "note": "Reads chat." }
                ]
                """);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
        Assert.True(managed.UpdateCapabilitiesChanged);
        Assert.Equal("Update available: v0.2.0 · new capabilities", managed.UpdateChipText);
    }

    [Fact]
    public async Task UpdateWithUnchangedCapabilitiesShowsAPlainChipAndNoFreshConsent()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest(
            "edwards.managed", "0.1.0", ["headless"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """[{ "name": "network", "note": "Sends usage counts." }]""");
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.managed", "0.2.0", "0.1.0", ["headless"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """[{ "name": "network", "note": "Sends usage counts." }]""");
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
            {
                return Ok(remoteManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

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
        Assert.False(managed.UpdateCapabilitiesChanged);
        Assert.Equal("Update available: v0.2.0", managed.UpdateChipText);

        managed.UpdateCommand!.Execute(null);

        PluginInstallDialogViewModel dialog = viewModel.Plugins.InstallDialog;
        Assert.True(dialog.IsOpen);
        Assert.False(dialog.CapabilitiesChanged);
        Assert.False(dialog.ShowKeepEnabledChoice);
        Assert.Equal("Install", dialog.ConfirmLabel);
    }

    [Fact]
    public async Task UpdateDialogOffersToKeepEnabledForCharactersAlreadyHoldingTheOldConsent()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest(
            "edwards.managed", "0.1.0", ["headless"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """[{ "name": "network", "note": "Sends usage counts." }]""");
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.managed", "0.2.0", "0.1.0", ["headless"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """
                [
                  { "name": "network", "note": "Sends usage counts." },
                  { "name": "chat", "note": "Reads chat." }
                ]
                """);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
            {
                return Ok(remoteManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

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
                        new LauncherCharacterSnapshot(
                            "Local ACE", "testaccount", "+Untouched", "0x50000002",
                            LaunchMode.Headless, [], [], false, "Ready"),
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
        managed.UpdateCommand!.Execute(null);

        PluginInstallDialogViewModel dialog = viewModel.Plugins.InstallDialog;
        Assert.True(dialog.IsOpen);
        Assert.True(dialog.CapabilitiesChanged);
        Assert.True(dialog.ShowKeepEnabledChoice);
        Assert.Equal(["+Holder (testaccount@Local ACE)"], dialog.AffectedCharacters);
        Assert.Equal("Update and allow", dialog.ConfirmLabel);
        Assert.True(dialog.Capabilities.Single(chip => chip.Label.Contains("chat")).IsNew);
        Assert.False(dialog.Capabilities.Single(chip => chip.Label.Contains("network")).IsNew);
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
            "Direct install",
            Assert.Single(viewModel.Plugins.Installed, row => row.Id == "someone.manual").SourceBadge);
    }

    [Fact]
    public async Task ARefusedDirectInstallShowsAChipWithItsReasonAndCanStillBeRemoved()
    {
        using var fixture = new PluginPanelFixture();
        string directory = Path.Combine(fixture.Paths.PluginsDirectory, "broken-plugin");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(
            Path.Combine(directory, "plugin.json"),
            PluginPanelFixture.ManifestJson("edwards.broken", "0.1.0", "0.1.0", ["headless"]));
        // No entry DLL on disk: the manifest declares one, but DirectInstallCheck finds it missing.
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel row = Assert.Single(
            viewModel.Plugins.Installed, r => r.Id == "edwards.broken");
        Assert.True(row.IsRefused);
        Assert.False(string.IsNullOrWhiteSpace(row.Refusal));
        Assert.StartsWith("Refused: ", row.RefusedText);
        Assert.True(row.CanRemove);
        Assert.NotNull(row.RemoveCommand);
    }

    [Fact]
    public async Task ASecondCopyOfAnInstalledIdIsFlaggedOnBothCardsAndNotOffered()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        string strayDirectory = Path.Combine(fixture.Paths.PluginsDirectory, "aaa-stray");
        Directory.CreateDirectory(strayDirectory);
        File.WriteAllBytes(
            Path.Combine(strayDirectory, "plugin.json"),
            PluginPanelFixture.ManifestJson("edwards.managed", "0.1.0", "0.1.0", ["headless"]));
        File.WriteAllBytes(Path.Combine(strayDirectory, "edwards.managed.dll"), []);
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
            viewModel.Plugins.Installed, r => r.SourceBadge != "Direct install");
        Assert.True(managed.HasDuplicate);
        Assert.True(managed.HasChips);

        PluginInstalledRowViewModel stray = Assert.Single(
            viewModel.Plugins.Installed, r => r.SourceBadge == "Direct install");
        Assert.True(stray.IsRefused);
        Assert.Equal("Another copy of this plugin is installed.", stray.Refusal);
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
    public async Task SearchingDiscoverKeepsOnlyTheMatchingPluginsAndSaysSoWhenNoneMatch()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless", "graphical"]);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();
        int listed = viewModel.Plugins.Discover.Count;
        Assert.True(listed > 0, "The fixture list should offer at least one plugin.");

        viewModel.Plugins.DiscoverFilter = "discoverable";
        Assert.All(viewModel.Plugins.Discover, row =>
            Assert.Contains("discoverable", row.Id, StringComparison.OrdinalIgnoreCase));

        viewModel.Plugins.DiscoverFilter = "nothing-matches-this";
        Assert.Empty(viewModel.Plugins.Discover);
        Assert.False(viewModel.Plugins.HasDiscover);
        Assert.Equal("No listed plugin matches that search.", viewModel.Plugins.DiscoverEmptyText);

        viewModel.Plugins.DiscoverFilter = "";
        Assert.Equal(listed, viewModel.Plugins.Discover.Count);
        Assert.Equal("No listed plugins are available to install.", viewModel.Plugins.DiscoverEmptyText);
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
                return Redirect(GitHubReleaseLocator.TaggedAsset(
                    "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json"));
            }

            if (request.RequestUri == GitHubReleaseLocator.TaggedAsset(
                "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json"))
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

        // Not blocked at the list-check stage (the block only names an older version), so it stays
        // hidden until its release resolves, exactly like an unblocked listing would.
        Assert.Empty(viewModel.Plugins.Discover);

        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
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
                return Redirect(GitHubReleaseLocator.TaggedAsset(
                    "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json"));
            }

            if (request.RequestUri == GitHubReleaseLocator.TaggedAsset(
                "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json"))
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

        Assert.Empty(viewModel.Plugins.Discover);

        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.Empty(viewModel.Plugins.Discover);
    }

    [Fact]
    public async Task A404OnPluginJsonHidesTheDiscoverRow()
    {
        using var fixture = new PluginPanelFixture();
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.Empty(viewModel.Plugins.Discover);
        Assert.False(viewModel.Plugins.HasDiscoverStatusLine);
    }

    [Fact]
    public async Task ARepoWithNoReleaseAtAllStaysHiddenEvenWithBetaFallback()
    {
        using var fixture = new PluginPanelFixture();
        const string repo = "shaneedwards/openac-plugin-hello";
        Uri feedUri = GitHubReleaseLocator.ReleasesFeed(repo);
        byte[] emptyFeed = System.Text.Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom"></feed>
            """);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            // A bare tag with no release: the latest-asset redirect never resolves.
            if (request.RequestUri == feedUri)
            {
                return Ok(emptyFeed);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        viewModel.Plugins.ShowBetaPlugins = true;

        Assert.Empty(viewModel.Plugins.Discover);
    }

    [Fact]
    public async Task AnInvalidManifestHidesTheDiscoverRow()
    {
        using var fixture = new PluginPanelFixture();
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
            {
                return Ok(System.Text.Encoding.UTF8.GetBytes("{ this is not a plugin.json"));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

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
    public async Task ADiscoverSearchAndCountReflectOnlyRowsThatHaveResolved()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless", "graphical"]);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
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
                          "repo": "shaneedwards/openac-plugin-hello" },
                        { "id": "edwards.unreleased", "name": "edwards.unreleased",
                          "author": "Shane Edwards", "description": "No release yet.",
                          "repo": "shaneedwards/openac-plugin-unreleased" }
                      ],
                      "blocked": []
                    }
                    """));
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        // The unreleased listing never resolves, so it never counts, even though it was eligible.
        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        Assert.Equal("edwards.discoverable", row.Id);

        viewModel.Plugins.DiscoverFilter = "unreleased";
        Assert.Empty(viewModel.Plugins.Discover);
        Assert.Equal("No listed plugin matches that search.", viewModel.Plugins.DiscoverEmptyText);
    }

    [Fact]
    public async Task DiscoverShowsHowManyAreStillCheckingAndClearsItWhenTheFirstResolves()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless", "graphical"]);
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        var manifestGate = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new DeferredHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Task.FromResult(Ok(fixture.ListJson()));
            }

            if (request.RequestUri == manifestUri)
            {
                return manifestGate.Task;
            }

            if (request.RequestUri == taggedManifestUri)
            {
                return Task.FromResult(Ok(remoteManifest));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        Task refresh = viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.True(viewModel.Plugins.IsDiscoverChecking);
        Assert.Equal("Checking 1 plugin…", viewModel.Plugins.DiscoverCheckingText);
        Assert.False(viewModel.Plugins.ShowDiscoverEmptyText);
        Assert.Empty(viewModel.Plugins.Discover);

        manifestGate.SetResult(Redirect(taggedManifestUri));
        await refresh;

        Assert.False(viewModel.Plugins.IsDiscoverChecking);
        Assert.Single(viewModel.Plugins.Discover);
    }

    [Fact]
    public async Task APluginJsonThatAppearsAfterA404ShowsUpAfterACheckPass()
    {
        using var fixture = new PluginPanelFixture();
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless", "graphical"]);
        bool releasePublished = false;
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (!releasePublished)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.Empty(viewModel.Plugins.Discover);

        // The author publishes the release's plugin.json; a Check pass the user asks for picks
        // it up without needing a restart.
        releasePublished = true;
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        Assert.Equal("0.2.0", Assert.Single(viewModel.Plugins.Discover).LatestVersion);
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
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.1.1", "plugin.json");
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
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.1.1", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
                return Redirect(GitHubReleaseLocator.TaggedAsset(
                    "someone/other-plugin", "v0.1.0", "plugin.json"));
            }

            if (request.RequestUri == GitHubReleaseLocator.TaggedAsset(
                "someone/other-plugin", "v0.1.0", "plugin.json"))
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
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.1.0", "plugin.json");
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
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
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
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.1.1", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.1.1", "plugin.json");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
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
        // The release resolves (so the install dialog's own capability fetch succeeds), but no
        // zip is mocked, so the install itself still refuses once it gets that far, preserving
        // the "refused install" scenario below.
        Uri manifestUri = GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello", "v0.2.0", "plugin.json");
        byte[] discoverableManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless"]);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == manifestUri)
            {
                return Redirect(taggedManifestUri);
            }

            if (request.RequestUri == taggedManifestUri)
            {
                return Ok(discoverableManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

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

    [Fact]
    public async Task InstalledRowWithOneCapabilityShowsTheSingularCount()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest(
            "edwards.managed", "0.1.0", ["headless"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """[{ "name": "network", "note": "Sends usage counts." }]""");
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

        PluginInstalledRowViewModel row = Assert.Single(viewModel.Plugins.Installed);
        Assert.True(row.HasCapabilities);
        Assert.Equal("1 capability", row.CapabilityCountText);
    }

    [Fact]
    public async Task InstalledRowWithSeveralCapabilitiesShowsThePluralCount()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest(
            "edwards.managed", "0.1.0", ["headless"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """
                [
                  { "name": "network", "note": "Sends usage counts." },
                  { "name": "chat", "note": "Reads chat to detect buff requests." }
                ]
                """);
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

        PluginInstalledRowViewModel row = Assert.Single(viewModel.Plugins.Installed);
        Assert.Equal("2 capabilities", row.CapabilityCountText);
    }

    [Fact]
    public async Task InstalledRowDeclaringNoCapabilitiesShowsNoCount()
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

        PluginInstalledRowViewModel row = Assert.Single(viewModel.Plugins.Installed);
        Assert.False(row.HasCapabilities);
        Assert.Equal(string.Empty, row.CapabilityCountText);
    }

    [Fact]
    public async Task DiscoverRowShowsACapabilityCountOnceDetailsLoad()
    {
        using var fixture = new PluginPanelFixture();
        const string repo = "shaneedwards/openac-plugin-hello";
        Uri stableUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
        Uri stableTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0", "plugin.json");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless", "graphical"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """
                [
                  { "name": "network", "note": "Sends usage counts." },
                  { "name": "chat", "note": "Reads chat to detect buff requests." }
                ]
                """);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == stableUri)
            {
                return Redirect(stableTaggedUri);
            }

            if (request.RequestUri == stableTaggedUri)
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
        Assert.Empty(viewModel.Plugins.Discover);

        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        Assert.True(row.HasCapabilities);
        Assert.Equal("2 capabilities", row.CapabilityCountText);
    }

    [Fact]
    public async Task InstallDialogListsEveryDeclaredCapabilityInVocabularyOrderWithItsNote()
    {
        using var fixture = new PluginPanelFixture();
        const string repo = "shaneedwards/openac-plugin-hello";
        Uri stableUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
        Uri stableTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0", "plugin.json");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless", "graphical"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """
                [
                  { "name": "chat", "note": "Reads chat to detect buff requests." },
                  { "name": "network", "note": "Sends usage counts to my server." }
                ]
                """);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == stableUri)
            {
                return Redirect(stableTaggedUri);
            }

            if (request.RequestUri == stableTaggedUri)
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
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.Single(viewModel.Plugins.Discover).InstallCommand.Execute(null);

        PluginInstallDialogViewModel dialog = viewModel.Plugins.InstallDialog;
        Assert.True(dialog.HasCapabilities);
        Assert.Equal(2, dialog.Capabilities.Count);
        Assert.Equal("uses network", dialog.Capabilities[0].Label);
        Assert.Equal("Sends usage counts to my server.", dialog.Capabilities[0].Note);
        Assert.Equal("uses chat", dialog.Capabilities[1].Label);
        Assert.Equal("Reads chat to detect buff requests.", dialog.Capabilities[1].Note);
    }

    [Fact]
    public async Task InstallOnAVisibleDiscoverRowShowsEveryCapabilityItAlreadyResolved()
    {
        using var fixture = new PluginPanelFixture();
        const string repo = "shaneedwards/openac-plugin-hello";
        Uri stableUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
        Uri stableTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0", "plugin.json");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless", "graphical"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """
                [
                  { "name": "network", "note": "Sends usage counts to my server." },
                  { "name": "chat", "note": "Reads chat to detect buff requests." }
                ]
                """);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == stableUri)
            {
                return Redirect(stableTaggedUri);
            }

            if (request.RequestUri == stableTaggedUri)
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
        // A row only shows once its release resolves, so by the time Install can be
        // pressed at all, its capabilities are already loaded.
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        Assert.True(row.HasCapabilities);
        row.InstallCommand.Execute(null);

        PluginInstallDialogViewModel dialog = viewModel.Plugins.InstallDialog;
        Assert.False(dialog.IsLoadingCapabilities);
        Assert.True(dialog.HasCapabilities);
        Assert.Equal(2, dialog.Capabilities.Count);
        Assert.Equal("uses network", dialog.Capabilities[0].Label);
        Assert.Equal("uses chat", dialog.Capabilities[1].Label);
    }

    [Fact]
    public async Task InstallReusesAlreadyLoadedDiscoverDetailsWithoutFetchingAgain()
    {
        using var fixture = new PluginPanelFixture();
        const string repo = "shaneedwards/openac-plugin-hello";
        Uri stableUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
        Uri stableTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0", "plugin.json");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless", "graphical"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """[{ "name": "network", "note": "Sends usage counts." }]""");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == stableUri)
            {
                return Redirect(stableTaggedUri);
            }

            if (request.RequestUri == stableTaggedUri)
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
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.Equal(1, handler.Requests.Count(uri => uri == stableUri));

        Assert.Single(viewModel.Plugins.Discover).InstallCommand.Execute(null);

        PluginInstallDialogViewModel dialog = viewModel.Plugins.InstallDialog;
        Assert.False(dialog.IsLoadingCapabilities);
        Assert.True(dialog.HasCapabilities);
        Assert.Equal(1, handler.Requests.Count(uri => uri == stableUri));
    }

    [Fact]
    public async Task ARateLimitedDiscoverRowStaysHiddenAndThePanelSaysSo()
    {
        using var fixture = new PluginPanelFixture();
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : request.RequestUri == GitHubReleaseLocator.LatestAsset(
                "shaneedwards/openac-plugin-hello", "plugin.json")
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.Empty(viewModel.Plugins.Discover);
        Assert.True(viewModel.Plugins.HasDiscoverStatusLine);
        Assert.Equal(
            "GitHub is rate limiting; some plugins could not be checked. Try Refresh list in a minute.",
            viewModel.Plugins.DiscoverStatusLine);
    }

    [Fact]
    public async Task TogglingBetaWritesTheChannelAndReChecksOnlyThatPluginNotTheWholeList()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        const string repo = "shaneedwards/openac-plugin-hello";
        Uri stableUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
        Uri stableTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.1.0", "plugin.json");
        Uri feedUri = GitHubReleaseLocator.ReleasesFeed(repo);
        Uri betaTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0-beta.1", "plugin.json");
        byte[] feedBytes = System.Text.Encoding.UTF8.GetBytes($$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><link rel="alternate" href="https://github.com/{{repo}}/releases/tag/v0.2.0-beta.1"/></entry>
            </feed>
            """);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == stableUri)
            {
                return Redirect(stableTaggedUri);
            }

            if (request.RequestUri == stableTaggedUri)
            {
                return Ok(PluginPanelFixture.ManifestJson("edwards.managed", "0.1.0", "0.1.0", ["headless"]));
            }

            if (request.RequestUri == feedUri)
            {
                return Ok(feedBytes);
            }

            if (request.RequestUri == betaTaggedUri)
            {
                return Ok(PluginPanelFixture.ManifestJson(
                    "edwards.managed", "0.2.0-beta.1", "0.1.0", ["headless"]));
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
        Assert.True(managed.ShowBetaToggle);
        Assert.False(managed.IsBetaChannel);
        Assert.False(managed.UpdateAvailable);

        managed.IsBetaChannel = true;

        InstalledPluginRecordStore store = InstalledPluginRecordStore.ForApplicationPaths(fixture.Paths);
        store.Load();
        Assert.Equal(PluginReleaseChannel.Beta, store.Find("edwards.managed")!.Channel);

        PluginInstalledRowViewModel updated = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        Assert.True(updated.IsBetaChannel);
        Assert.True(updated.UpdateAvailable);
        Assert.Equal("0.2.0-beta.1", updated.UpdateVersion);

        // A single-plugin re-check, never the full list Check the toggle would otherwise pay for.
        Assert.Equal(1, handler.Requests.Count(uri => uri == PluginListUri));
    }

    [Fact]
    public async Task ARefusedChannelWriteRevertsTheToggleAndShowsTheLeaseRefusalLikeRemove()
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

        var barrier = new UpdateSessionBarrier(fixture.Paths.DataDirectory);
        Assert.True(barrier.TryAcquireExclusive(out UpdateSessionBarrier.ExclusiveLease? lease));
        using (lease)
        {
            managed.IsBetaChannel = true;
        }

        Assert.Equal(PluginInstaller.UpdateInProgressRefusal, viewModel.Plugins.Error);
        PluginInstalledRowViewModel stillManaged = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        Assert.False(stillManaged.IsBetaChannel);
    }

    [Fact]
    public async Task BetaToggleIsDisabledWhileTheModalIsOpen()
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
        Assert.True(managed.IsBetaToggleEnabled);

        managed.RemoveCommand!.Execute(null);
        Assert.True(viewModel.Plugins.IsRemoveDialogOpen);
        Assert.False(managed.IsBetaToggleEnabled);

        viewModel.CloseActiveModal();
        Assert.True(managed.IsBetaToggleEnabled);
    }

    [Fact]
    public async Task ABetaChipShowsOnlyForAPrereleaseInstalledVersion()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.prerelease", "0.2.0-beta.1", ["headless"]);
        fixture.AddRecord("edwards.prerelease", "shaneedwards/openac-plugin-hello", "0.2.0-beta.1");
        fixture.WriteManifest("edwards.stable", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.stable", "shaneedwards/openac-plugin-other", "0.1.0");
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel prerelease = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.prerelease");
        PluginInstalledRowViewModel stable = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.stable");
        Assert.True(prerelease.IsPrerelease);
        Assert.False(stable.IsPrerelease);
    }

    [Fact]
    public async Task TheBetaToggleIsHiddenForADirectInstallEvenOneOnAPrereleaseVersion()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("someone.manual", "1.0.0-beta.1", ["headless"]);
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel direct = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "someone.manual");
        Assert.False(direct.ShowBetaToggle);
        Assert.True(direct.IsPrerelease);
    }

    [Fact]
    public async Task WithShowBetaPluginsOffABetaOnlyRepoIsNotOfferedInDiscoverOrAddFromUrl()
    {
        using var fixture = new PluginPanelFixture();
        const string repo = "shaneedwards/openac-plugin-hello";
        Uri stableUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
        Uri feedUri = GitHubReleaseLocator.ReleasesFeed(repo);
        Uri betaTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0-beta.1", "plugin.json");
        byte[] feedBytes = System.Text.Encoding.UTF8.GetBytes($$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><link rel="alternate" href="https://github.com/{{repo}}/releases/tag/v0.2.0-beta.1"/></entry>
            </feed>
            """);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == stableUri)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.RequestUri == feedUri)
            {
                return Ok(feedBytes);
            }

            if (request.RequestUri == betaTaggedUri)
            {
                return Ok(PluginPanelFixture.ManifestJson(
                    "edwards.discoverable", "0.2.0-beta.1", "0.1.0", ["headless"]));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        Assert.False(viewModel.Plugins.ShowBetaPlugins);
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();
        // Stable is unavailable and beta is off, so the release never resolves and the row
        // stays hidden, not merely blank.
        Assert.Empty(viewModel.Plugins.Discover);

        viewModel.Plugins.AddFromUrlText = "https://github.com/" + repo;
        await viewModel.Plugins.AddFromUrlCommand.ExecuteAsync();
        Assert.False(viewModel.Plugins.InstallDialog.IsOpen);
        Assert.True(viewModel.Plugins.HasError);
    }

    [Fact]
    public async Task WithShowBetaPluginsOnABetaOnlyRepoIsOfferedInstallsWithTheBetaChannelAndChip()
    {
        using var fixture = new PluginPanelFixture();
        const string repo = "shaneedwards/openac-plugin-hello";
        const string id = "edwards.discoverable";
        Uri stableUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
        Uri feedUri = GitHubReleaseLocator.ReleasesFeed(repo);
        Uri betaTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0-beta.1", "plugin.json");
        byte[] manifestBytes = PluginPanelFixture.ManifestJson(id, "0.2.0-beta.1", "0.1.0", ["headless"]);
        byte[] zipBytes = PluginPanelFixture.BuildZip(id, manifestBytes);
        string zipName = $"{id}-0.2.0-beta.1.zip";
        Uri zipUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0-beta.1", zipName);
        Uri shaUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0-beta.1", zipName + ".sha256");
        byte[] feedBytes = System.Text.Encoding.UTF8.GetBytes($$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><link rel="alternate" href="https://github.com/{{repo}}/releases/tag/v0.2.0-beta.1"/></entry>
            </feed>
            """);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == stableUri)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.RequestUri == feedUri)
            {
                return Ok(feedBytes);
            }

            if (request.RequestUri == betaTaggedUri)
            {
                return Ok(manifestBytes);
            }

            if (request.RequestUri == shaUri)
            {
                return Ok(System.Text.Encoding.UTF8.GetBytes(
                    $"{PluginPanelFixture.Sha256(zipBytes)}  {zipName}\n"));
            }

            if (request.RequestUri == zipUri)
            {
                return Ok(zipBytes);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        viewModel.Plugins.ShowBetaPlugins = true;

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        // Stable is the default and this repo has never published one, so the beta is a choice.
        Assert.Null(row.LatestVersion);
        Assert.Equal("No stable release yet.", row.ChannelUnavailableText);
        row.SelectedChannelLabel = PluginDiscoverRowViewModel.BetaChannelLabel;
        Assert.Equal("0.2.0-beta.1", row.LatestVersion);

        row.InstallCommand.Execute(null);
        Assert.True(viewModel.Plugins.InstallDialog.IsOpen);
        Assert.True(viewModel.Plugins.InstallDialog.IsOfferedPrerelease);
        Assert.Contains("pre-release", viewModel.Plugins.InstallDialog.WarningText, StringComparison.Ordinal);

        await viewModel.Plugins.InstallDialog.ConfirmCommand.ExecuteAsync();
        Assert.False(viewModel.Plugins.InstallDialog.IsOpen);

        InstalledPluginRecordStore store = InstalledPluginRecordStore.ForApplicationPaths(fixture.Paths);
        store.Load();
        Assert.Equal(PluginReleaseChannel.Beta, store.Find(id)!.Channel);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        PluginInstalledRowViewModel installed = Assert.Single(
            viewModel.Plugins.Installed, r => r.Id == id);
        Assert.True(installed.IsPrerelease);
    }

    [Fact]
    public async Task TheChannelPickerShowsForEveryRowWhenShowBetaPluginsIsOnEvenWithNoBetaCandidate()
    {
        using var fixture = new PluginPanelFixture();
        const string repo = "shaneedwards/openac-plugin-hello";
        Uri stableUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
        Uri stableTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0", "plugin.json");
        Uri feedUri = GitHubReleaseLocator.ReleasesFeed(repo);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == stableUri)
            {
                return Redirect(stableTaggedUri);
            }

            if (request.RequestUri == stableTaggedUri)
            {
                return Ok(PluginPanelFixture.ManifestJson(
                    "edwards.discoverable", "0.2.0", "0.1.0", ["headless"]));
            }

            // No prerelease entries at all: the picker must still show while Show beta plugins is
            // on (opting in is a durable subscription, not conditioned on today's releases).
            if (request.RequestUri == feedUri)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        Assert.False(row.ShowChannelPicker);

        viewModel.Plugins.ShowBetaPlugins = true;

        row = Assert.Single(viewModel.Plugins.Discover);
        Assert.True(row.ShowChannelPicker);
    }

    [Fact]
    public async Task SwitchingTheDiscoverRowsChannelChangesTheDisplayedVersionWithoutANewRequest()
    {
        using var fixture = new PluginPanelFixture();
        const string repo = "shaneedwards/openac-plugin-hello";
        const string id = "edwards.discoverable";
        Uri stableUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
        Uri stableTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0", "plugin.json");
        Uri feedUri = GitHubReleaseLocator.ReleasesFeed(repo);
        Uri betaTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.3.0-beta.1", "plugin.json");
        byte[] feedBytes = System.Text.Encoding.UTF8.GetBytes($$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><link rel="alternate" href="https://github.com/{{repo}}/releases/tag/v0.3.0-beta.1"/></entry>
            </feed>
            """);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == stableUri)
            {
                return Redirect(stableTaggedUri);
            }

            if (request.RequestUri == stableTaggedUri)
            {
                return Ok(PluginPanelFixture.ManifestJson(id, "0.2.0", "0.1.0", ["headless"]));
            }

            if (request.RequestUri == feedUri)
            {
                return Ok(feedBytes);
            }

            if (request.RequestUri == betaTaggedUri)
            {
                return Ok(PluginPanelFixture.ManifestJson(id, "0.3.0-beta.1", "0.1.0", ["headless"]));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        viewModel.Plugins.ShowBetaPlugins = true;

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        Assert.Equal("0.2.0", row.LatestVersion);

        int requestsBeforeSwitch = handler.Requests.Count;
        row.SelectedChannelLabel = PluginDiscoverRowViewModel.BetaChannelLabel;

        Assert.Equal("0.3.0-beta.1", row.LatestVersion);
        Assert.Equal(requestsBeforeSwitch, handler.Requests.Count);

        row.SelectedChannelLabel = PluginDiscoverRowViewModel.StableChannelLabel;

        Assert.Equal("0.2.0", row.LatestVersion);
        Assert.Equal(requestsBeforeSwitch, handler.Requests.Count);
    }

    [Fact]
    public async Task InstallingWithStableSelectedPersistsTheStableChannelEvenWhenABetaCandidateExists()
    {
        using var fixture = new PluginPanelFixture();
        const string repo = "shaneedwards/openac-plugin-hello";
        const string id = "edwards.discoverable";
        Uri stableUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
        Uri stableTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0", "plugin.json");
        Uri feedUri = GitHubReleaseLocator.ReleasesFeed(repo);
        Uri betaTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.3.0-beta.1", "plugin.json");
        byte[] stableManifest = PluginPanelFixture.ManifestJson(id, "0.2.0", "0.1.0", ["headless"]);
        byte[] betaManifest = PluginPanelFixture.ManifestJson(id, "0.3.0-beta.1", "0.1.0", ["headless"]);
        byte[] stableZip = PluginPanelFixture.BuildZip(id, stableManifest);
        string stableZipName = $"{id}-0.2.0.zip";
        Uri stableZipUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0", stableZipName);
        Uri stableShaUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0", stableZipName + ".sha256");
        byte[] feedBytes = System.Text.Encoding.UTF8.GetBytes($$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><link rel="alternate" href="https://github.com/{{repo}}/releases/tag/v0.3.0-beta.1"/></entry>
            </feed>
            """);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == stableUri)
            {
                return Redirect(stableTaggedUri);
            }

            if (request.RequestUri == stableTaggedUri)
            {
                return Ok(stableManifest);
            }

            if (request.RequestUri == feedUri)
            {
                return Ok(feedBytes);
            }

            if (request.RequestUri == betaTaggedUri)
            {
                return Ok(betaManifest);
            }

            if (request.RequestUri == stableShaUri)
            {
                return Ok(System.Text.Encoding.UTF8.GetBytes(
                    $"{PluginPanelFixture.Sha256(stableZip)}  {stableZipName}\n"));
            }

            if (request.RequestUri == stableZipUri)
            {
                return Ok(stableZip);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        viewModel.Plugins.ShowBetaPlugins = true;

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        Assert.Equal(PluginDiscoverRowViewModel.StableChannelLabel, row.SelectedChannelLabel);
        Assert.Equal("0.2.0", row.LatestVersion);

        row.InstallCommand.Execute(null);
        Assert.True(viewModel.Plugins.InstallDialog.IsOpen);
        Assert.False(viewModel.Plugins.InstallDialog.IsOfferedPrerelease);

        await viewModel.Plugins.InstallDialog.ConfirmCommand.ExecuteAsync();
        Assert.False(viewModel.Plugins.InstallDialog.IsOpen);

        InstalledPluginRecordStore store = InstalledPluginRecordStore.ForApplicationPaths(fixture.Paths);
        store.Load();
        Assert.Equal(PluginReleaseChannel.Stable, store.Find(id)!.Channel);
        Assert.Equal("0.2.0", store.Find(id)!.Version);
    }

    [Fact]
    public async Task SelectingBetaOnAPluginWhoseNewestReleaseIsStablePersistsBetaAndShowsInTheInstalledCorner()
    {
        using var fixture = new PluginPanelFixture();
        const string repo = "shaneedwards/openac-plugin-hello";
        const string id = "edwards.discoverable";
        Uri stableUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
        Uri stableTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0", "plugin.json");
        Uri feedUri = GitHubReleaseLocator.ReleasesFeed(repo);
        byte[] manifestBytes = PluginPanelFixture.ManifestJson(id, "0.2.0", "0.1.0", ["headless"]);
        byte[] zipBytes = PluginPanelFixture.BuildZip(id, manifestBytes);
        string zipName = $"{id}-0.2.0.zip";
        Uri zipUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0", zipName);
        Uri shaUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0", zipName + ".sha256");
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == stableUri)
            {
                return Redirect(stableTaggedUri);
            }

            if (request.RequestUri == stableTaggedUri)
            {
                return Ok(manifestBytes);
            }

            // No prerelease on the feed: the newest release really is the stable one.
            if (request.RequestUri == feedUri)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.RequestUri == shaUri)
            {
                return Ok(System.Text.Encoding.UTF8.GetBytes(
                    $"{PluginPanelFixture.Sha256(zipBytes)}  {zipName}\n"));
            }

            if (request.RequestUri == zipUri)
            {
                return Ok(zipBytes);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        viewModel.Plugins.ShowBetaPlugins = true;

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        Assert.True(row.ShowChannelPicker);
        Assert.Equal(PluginDiscoverRowViewModel.StableChannelLabel, row.SelectedChannelLabel);
        row.SelectedChannelLabel = PluginDiscoverRowViewModel.BetaChannelLabel;

        row.InstallCommand.Execute(null);
        Assert.True(viewModel.Plugins.InstallDialog.IsOpen);

        await viewModel.Plugins.InstallDialog.ConfirmCommand.ExecuteAsync();
        Assert.False(viewModel.Plugins.InstallDialog.IsOpen);

        InstalledPluginRecordStore store = InstalledPluginRecordStore.ForApplicationPaths(fixture.Paths);
        store.Load();
        Assert.Equal(PluginReleaseChannel.Beta, store.Find(id)!.Channel);
        Assert.Equal("0.2.0", store.Find(id)!.Version);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        PluginInstalledRowViewModel installed = Assert.Single(viewModel.Plugins.Installed, r => r.Id == id);
        Assert.True(installed.ShowBetaChannelLabel);
    }

    [Fact]
    public async Task TheInstalledCardCornerShowsNothingForAStableChannelPluginEvenWithShowBetaPluginsOn()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord(
            "edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0",
            channel: PluginReleaseChannel.Stable);
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        viewModel.Plugins.ShowBetaPlugins = true;
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        Assert.False(managed.ShowBetaChannelLabel);
    }

    [Fact]
    public async Task TheInstalledCardCornerHidesTheBetaLabelWhenShowBetaPluginsIsOff()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.2.0-beta.1", ["headless"]);
        fixture.AddRecord(
            "edwards.managed", "shaneedwards/openac-plugin-hello", "0.2.0-beta.1",
            channel: PluginReleaseChannel.Beta);
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
        Assert.False(managed.ShowBetaChannelLabel);
    }

    /// <summary>Toggling the launcher-wide setting has to reach cards that are already built:
    /// installed rows are rebuilt only by a Check pass, so a corner label captured at construction
    /// would stay wrong for the rest of the session.</summary>
    [Fact]
    public async Task TheInstalledCardCornerFollowsShowBetaPluginsWithoutARecheck()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.2.0-beta.1", ["headless"]);
        fixture.AddRecord(
            "edwards.managed", "shaneedwards/openac-plugin-hello", "0.2.0-beta.1",
            channel: PluginReleaseChannel.Beta);
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        viewModel.Plugins.ShowBetaPlugins = false;
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        Assert.False(managed.ShowBetaChannelLabel);

        viewModel.Plugins.ShowBetaPlugins = true;
        Assert.True(managed.ShowBetaChannelLabel);

        viewModel.Plugins.ShowBetaPlugins = false;
        Assert.False(managed.ShowBetaChannelLabel);
    }

    /// <summary>Found in live testing: BuffBot has only ever published prereleases, and picking Stable
    /// installed the beta anyway. The selected channel must refuse rather than substitute the other
    /// one — what the picker says and what Install does have to be the same release.</summary>
    [Fact]
    public async Task PickingStableOnAPluginWithNoStableReleaseRefusesInsteadOfInstallingTheBeta()
    {
        using var fixture = new PluginPanelFixture();
        const string repo = "shaneedwards/openac-plugin-hello";
        const string id = "edwards.discoverable";
        Uri stableUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
        Uri feedUri = GitHubReleaseLocator.ReleasesFeed(repo);
        Uri betaTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.1.0-beta.2", "plugin.json");
        byte[] betaManifest = PluginPanelFixture.ManifestJson(id, "0.1.0-beta.2", "0.1.0", ["headless"]);
        byte[] feedBytes = System.Text.Encoding.UTF8.GetBytes($$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><link rel="alternate" href="https://github.com/{{repo}}/releases/tag/v0.1.0-beta.2"/></entry>
            </feed>
            """);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == feedUri)
            {
                return Ok(feedBytes);
            }

            if (request.RequestUri == betaTaggedUri)
            {
                return Ok(betaManifest);
            }

            // No stable release has ever been published: latest itself 404s.
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        viewModel.Plugins.ShowBetaPlugins = true;
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);

        // Default is Stable, and this plugin has none: nothing offered, Install refused.
        Assert.Equal(PluginDiscoverRowViewModel.StableChannelLabel, row.SelectedChannelLabel);
        Assert.Null(row.LatestVersion);
        Assert.Equal("No stable release yet.", row.ChannelUnavailableText);
        Assert.False(row.InstallCommand.CanExecute(null));

        row.InstallCommand.Execute(null);
        Assert.False(viewModel.Plugins.InstallDialog.IsOpen);

        // Choosing Beta is what makes it installable, and it pins the prerelease.
        row.SelectedChannelLabel = PluginDiscoverRowViewModel.BetaChannelLabel;
        Assert.Equal("0.1.0-beta.2", row.LatestVersion);
        Assert.Null(row.ChannelUnavailableText);
        Assert.True(row.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task ShowBetaPluginsPersistsAcrossAViewModelReload()
    {
        using var fixture = new PluginPanelFixture();
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        viewModel.Plugins.ShowBetaPlugins = true;

        // A fresh view model over the same orchestrator (the way a window reopen would), reading
        // whatever the setting last wrote rather than defaulting off again.
        using var reloadedViewModel = CreateInitialized(orchestrator);
        reloadedViewModel.ConfigurePlugins(composition, () => null);

        Assert.True(reloadedViewModel.Plugins.ShowBetaPlugins);
    }

    /// <summary>Found in live testing: the setting was saved but came back off on every launch. App wires
    /// the plugin panel (ConfigurePlugins) before Initialize loads the profiles, so the panel read
    /// the empty store's default. The test above builds its view model already initialized, the
    /// opposite order, which is why it never saw this; this one runs the order App actually uses.</summary>
    [Fact]
    public async Task ShowBetaPluginsIsRestoredWhenProfilesLoadAfterThePanelIsConfigured()
    {
        using var fixture = new PluginPanelFixture();
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator { ShowBetaPluginsOnDisk = true };
        using var viewModel = new LauncherWindowViewModel(orchestrator, new ImmediateUiDispatcher());

        // App.axaml.cs order: the panel is configured first, the profiles load second.
        viewModel.ConfigurePlugins(composition, () => null);
        Assert.False(viewModel.Plugins.ShowBetaPlugins);
        viewModel.Initialize();

        Assert.True(viewModel.Plugins.ShowBetaPlugins);
        // Restoring a saved value must not write it straight back as if the player toggled it.
        Assert.True(orchestrator.ShowBetaPlugins);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TogglingShowBetaPluginsNeverChangesInstalledRecords()
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

        viewModel.Plugins.ShowBetaPlugins = true;
        viewModel.Plugins.ShowBetaPlugins = false;

        InstalledPluginRecordStore store = InstalledPluginRecordStore.ForApplicationPaths(fixture.Paths);
        store.Load();
        Assert.Equal(PluginReleaseChannel.Stable, store.Find("edwards.managed")!.Channel);
    }

    [Fact]
    public async Task UpdateDialogShowsTheBetaReleasesOwnCapabilitiesNotTheStableOnes()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest(
            "edwards.managed", "0.1.0", ["headless"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """[{ "name": "network", "note": "Sends usage counts." }]""");
        fixture.AddRecord(
            "edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0",
            channel: PluginReleaseChannel.Beta);
        const string repo = "shaneedwards/openac-plugin-hello";
        Uri stableUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
        Uri stableTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.1.0", "plugin.json");
        Uri feedUri = GitHubReleaseLocator.ReleasesFeed(repo);
        Uri betaTaggedUri = GitHubReleaseLocator.TaggedAsset(repo, "v0.2.0-beta.1", "plugin.json");
        byte[] feedBytes = System.Text.Encoding.UTF8.GetBytes($$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><link rel="alternate" href="https://github.com/{{repo}}/releases/tag/v0.2.0-beta.1"/></entry>
            </feed>
            """);
        byte[] betaManifest = PluginPanelFixture.ManifestJson(
            "edwards.managed", "0.2.0-beta.1", "0.1.0", ["headless"],
            capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
            capabilitiesJson: """
                [
                  { "name": "network", "note": "Sends usage counts." },
                  { "name": "chat", "note": "Reads chat to detect buff requests." }
                ]
                """);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == stableUri)
            {
                return Redirect(stableTaggedUri);
            }

            if (request.RequestUri == stableTaggedUri)
            {
                return Ok(PluginPanelFixture.ManifestJson(
                    "edwards.managed", "0.1.0", "0.1.0", ["headless"],
                    capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
                    capabilitiesJson: """[{ "name": "network", "note": "Sends usage counts." }]"""));
            }

            if (request.RequestUri == feedUri)
            {
                return Ok(feedBytes);
            }

            if (request.RequestUri == betaTaggedUri)
            {
                return Ok(betaManifest);
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
        Assert.Equal("0.2.0-beta.1", managed.UpdateVersion);
        // The beta release adds "chat" beyond what the installed stable version declares, so the
        // update chip and dialog must both reflect the beta manifest's own list, not the stable
        // release's, even though the installed capabilities on disk are still the stable ones.
        Assert.True(managed.UpdateCapabilitiesChanged);

        managed.UpdateCommand!.Execute(null);

        PluginInstallDialogViewModel dialog = viewModel.Plugins.InstallDialog;
        Assert.True(dialog.IsOpen);
        Assert.True(dialog.CapabilitiesChanged);
        Assert.Equal(2, dialog.Capabilities.Count);
        // "chat" is new since the installed (stable) version, so it carries the "(new)" mark;
        // "network" was already declared, so it reads plainly.
        Assert.Contains(dialog.Capabilities, chip => chip.Label == "uses chat (new)" && chip.IsNew);
        Assert.Contains(dialog.Capabilities, chip => chip.Label == "uses network" && !chip.IsNew);
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
                Path.Combine(_root, "cache"));
        }

        public ApplicationPathSet Paths { get; }

        public void WriteManifest(
            string id,
            string version,
            IReadOnlyList<string> hosts,
            int? capabilitiesVersion = null,
            string? capabilitiesJson = null)
        {
            string directory = Path.Combine(Paths.PluginsDirectory, id);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(
                Path.Combine(directory, "plugin.json"),
                ManifestJson(id, version, "0.1.0", hosts, capabilitiesVersion, capabilitiesJson));
            File.WriteAllBytes(Path.Combine(directory, $"{id}.dll"), []);
        }

        public static byte[] ManifestJson(
            string id,
            string version,
            string minHostVersion,
            IReadOnlyList<string> hosts,
            int? capabilitiesVersion = null,
            string? capabilitiesJson = null)
        {
            string hostsJson = string.Join(", ", hosts.Select(host => $"\"{host}\""));
            string capabilitiesField = capabilitiesVersion is { } v
                ? $",\n  \"capabilitiesVersion\": {v},\n  \"capabilities\": {capabilitiesJson ?? "[]"}"
                : "";
            return System.Text.Encoding.UTF8.GetBytes($$"""
                {
                  "id": "{{id}}",
                  "displayName": "{{id}}",
                  "version": "{{version}}",
                  "entryDll": "{{id}}.dll",
                  "apiVersion": 1,
                  "minHostVersion": "{{minHostVersion}}",
                  "hosts": [{{hostsJson}}]{{capabilitiesField}}
                }
                """);
        }

        /// <summary>A zip an install can actually extract: the given <paramref name="manifestBytes"/>
        /// at <c>plugin.json</c> and a placeholder entry DLL matching its declared name.</summary>
        public static byte[] BuildZip(string id, byte[] manifestBytes)
        {
            using var output = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(
                output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                using (Stream entryStream = archive.CreateEntry("plugin.json").Open())
                {
                    entryStream.Write(manifestBytes, 0, manifestBytes.Length);
                }

                byte[] dllBytes = System.Text.Encoding.UTF8.GetBytes("binary-" + id);
                using (Stream entryStream = archive.CreateEntry($"{id}.dll").Open())
                {
                    entryStream.Write(dllBytes, 0, dllBytes.Length);
                }
            }

            return output.ToArray();
        }

        public static string Sha256(byte[] bytes) =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

        public void AddRecord(
            string id,
            string repo,
            string version,
            PluginInstallSource source = PluginInstallSource.Listed,
            PluginReleaseChannel channel = PluginReleaseChannel.Stable)
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
                null)
            {
                Channel = channel,
            });
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

    private static HttpResponseMessage Redirect(Uri location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = location;
        return response;
    }

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

    /// <summary>Like <see cref="RoutedHandler"/>, but the response is a <see cref="Task"/> the
    /// caller drives itself: the one way to pause <see cref="LauncherPluginsViewModel.RefreshDiscoverDetailsAsync"/>
    /// mid-resolution so a test can observe the "checking" state before completing the request.</summary>
    private sealed class DeferredHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => respond(request);
    }

    [Fact]
    public void AWidePluginsPanelPutsDiscoverBesideInstalledAndANarrowOneStacksThem()
    {
        using var core = new FakeLauncherOrchestrator();
        using var vm = CreateInitialized(core);
        var plugins = vm.Plugins;

        plugins.SetPanelWidth(LauncherPluginsViewModel.SideBySideWidth);
        Assert.True(plugins.IsSideBySide);
        Assert.Equal((0, 0, 1), (plugins.DiscoverRow, plugins.DiscoverColumn, plugins.DiscoverColumnSpan));
        Assert.Equal((0, 1, 1), (plugins.InstalledRow, plugins.InstalledColumn, plugins.InstalledColumnSpan));

        plugins.SetPanelWidth(LauncherPluginsViewModel.SideBySideWidth - 1);
        Assert.False(plugins.IsSideBySide);
        Assert.Equal((0, 0, 2), (plugins.InstalledRow, plugins.InstalledColumn, plugins.InstalledColumnSpan));
        Assert.Equal((1, 0, 2), (plugins.DiscoverRow, plugins.DiscoverColumn, plugins.DiscoverColumnSpan));
    }
}
