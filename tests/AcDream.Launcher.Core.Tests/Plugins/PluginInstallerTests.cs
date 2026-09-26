using System.Diagnostics;
using System.Net;
using System.Text;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Tests.Updates;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;
using AcDream.Tests.Fixtures.PluginIcons;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class PluginInstallerTests
{
    private const string Repo = "shaneedwards/openac-plugin-hello";
    private const string Id = "edwards.hello";

    [Fact]
    public async Task InstallRejectsAZipEntryOverTheSharedContractCap()
    {
        using var fixture = new Fixture();
        // 70 MiB: over the contract's 64 MiB per-entry/zip caps, under every invented cap this
        // installer used before it read them off the plan (256/512 MiB).
        byte[] oversizedPayload = new byte[70 * 1024 * 1024];
        Random.Shared.NextBytes(oversizedPayload);
        byte[] manifestBytes = Encoding.UTF8.GetBytes(Fixture.ManifestJson(Id, "0.1.0"));
        byte[] zipBytes = UpdateTestData.CreateZip(
        [
            ("plugin.json", manifestBytes, null),
            ($"{Id}.dll", oversizedPayload, null),
        ]);
        var release = new Fixture.Release(
            Id,
            "0.1.0",
            manifestBytes,
            zipBytes,
            UpdateTestData.Sha256(zipBytes),
            $"{Id}-0.1.0.zip");
        fixture.RegisterRelease(Repo, release);

        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));
    }

    [Fact]
    public async Task InstallSucceedsAndWritesAConfirmedRecord()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        PluginInstallResult result = await fixture.Installer.InstallOrUpdateAsync(
            Repo,
            release.Tag,
            catalog: null,
            clientResolution: null);

        Assert.Equal(Id, result.Id);
        Assert.Equal("0.1.0", result.Version);
        Assert.False(result.WasUpdate);
        Assert.True(File.Exists(
            Path.Combine(fixture.Paths.PluginsDirectory, Id, "plugin.json")));
        InstalledPluginRecord? record = fixture.RecordStore.Find(Id);
        Assert.NotNull(record);
        Assert.Equal("0.1.0", record!.Version);
        Assert.Equal("v0.1.0", record.Tag);
        Assert.Null(record.Pending);
    }

    [Fact]
    public async Task InstallNeverFetchesLatestAssetOnlyThePinnedTag()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);

        Uri latestManifestUri = GitHubReleaseLocator.LatestAsset(Repo, "plugin.json");
        Assert.DoesNotContain(fixture.Handler.Requests, uri => uri == latestManifestUri);
    }

    [Fact]
    public async Task InstallLeavesNoEmptyStagingOrTrashFolder()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);

        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".staging")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".trash")));
    }

    [Fact]
    public async Task UpdateLeavesNoEmptyStagingOrTrashFolder()
    {
        using var fixture = new Fixture();
        var first = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, first);
        await fixture.Installer.InstallOrUpdateAsync(Repo, first.Tag, null, null);

        var second = fixture.BuildRelease(Id, "0.2.0");
        fixture.RegisterRelease(Repo, second);
        await fixture.Installer.InstallOrUpdateAsync(Repo, second.Tag, null, null);

        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".staging")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".trash")));
    }

    [Fact]
    public async Task RemoveLeavesNoEmptyTrashFolder()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);
        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);

        fixture.Installer.Remove(Id, deleteStorage: false);

        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".trash")));
    }

    [Fact]
    public void RecoverReclaimsLeftoverEmptyStagingAndTrashFolders()
    {
        using var fixture = new Fixture();
        string stagingRoot = Path.Combine(fixture.Paths.PluginsDirectory, ".staging");
        string trashRoot = Path.Combine(fixture.Paths.PluginsDirectory, ".trash");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(trashRoot);

        fixture.Installer.Recover();

        Assert.False(Directory.Exists(stagingRoot));
        Assert.False(Directory.Exists(trashRoot));
    }

    [Fact]
    public async Task InstallNeverDeletesAStagingFolderSomethingElseIsStillUsing()
    {
        using var fixture = new Fixture();
        string stagingRoot = Path.Combine(fixture.Paths.PluginsDirectory, ".staging");
        string strayDirectory = Path.Combine(stagingRoot, "someone-elses-transaction");
        Directory.CreateDirectory(strayDirectory);
        File.WriteAllText(Path.Combine(strayDirectory, "in-progress.txt"), "still here");
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);

        Assert.True(Directory.Exists(strayDirectory));
        Assert.True(File.Exists(Path.Combine(strayDirectory, "in-progress.txt")));
    }

    [Fact]
    public async Task HashMismatchAbortsAndLeavesNoStaging()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release, shaFileSha256Override: new string('f', 64));

        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        string stagingRoot = Path.Combine(fixture.Paths.PluginsDirectory, ".staging");
        Assert.True(
            !Directory.Exists(stagingRoot)
            || !Directory.EnumerateFileSystemEntries(stagingRoot).Any());
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id)));
        Assert.Null(fixture.RecordStore.Find(Id));
    }

    [Fact]
    public async Task RetryAfterFailedDownloadSucceeds()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release, shaFileSha256Override: new string('f', 64));
        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        fixture.RegisterRelease(Repo, release);
        PluginInstallResult result = await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);

        Assert.Equal(Id, result.Id);
        Assert.True(File.Exists(
            Path.Combine(fixture.Paths.PluginsDirectory, Id, "plugin.json")));
    }

    [Fact]
    public async Task ZipManifestMustMatchReleaseManifest()
    {
        using var fixture = new Fixture();
        byte[] releaseManifestBytes = Encoding.UTF8.GetBytes(Fixture.ManifestJson(Id, "0.1.0"));
        byte[] zipManifestBytes = Encoding.UTF8.GetBytes(
            Fixture.ManifestJson(Id, "0.1.0", displayName: "Not The Same"));
        byte[] zipBytes = UpdateTestData.CreateZip(
        [
            ("plugin.json", zipManifestBytes, null),
            ($"{Id}.dll", Encoding.UTF8.GetBytes("binary"), null),
        ]);
        var release = new Fixture.Release(
            Id,
            "0.1.0",
            releaseManifestBytes,
            zipBytes,
            UpdateTestData.Sha256(zipBytes),
            $"{Id}-0.1.0.zip");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        Assert.Contains("plugin.json", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id)));
    }

    [Fact]
    public async Task InstallSucceedsWithAValidIconMatchingItsPublishedAsset()
    {
        using var fixture = new Fixture();
        byte[] icon = PngTestData.Valid();
        var release = fixture.BuildRelease(Id, "0.1.0", icon: icon);
        fixture.RegisterRelease(Repo, release, iconAssetBytes: icon);

        PluginInstallResult result = await fixture.Installer.InstallOrUpdateAsync(
            Repo, release.Tag, catalog: null, clientResolution: null);

        Assert.Equal(Id, result.Id);
        Assert.True(File.Exists(
            Path.Combine(fixture.Paths.PluginsDirectory, Id, LauncherPluginIcon.FileName)));
    }

    [Fact]
    public async Task InstallRefusedWhenTheIconAssetIsNotRegistered()
    {
        using var fixture = new Fixture();
        byte[] icon = PngTestData.Valid();
        var release = fixture.BuildRelease(Id, "0.1.0", icon: icon);
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        Assert.Contains("missing its icon.png asset", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id)));
    }

    [Fact]
    public async Task InstallRefusedWhenTheIconAssetDiffersFromTheZipCopy()
    {
        using var fixture = new Fixture();
        byte[] icon = PngTestData.Valid();
        var release = fixture.BuildRelease(Id, "0.1.0", icon: icon);
        fixture.RegisterRelease(Repo, release, iconAssetBytes: PngTestData.WrongDimensions());

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        Assert.Contains("icon.png does not match", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id)));
    }

    [Fact]
    public async Task InstallRefusedWhenTheIconAssetIsRateLimited()
    {
        using var fixture = new Fixture();
        byte[] icon = PngTestData.Valid();
        var release = fixture.BuildRelease(Id, "0.1.0", icon: icon);
        fixture.RegisterRelease(Repo, release, iconAssetRateLimited: true);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        Assert.Equal("GitHub is rate limiting; try later.", error.Message);
    }

    [Fact]
    public async Task InstallRefusedWithAnInvalidIconAndNoIconRequest()
    {
        using var fixture = new Fixture();
        byte[] icon = PngTestData.WrongDimensions();
        var release = fixture.BuildRelease(Id, "0.1.0", icon: icon);
        fixture.RegisterRelease(Repo, release);

        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        Uri iconUri = GitHubReleaseLocator.TaggedAsset(
            Repo, "v0.1.0", LauncherPluginIcon.FileName);
        Assert.DoesNotContain(fixture.Handler.Requests, uri => uri == iconUri);
    }

    [Fact]
    public async Task InstallWithNoIconMakesNoIconRequest()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);

        Uri iconUri = GitHubReleaseLocator.TaggedAsset(
            Repo, "v0.1.0", LauncherPluginIcon.FileName);
        Assert.DoesNotContain(fixture.Handler.Requests, uri => uri == iconUri);
    }

    [Fact]
    public async Task InstallRefusedWithATooNewCapabilityVocabularySaysToUpdateTheLauncher()
    {
        using var fixture = new Fixture();
        byte[] manifestBytes = Encoding.UTF8.GetBytes(
            Fixture.ManifestJson(Id, "0.1.0", capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current + 1));
        var release = new Fixture.Release(
            Id, "0.1.0", manifestBytes, [], "", $"{Id}-0.1.0.zip");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        Assert.Equal(PluginInstaller.CapabilityVocabularyRefusal, error.Message);
        Assert.DoesNotContain("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<LauncherPluginCapabilityVersionException>(error.InnerException);
    }

    [Fact]
    public async Task InstallRefusedWithAnUnrecognizedCapabilityUnderAKnownVersionIsAnInvalidManifest()
    {
        using var fixture = new Fixture();
        byte[] manifestBytes = Encoding.UTF8.GetBytes(
            Fixture.ManifestJson(
                Id,
                "0.1.0",
                capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
                capabilitiesJson: """[{ "name": "quantumTeleport", "note": "test" }]"""));
        var release = new Fixture.Release(
            Id, "0.1.0", manifestBytes, [], "", $"{Id}-0.1.0.zip");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        Assert.Contains("The plugin manifest is invalid", error.Message, StringComparison.Ordinal);
        Assert.IsNotType<LauncherPluginCapabilityVersionException>(error.InnerException);
    }

    [Fact]
    public async Task InstallRefusesWhenReleaseCapabilitiesDifferFromWhatWasDisplayed()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildReleaseWithCapabilities(
            Id, "0.1.0", """[{ "name": "network", "note": "Sends usage counts." }]""");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(
                Repo,
                release.Tag,
                catalog: null,
                clientResolution: null,
                displayedCapabilities:
                [
                    new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Chat, "Reads chat."),
                ]));

        Assert.Equal(PluginInstaller.CapabilitiesChangedRefusal, error.Message);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id)));
    }

    [Fact]
    public async Task InstallSucceedsWhenDisplayedCapabilitiesMatchInADifferentOrder()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildReleaseWithCapabilities(
            Id,
            "0.1.0",
            """
            [
              { "name": "chat", "note": "Reads chat." },
              { "name": "network", "note": "Sends usage counts." }
            ]
            """);
        fixture.RegisterRelease(Repo, release);

        PluginInstallResult result = await fixture.Installer.InstallOrUpdateAsync(
            Repo,
            release.Tag,
            catalog: null,
            clientResolution: null,
            displayedCapabilities:
            [
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Network, "Sends usage counts."),
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Chat, "Reads chat."),
            ]);

        Assert.Equal(Id, result.Id);
    }

    [Fact]
    public async Task UnrecordedFolderWithSameNameIsRefused()
    {
        using var fixture = new Fixture();
        string collidingDirectory = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Directory.CreateDirectory(collidingDirectory);
        File.WriteAllText(Path.Combine(collidingDirectory, "leftover.txt"), "not a plugin");
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        Assert.Equal(
            $"A folder named {Id} is already in your plugins folder, and the launcher didn't "
            + "install it. Move or delete that folder, then try again.",
            error.Message);
        Assert.DoesNotContain(
            fixture.Paths.PluginsDirectory, error.Message, StringComparison.Ordinal);
        Assert.Contains(
            fixture.Paths.PluginsDirectory,
            error.InnerException!.Message,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(collidingDirectory, "leftover.txt")));
    }

    [Fact]
    public async Task AFolderThatAppearsDuringTheDownloadIsRefusedAndLeftAlone()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);
        string handPlaced = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Uri zipUri = GitHubReleaseLocator.TaggedAsset(Repo, release.Tag, release.ZipName);
        fixture.Handler.OnRequest = uri =>
        {
            if (uri == zipUri)
            {
                Directory.CreateDirectory(handPlaced);
                File.WriteAllText(Path.Combine(handPlaced, "mine.txt"), "placed by hand");
            }
        };

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        Assert.Contains("launcher didn't install it", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(handPlaced, "mine.txt")));
        Assert.Null(fixture.RecordStore.Find(Id));
    }

    [Fact]
    public async Task AManifestSavedWithAByteOrderMarkInstalls()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0", manifestByteOrderMark: true);
        fixture.RegisterRelease(Repo, release);

        PluginInstallResult result = await fixture.Installer.InstallOrUpdateAsync(
            Repo, release.Tag, null, null);

        Assert.Equal("0.1.0", result.Version);
    }

    [Fact]
    public async Task TheSameRepositorySpelledInAnotherCaseIsAnUpdate()
    {
        using var fixture = new Fixture();
        var first = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, first);
        await fixture.Installer.InstallOrUpdateAsync(Repo, first.Tag, null, null);

        string otherCase = Repo.ToUpperInvariant();
        var second = fixture.BuildRelease(Id, "0.2.0");
        fixture.RegisterRelease(otherCase, second);
        PluginInstallResult result = await fixture.Installer.InstallOrUpdateAsync(
            otherCase, second.Tag, null, null);

        Assert.True(result.WasUpdate);
        Assert.Equal("0.2.0", fixture.RecordStore.Find(Id)!.Version);
    }

    [Fact]
    public async Task InstallRefusedWhenIdCollidesWithAManualPlugin()
    {
        using var fixture = new Fixture();
        string manualDirectory = Path.Combine(fixture.Paths.PluginsDirectory, "someones-copy");
        Directory.CreateDirectory(manualDirectory);
        File.WriteAllText(
            Path.Combine(manualDirectory, "plugin.json"),
            Fixture.ManifestJson(Id, "0.0.1"));
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        Assert.Contains("directly installed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallRefusedWhenIdCollidesWithABundledPlugin()
    {
        using var fixture = new Fixture();
        string clientDirectory = Path.Combine(fixture.Root, "client");
        Directory.CreateDirectory(Path.Combine(clientDirectory, "plugins", Id));
        File.WriteAllText(
            Path.Combine(clientDirectory, "plugins", Id, "plugin.json"),
            Fixture.ManifestJson(Id, "0.1.0"));
        var clientResolution = new ClientVersionResolution(
            ClientVersionState.Verified,
            "ok",
            LauncherVersion.Parse("0.1.7"),
            clientDirectory,
            null,
            null);
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, clientResolution));

        Assert.Contains("client-bundled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallRefusedWhenIdAlreadyInstalledFromADifferentRepo()
    {
        using var fixture = new Fixture();
        fixture.RecordStore.Records.Add(new InstalledPluginRecord(
            Id,
            "someoneElse/openac-plugin-hello",
            PluginInstallSource.Unlisted,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            null));
        fixture.RecordStore.Save();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        Assert.Contains("someoneElse/openac-plugin-hello", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallRefusedWhenIdCollidesWithADifferentCaseSpellingOfAManualPlugin()
    {
        using var fixture = new Fixture();
        string manualDirectory = Path.Combine(fixture.Paths.PluginsDirectory, "someones-copy");
        Directory.CreateDirectory(manualDirectory);
        File.WriteAllText(
            Path.Combine(manualDirectory, "plugin.json"),
            Fixture.ManifestJson("Edwards.Hello", "0.0.1"));
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        Assert.Contains("directly installed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateRefusedWhenNotNewerThanInstalled()
    {
        using var fixture = new Fixture();
        var first = fixture.BuildRelease(Id, "0.2.0");
        fixture.RegisterRelease(Repo, first);
        await fixture.Installer.InstallOrUpdateAsync(Repo, first.Tag, null, null);

        var downgrade = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, downgrade);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, downgrade.Tag, null, null));

        Assert.Contains("not newer", error.Message, StringComparison.Ordinal);
        Assert.Equal("0.2.0", fixture.RecordStore.Find(Id)!.Version);
    }

    [Fact]
    public async Task InstallingAPrereleaseVersionWritesTheChannelAsBeta()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.2.0-beta.1");
        fixture.RegisterRelease(Repo, release);

        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);

        Assert.Equal(PluginReleaseChannel.Beta, fixture.RecordStore.Find(Id)!.Channel);
    }

    [Fact]
    public async Task InstallingAStableVersionWritesTheChannelAsStable()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);

        Assert.Equal(PluginReleaseChannel.Stable, fixture.RecordStore.Find(Id)!.Channel);
    }

    [Fact]
    public async Task AnExplicitBetaChannelWinsOverAStableVersionsInferredChannel()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        await fixture.Installer.InstallOrUpdateAsync(
            Repo, release.Tag, catalog: null, clientResolution: null,
            channel: PluginReleaseChannel.Beta);

        Assert.Equal(PluginReleaseChannel.Beta, fixture.RecordStore.Find(Id)!.Channel);
    }

    [Fact]
    public async Task UpdatingABetaChannelPluginToAStableReleaseKeepsItOnBeta()
    {
        using var fixture = new Fixture();
        var first = fixture.BuildRelease(Id, "0.2.0-beta.1");
        fixture.RegisterRelease(Repo, first);
        await fixture.Installer.InstallOrUpdateAsync(Repo, first.Tag, null, null);
        Assert.Equal(PluginReleaseChannel.Beta, fixture.RecordStore.Find(Id)!.Channel);

        var stable = fixture.BuildRelease(Id, "0.2.0");
        fixture.RegisterRelease(Repo, stable);
        await fixture.Installer.InstallOrUpdateAsync(Repo, stable.Tag, null, null);

        Assert.Equal(PluginReleaseChannel.Beta, fixture.RecordStore.Find(Id)!.Channel);
    }

    /// <summary>
    /// A plugin installs and updates while another process -- a running
    /// client -- holds the session lock. Before plugins were loaded from
    /// memory this was refused with "Close all OpenAC sessions to install or
    /// update plugins."
    /// </summary>
    [Fact]
    public async Task InstallAndUpdateRunWhileASessionIsPlaying()
    {
        using var fixture = new Fixture();
        var first = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, first);

        string ready = Path.Combine(fixture.Root, "lease.ready");
        string releaseFile = Path.Combine(fixture.Root, "lease.release");
        string fixturePath = GetLeaseFixturePath();
        Assert.True(File.Exists(fixturePath), $"Missing fixture: {fixturePath}");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add(fixturePath);
        startInfo.ArgumentList.Add("hold-update-lease");
        startInfo.ArgumentList.Add("session");
        startInfo.ArgumentList.Add(fixture.Paths.DataDirectory);
        startInfo.ArgumentList.Add(ready);
        startInfo.ArgumentList.Add(releaseFile);
        using Process holder = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the lease fixture.");
        try
        {
            await WaitForFileAsync(ready, holder);

            await fixture.Installer.InstallOrUpdateAsync(Repo, first.Tag, null, null);
            var second = fixture.BuildRelease(Id, "0.2.0");
            fixture.RegisterRelease(Repo, second);
            PluginInstallResult result =
                await fixture.Installer.InstallOrUpdateAsync(Repo, second.Tag, null, null);

            Assert.True(result.WasUpdate);
            Assert.Equal("0.2.0", fixture.RecordStore.Find(Id)!.Version);
            Assert.Contains(
                "\"0.2.0\"",
                File.ReadAllText(Path.Combine(fixture.Paths.PluginsDirectory, Id, "plugin.json")));
        }
        finally
        {
            await File.WriteAllTextAsync(releaseFile, "release");
            if (!holder.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                holder.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// A client update or a move of the install holds the session lock
    /// alone; a plugin write waits for it rather than writing underneath it.
    /// </summary>
    [Fact]
    public async Task InstallIsRefusedWhileAClientUpdateRuns()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        var barrier = new UpdateSessionBarrier(fixture.Paths.DataDirectory);
        Assert.True(barrier.TryAcquireExclusive(out UpdateSessionBarrier.ExclusiveLease? lease));
        using (lease)
        {
            LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(
                () => fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));
            Assert.Equal(PluginInstaller.UpdateInProgressRefusal, error.Message);
        }

        Assert.Null(fixture.RecordStore.Find(Id));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id)));
    }

    /// <summary>Two launchers never write plugins at the same time.</summary>
    [Fact]
    public async Task InstallIsRefusedWhileAnotherPluginWriteRuns()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        var barrier = new UpdateSessionBarrier(fixture.Paths.DataDirectory);
        string lockPath = Path.Combine(
            Path.GetDirectoryName(barrier.LockPath)!,
            PluginInstaller.PluginWriteLockFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(
                () => fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));
            Assert.Equal(PluginInstaller.PluginWriteBusyRefusal, error.Message);
        }

        // The session lock is let go with the refusal: a client update is not
        // left blocked by a write that never happened.
        Assert.True(barrier.TryAcquireExclusive(out UpdateSessionBarrier.ExclusiveLease? exclusive));
        exclusive!.Dispose();
    }

    /// <summary>
    /// The update never moves the player's files folder: a plugin that has a
    /// file in it open keeps it open through the update. Mutation
    /// (2026-09-26): the earlier update, which moved files/ into the new
    /// folder, failed here on Windows because the open file blocks moving its
    /// folder.
    /// </summary>
    [Fact]
    public async Task AnUpdateLeavesThePlayersFilesFolderWhereItIs()
    {
        using var fixture = new Fixture();
        var first = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, first);
        await fixture.Installer.InstallOrUpdateAsync(Repo, first.Tag, null, null);
        string files = fixture.Paths.PluginFilesDirectory(Id);
        Directory.CreateDirectory(files);
        string log = Path.Combine(files, "plugin.log");

        var second = fixture.BuildRelease(Id, "0.2.0");
        fixture.RegisterRelease(Repo, second);
        using (var open = new FileStream(log, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            open.Write("before"u8);
            await fixture.Installer.InstallOrUpdateAsync(Repo, second.Tag, null, null);
            open.Write(" after"u8);
        }

        Assert.Equal("before after", File.ReadAllText(log));
        Assert.Equal("0.2.0", fixture.RecordStore.Find(Id)!.Version);
    }

    /// <summary>
    /// A file the old version shipped and the new one does not is gone after
    /// the update, so a client never loads a stale dependency beside the new
    /// code. Mutation (2026-09-26): writing the new files over the old ones
    /// without first setting the old ones aside left old.dll behind.
    /// </summary>
    [Fact]
    public async Task AnUpdateRemovesFilesTheNewVersionNoLongerShips()
    {
        using var fixture = new Fixture();
        var first = fixture.BuildRelease(
            Id,
            "0.1.0",
            extraEntries: [("lib/old.dll", Encoding.UTF8.GetBytes("old")), ("shared.xml", Encoding.UTF8.GetBytes("v1"))]);
        fixture.RegisterRelease(Repo, first);
        await fixture.Installer.InstallOrUpdateAsync(Repo, first.Tag, null, null);

        var second = fixture.BuildRelease(
            Id,
            "0.2.0",
            extraEntries: [("shared.xml", Encoding.UTF8.GetBytes("v2"))]);
        fixture.RegisterRelease(Repo, second);
        await fixture.Installer.InstallOrUpdateAsync(Repo, second.Tag, null, null);

        string plugin = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Assert.False(File.Exists(Path.Combine(plugin, "lib", "old.dll")));
        Assert.False(Directory.Exists(Path.Combine(plugin, "lib")));
        Assert.Equal("v2", File.ReadAllText(Path.Combine(plugin, "shared.xml")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".staging")));
    }

    [Fact]
    public void RecoveryReconcilesPendingEntry()
    {
        using var fixture = new Fixture();
        string pluginDirectory = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(
            Path.Combine(pluginDirectory, "plugin.json"),
            Fixture.ManifestJson(Id, "0.2.0"));
        fixture.RecordStore.Records.Add(new InstalledPluginRecord(
            Id,
            Repo,
            PluginInstallSource.Listed,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            Pending: new PendingPluginInstall("0.2.0", "v0.2.0", new string('b', 64))));
        fixture.RecordStore.Save();

        fixture.Installer.Recover();

        InstalledPluginRecord? record = fixture.RecordStore.Find(Id);
        Assert.NotNull(record);
        Assert.Null(record!.Pending);
        Assert.Equal("0.2.0", record.Version);
        Assert.Equal("v0.2.0", record.Tag);
    }

    [Fact]
    public void RecoveryKeepsThePreviousVersionWhenAPendingUpdateDidNotComplete()
    {
        using var fixture = new Fixture();
        string pluginDirectory = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(
            Path.Combine(pluginDirectory, "plugin.json"),
            Fixture.ManifestJson(Id, "0.1.0"));
        fixture.RecordStore.Records.Add(new InstalledPluginRecord(
            Id,
            Repo,
            PluginInstallSource.Listed,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            Pending: new PendingPluginInstall("0.2.0", "v0.2.0", new string('b', 64))));
        fixture.RecordStore.Save();

        fixture.Installer.Recover();

        InstalledPluginRecord? record = fixture.RecordStore.Find(Id);
        Assert.NotNull(record);
        Assert.Null(record!.Pending);
        Assert.Equal("0.1.0", record.Version);
    }

    [Fact]
    public void RecoveryDropsAnAbandonedNewInstallPendingEntry()
    {
        using var fixture = new Fixture();
        fixture.RecordStore.Records.Add(new InstalledPluginRecord(
            Id,
            Repo,
            PluginInstallSource.Listed,
            Version: null,
            Tag: null,
            ZipSha256: null,
            DateTimeOffset.UtcNow,
            Pending: new PendingPluginInstall("0.1.0", "v0.1.0", new string('a', 64))));
        fixture.RecordStore.Save();

        fixture.Installer.Recover();

        Assert.Null(fixture.RecordStore.Find(Id));
    }

    [Fact]
    public void RecoveryRestoresTrashWhenFolderMissing()
    {
        using var fixture = new Fixture();
        fixture.RecordStore.Records.Add(new InstalledPluginRecord(
            Id,
            Repo,
            PluginInstallSource.Listed,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            null));
        fixture.RecordStore.Save();
        string trashDirectory = Path.Combine(fixture.Paths.PluginsDirectory, ".trash", $"{Id}-abc123");
        Directory.CreateDirectory(trashDirectory);
        File.WriteAllText(Path.Combine(trashDirectory, "plugin.json"), Fixture.ManifestJson(Id, "0.1.0"));

        fixture.Installer.Recover();

        string restored = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Assert.True(Directory.Exists(restored));
        Assert.True(File.Exists(Path.Combine(restored, "plugin.json")));
        Assert.False(Directory.Exists(trashDirectory));
    }

    [Fact]
    public void RecoveryDiscardsRecordLessTrashInsteadOfRestoringIt()
    {
        using var fixture = new Fixture();
        string trashDirectory = Path.Combine(fixture.Paths.PluginsDirectory, ".trash", "someones-copy-abc123");
        Directory.CreateDirectory(trashDirectory);
        File.WriteAllText(Path.Combine(trashDirectory, "plugin.json"), Fixture.ManifestJson(Id, "0.1.0"));

        fixture.Installer.Recover();

        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, "someones-copy")));
        Assert.False(Directory.Exists(trashDirectory));
    }

    [Fact]
    public void RecoveryDiscardsTrashWhenFolderAlreadyExists()
    {
        using var fixture = new Fixture();
        string keptDirectory = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Directory.CreateDirectory(keptDirectory);
        File.WriteAllText(Path.Combine(keptDirectory, "plugin.json"), Fixture.ManifestJson(Id, "0.2.0"));
        string trashDirectory = Path.Combine(fixture.Paths.PluginsDirectory, ".trash", $"{Id}-abc123");
        Directory.CreateDirectory(trashDirectory);
        File.WriteAllText(Path.Combine(trashDirectory, "plugin.json"), Fixture.ManifestJson(Id, "0.1.0"));

        fixture.Installer.Recover();

        Assert.True(File.Exists(Path.Combine(keptDirectory, "plugin.json")));
        Assert.False(Directory.Exists(trashDirectory));
    }

    [Fact]
    public async Task RemoveDeletesFolderAndRecordButKeepsStorageByDefault()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);
        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);
        string storageDirectory = fixture.Paths.PluginFilesDirectory(Id);
        Directory.CreateDirectory(storageDirectory);
        File.WriteAllText(Path.Combine(storageDirectory, "settings.json"), "{}");

        fixture.Installer.Remove(Id, deleteStorage: false);

        Assert.False(File.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id, "plugin.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id, Id + ".dll")));
        Assert.Null(fixture.RecordStore.Find(Id));
        Assert.True(File.Exists(Path.Combine(storageDirectory, "settings.json")));
    }

    [Fact]
    public async Task RemoveDeletesStorageOnlyWhenAsked()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);
        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);
        string storageDirectory = fixture.Paths.PluginFilesDirectory(Id);
        Directory.CreateDirectory(storageDirectory);
        File.WriteAllText(Path.Combine(storageDirectory, "settings.json"), "{}");

        fixture.Installer.Remove(Id, deleteStorage: true);

        Assert.False(Directory.Exists(storageDirectory));
    }

    /// <summary>
    /// An update replaces the plugin's code and keeps the player's files.
    /// Mutation: dropping the carry of files/ in SwapIntoPlace fails this.
    /// </summary>
    [Fact]
    public async Task UpdateCarriesThePluginsFilesIntoTheNewVersion()
    {
        using var fixture = new Fixture();
        var first = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, first);
        await fixture.Installer.InstallOrUpdateAsync(Repo, first.Tag, null, null);
        string files = fixture.Paths.PluginFilesDirectory(Id);
        Directory.CreateDirectory(Path.Combine(files, "profiles"));
        File.WriteAllText(Path.Combine(files, "profiles", "a.usd"), "mine");

        var second = fixture.BuildRelease(Id, "0.2.0");
        fixture.RegisterRelease(Repo, second);
        await fixture.Installer.InstallOrUpdateAsync(Repo, second.Tag, null, null);

        Assert.Equal("mine", File.ReadAllText(Path.Combine(files, "profiles", "a.usd")));
        Assert.Equal("0.2.0", fixture.RecordStore.Find(Id)!.Version);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".trash")));
    }

    /// <summary>
    /// A folder that holds only files/ (code removed, files kept) takes the
    /// plugin again and keeps them. Mutation: treating that folder as an
    /// unmanaged install in RefuseUnmanagedFolder fails this.
    /// </summary>
    [Fact]
    public async Task ReinstallAfterRemoveFindsTheKeptFiles()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);
        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);
        string files = fixture.Paths.PluginFilesDirectory(Id);
        Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(files, "state.json"), "{}");
        fixture.Installer.Remove(Id, deleteStorage: false);

        fixture.RegisterRelease(Repo, release);
        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);

        Assert.True(File.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id, "plugin.json")));
        Assert.True(File.Exists(Path.Combine(files, "state.json")));
    }

    /// <summary>Mutation: skipping RefusePackagedFilesFolder fails this.</summary>
    [Fact]
    public async Task APackageThatShipsAFilesFolderIsRefused()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(
            Id,
            "0.1.0",
            extraEntries: [("files/defaults.json", Encoding.UTF8.GetBytes("{}"))]);
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(
            () => fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null));

        Assert.Equal(PluginInstaller.PackagedFilesRefusal, error.Message);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id)));
    }

    /// <summary>
    /// An update stopped after the files were staged beside the new code but
    /// before the swap: Recover hands them back instead of deleting the
    /// staging folder with them inside. Mutation: deleting staging folders
    /// without ReturnStagedFiles fails this.
    /// </summary>
    [Fact]
    public void RecoveryReturnsFilesAnInterruptedUpdateStaged()
    {
        using var fixture = new Fixture();
        string plugin = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "plugin.json"), Fixture.ManifestJson(Id, "0.1.0"));
        string staged = Path.Combine(fixture.Paths.PluginsDirectory, ".staging", $"{Id}-abc123", "files");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "state.json"), "{}");
        File.WriteAllText(
            Path.Combine(fixture.Paths.PluginsDirectory, ".staging", $"{Id}-abc123.player-files"),
            Id);

        fixture.Installer.Recover();

        Assert.True(File.Exists(Path.Combine(fixture.Paths.PluginFilesDirectory(Id), "state.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".staging")));
    }

    /// <summary>
    /// An update that fails part-way, after the old version's files were set
    /// aside, puts every one of them back and never touches the player's
    /// files. Mutation (2026-09-26): skipping PutBack on failure left the
    /// folder without its plugin.json.
    /// </summary>
    [Fact]
    public async Task AnUpdateThatFailsPartWayPutsTheOldVersionBack()
    {
        using var fixture = new Fixture();
        var first = fixture.BuildRelease(
            Id,
            "0.1.0",
            extraEntries: [("shared.xml", Encoding.UTF8.GetBytes("v1"))]);
        fixture.RegisterRelease(Repo, first);
        await fixture.Installer.InstallOrUpdateAsync(Repo, first.Tag, null, null);
        string files = fixture.Paths.PluginFilesDirectory(Id);
        Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(files, "state.json"), "mine");
        fixture.Installer.AfterOldCodeSetAside = () => throw new IOException("stopped here");

        var second = fixture.BuildRelease(
            Id,
            "0.2.0",
            extraEntries: [("shared.xml", Encoding.UTF8.GetBytes("v2")), ("new.xml", [])]);
        fixture.RegisterRelease(Repo, second);
        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(
            () => fixture.Installer.InstallOrUpdateAsync(Repo, second.Tag, null, null));

        string plugin = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Assert.Contains("The previous version is back in place.", error.Message);
        Assert.Contains("\"0.1.0\"", File.ReadAllText(Path.Combine(plugin, "plugin.json")));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(plugin, "shared.xml")));
        Assert.False(File.Exists(Path.Combine(plugin, "new.xml")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(files, "state.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".staging")));

        fixture.Installer.Recover();
        InstalledPluginRecord record = fixture.RecordStore.Find(Id)!;
        Assert.Equal("0.1.0", record.Version);
        Assert.Null(record.Pending);
    }

    /// <summary>
    /// A crash in the middle of an in-place update: the journal names the
    /// old version's files, some are set aside and some new files are in.
    /// Recover puts the old version back. Mutation (2026-09-26): not settling
    /// journals before the staging folders are reclaimed deleted the set-aside
    /// files with them.
    /// </summary>
    [Fact]
    public void RecoveryPutsBackAnInPlaceUpdateACrashInterrupted()
    {
        using var fixture = new Fixture();
        string plugin = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        string staging = Path.Combine(fixture.Paths.PluginsDirectory, ".staging", $"{Id}-abc123");
        Directory.CreateDirectory(Path.Combine(plugin, "files"));
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(staging + ".old");
        File.WriteAllText(Path.Combine(plugin, "files", "state.json"), "mine");
        // Set aside: the old manifest; still in place: the old entry assembly;
        // already in: one new file; still staged: the new manifest.
        File.WriteAllText(Path.Combine(staging + ".old", "plugin.json"), Fixture.ManifestJson(Id, "0.1.0"));
        File.WriteAllText(Path.Combine(plugin, Id + ".dll"), "old");
        File.WriteAllText(Path.Combine(plugin, "new.xml"), "new");
        File.WriteAllText(Path.Combine(staging, "plugin.json"), Fixture.ManifestJson(Id, "0.2.0"));
        File.WriteAllLines(staging + ".in-place", [Id, Id + ".dll", "plugin.json"]);
        fixture.RecordStore.Records.Add(new InstalledPluginRecord(
            Id,
            Repo,
            PluginInstallSource.Listed,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            Pending: new PendingPluginInstall("0.2.0", "v0.2.0", new string('b', 64))));
        fixture.RecordStore.Save();

        fixture.Installer.Recover();

        Assert.Contains("\"0.1.0\"", File.ReadAllText(Path.Combine(plugin, "plugin.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(plugin, Id + ".dll")));
        Assert.False(File.Exists(Path.Combine(plugin, "new.xml")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(plugin, "files", "state.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".staging")));
        InstalledPluginRecord record = fixture.RecordStore.Find(Id)!;
        Assert.Equal("0.1.0", record.Version);
        Assert.Null(record.Pending);
    }

    /// <summary>
    /// A crash after the new plugin.json went in: the update was complete,
    /// and Recover keeps it and confirms the record.
    /// </summary>
    [Fact]
    public void RecoveryFinishesAnInPlaceUpdateWhoseManifestWasAlreadyIn()
    {
        using var fixture = new Fixture();
        string plugin = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        string staging = Path.Combine(fixture.Paths.PluginsDirectory, ".staging", $"{Id}-abc123");
        Directory.CreateDirectory(plugin);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(staging + ".old");
        File.WriteAllText(Path.Combine(staging + ".old", "plugin.json"), Fixture.ManifestJson(Id, "0.1.0"));
        File.WriteAllText(Path.Combine(plugin, "plugin.json"), Fixture.ManifestJson(Id, "0.2.0"));
        File.WriteAllLines(staging + ".in-place", [Id, "plugin.json"]);
        fixture.RecordStore.Records.Add(new InstalledPluginRecord(
            Id,
            Repo,
            PluginInstallSource.Listed,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            Pending: new PendingPluginInstall("0.2.0", "v0.2.0", new string('b', 64))));
        fixture.RecordStore.Save();

        fixture.Installer.Recover();

        Assert.Contains("\"0.2.0\"", File.ReadAllText(Path.Combine(plugin, "plugin.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".staging")));
        Assert.Equal("0.2.0", fixture.RecordStore.Find(Id)!.Version);
    }

    /// <summary>
    /// A staging folder left by an interrupted install whose package shipped
    /// its own files/ (refused later, or never checked) is not the player's
    /// data: without the marker, recovery deletes it rather than adopting it.
    /// Mutation: returning a staging folder's files/ without the marker fails this.
    /// </summary>
    [Fact]
    public void RecoveryNeverAdoptsAPackagesOwnFilesFolder()
    {
        using var fixture = new Fixture();
        string staged = Path.Combine(fixture.Paths.PluginsDirectory, ".staging", $"{Id}-abc123", "files");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "defaults.json"), "{}");

        fixture.Installer.Recover();

        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".staging")));
    }

    /// <summary>
    /// A removal stopped with the whole folder in the trash and no record:
    /// the code goes, the files come back. Mutation: deleting record-less
    /// trash without returning its files fails this.
    /// </summary>
    [Fact]
    public void RecoveryReturnsFilesFromRecordLessTrash()
    {
        using var fixture = new Fixture();
        string trash = Path.Combine(fixture.Paths.PluginsDirectory, ".trash", $"{Id}-abc123");
        Directory.CreateDirectory(Path.Combine(trash, "files"));
        File.WriteAllText(Path.Combine(trash, "plugin.json"), Fixture.ManifestJson(Id, "0.1.0"));
        File.WriteAllText(Path.Combine(trash, "files", "state.json"), "{}");

        fixture.Installer.Recover();

        Assert.True(File.Exists(Path.Combine(fixture.Paths.PluginFilesDirectory(Id), "state.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id, "plugin.json")));
        Assert.False(Directory.Exists(trash));
    }

    /// <summary>
    /// A hand-unzipped plugin's own files do not count against the install
    /// limits or content rules. Mutation: walking files/ in DirectInstallCheck fails this.
    /// </summary>
    [Fact]
    public void DirectInstallCheckIgnoresThePluginsOwnFiles()
    {
        using var fixture = new Fixture();
        string directory = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Directory.CreateDirectory(Path.Combine(directory, "files"));
        File.WriteAllText(Path.Combine(directory, "plugin.json"), Fixture.ManifestJson(Id, "0.1.0"));
        File.WriteAllBytes(Path.Combine(directory, $"{Id}.dll"), []);
        File.WriteAllBytes(Path.Combine(directory, "files", "stray.exe"), [0x4D, 0x5A]);

        string? refusal = DirectInstallCheck.Refusal(
            directory,
            LauncherPluginManifest.Parse(Fixture.ManifestJson(Id, "0.1.0")));

        Assert.Null(refusal);
    }

    [Fact]
    public void RemoveRefusesAnIdThatIsNotLauncherManaged()
    {
        using var fixture = new Fixture();

        Assert.Throws<LauncherUpdateException>(() => fixture.Installer.Remove(Id, deleteStorage: false));
    }

    [Fact]
    public async Task SetChannelWritesTheChannelAndReturnsTheUpdatedRecord()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);
        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);

        InstalledPluginRecord updated = fixture.Installer.SetChannel(Id, PluginReleaseChannel.Beta);

        Assert.Equal(PluginReleaseChannel.Beta, updated.Channel);
        Assert.Equal(PluginReleaseChannel.Beta, fixture.RecordStore.Find(Id)!.Channel);
    }

    [Fact]
    public void SetChannelRefusesAnIdThatIsNotLauncherManaged()
    {
        using var fixture = new Fixture();

        Assert.Throws<LauncherUpdateException>(
            () => fixture.Installer.SetChannel(Id, PluginReleaseChannel.Beta));
    }

    [Fact]
    public async Task SetChannelRunsWhileASessionIsPlaying()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);
        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);

        var barrier = new UpdateSessionBarrier(fixture.Paths.DataDirectory);
        using (barrier.AcquireSession())
        {
            fixture.Installer.SetChannel(Id, PluginReleaseChannel.Beta);
        }

        Assert.Equal(PluginReleaseChannel.Beta, fixture.RecordStore.Find(Id)!.Channel);
    }

    [Fact]
    public async Task SetChannelIsRefusedWhileAClientUpdateRuns()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);
        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);

        var barrier = new UpdateSessionBarrier(fixture.Paths.DataDirectory);
        Assert.True(barrier.TryAcquireExclusive(out UpdateSessionBarrier.ExclusiveLease? lease));
        using (lease)
        {
            LauncherUpdateException error = Assert.Throws<LauncherUpdateException>(
                () => fixture.Installer.SetChannel(Id, PluginReleaseChannel.Beta));
            Assert.Equal(PluginInstaller.UpdateInProgressRefusal, error.Message);
        }

        Assert.Equal(PluginReleaseChannel.Stable, fixture.RecordStore.Find(Id)!.Channel);
    }

    [Fact]
    public void RemoveDirectDeletesAPassingFolderWhoseNameDiffersFromItsId()
    {
        using var fixture = new Fixture();
        string directory = Path.Combine(fixture.Paths.PluginsDirectory, "someones-copy");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            Fixture.ManifestJson(Id, "0.1.0"));
        File.WriteAllBytes(Path.Combine(directory, $"{Id}.dll"), []);
        string storageDirectory = fixture.Paths.PluginFilesDirectory(Id);
        Directory.CreateDirectory(storageDirectory);
        File.WriteAllText(Path.Combine(storageDirectory, "settings.json"), "{}");

        fixture.Installer.RemoveDirect(directory, deleteStorage: true);

        Assert.False(Directory.Exists(directory));
        Assert.False(Directory.Exists(storageDirectory));
    }

    [Fact]
    public void RemoveDirectDeletesARefusedFolderToo()
    {
        using var fixture = new Fixture();
        string directory = Path.Combine(fixture.Paths.PluginsDirectory, "broken-copy");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            Fixture.ManifestJson(Id, "0.1.0"));
        // No entry DLL: DirectInstallCheck refuses this folder, but removal must still work.

        fixture.Installer.RemoveDirect(directory, deleteStorage: false);

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void RemoveDirectRefusesADirectoryWhoseParentIsNotThePluginsDirectory()
    {
        using var fixture = new Fixture();
        string outsideDirectory = Path.Combine(fixture.Root, "elsewhere", "someones-copy");
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllText(
            Path.Combine(outsideDirectory, "plugin.json"),
            Fixture.ManifestJson(Id, "0.1.0"));

        Assert.Throws<LauncherUpdateException>(
            () => fixture.Installer.RemoveDirect(outsideDirectory, deleteStorage: false));
        Assert.True(Directory.Exists(outsideDirectory));
    }

    [Fact]
    public async Task RemoveDirectRefusesAManagedPluginsFolder()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);
        await fixture.Installer.InstallOrUpdateAsync(Repo, release.Tag, null, null);

        Assert.Throws<LauncherUpdateException>(() => fixture.Installer.RemoveDirect(
            Path.Combine(fixture.Paths.PluginsDirectory, Id), deleteStorage: false));
    }

    [Fact]
    public void RemoveDirectDeletesNoStorageWhenTheIdIsNotPatternValid()
    {
        using var fixture = new Fixture();
        string directory = Path.Combine(fixture.Paths.PluginsDirectory, "someones-copy");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            Fixture.ManifestJson("not_a_valid_id", "0.1.0"));
        string storageDirectory = Path.Combine(
            fixture.Paths.ConfigDirectory, "plugins", "not_a_valid_id");
        Directory.CreateDirectory(storageDirectory);
        File.WriteAllText(Path.Combine(storageDirectory, "settings.json"), "{}");

        fixture.Installer.RemoveDirect(directory, deleteStorage: true);

        Assert.False(Directory.Exists(directory));
        Assert.True(Directory.Exists(storageDirectory));
    }

    [Fact]
    public void RemoveDirectTakesTheExclusiveLease()
    {
        using var fixture = new Fixture();
        string directory = Path.Combine(fixture.Paths.PluginsDirectory, "someones-copy");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            Fixture.ManifestJson(Id, "0.1.0"));

        var barrier = new UpdateSessionBarrier(fixture.Paths.DataDirectory);
        Assert.True(barrier.TryAcquireExclusive(out UpdateSessionBarrier.ExclusiveLease? lease));
        using (lease)
        {
            LauncherUpdateException error = Assert.Throws<LauncherUpdateException>(
                () => fixture.Installer.RemoveDirect(directory, deleteStorage: false));
            Assert.Equal(PluginInstaller.SessionLeaseRefusal, error.Message);
        }

        Assert.True(Directory.Exists(directory));
    }

    private static async Task WaitForFileAsync(string path, Process process)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!File.Exists(path))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Lease fixture exited early with {process.ExitCode}: "
                    + await process.StandardError.ReadToEndAsync());
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Lease fixture did not become ready.");
            }

            await Task.Delay(20);
        }
    }

    private static string GetLeaseFixturePath()
    {
        string root = FindRepositoryRoot();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        return Path.Combine(
            root,
            "tests",
            "AcDream.Launcher.Core.Tests.Fixtures.InstallLeaseHolder",
            "bin",
            configuration,
            "net10.0",
            "AcDream.Launcher.Core.Tests.Fixtures.InstallLeaseHolder.dll");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(
            Path.GetTempPath(),
            "acdream-plugin-installer-tests",
            Guid.NewGuid().ToString("N"));
        public readonly ApplicationPathSet Paths;
        public readonly RoutingHandler Handler = new();
        public readonly HttpClient HttpClient;
        public readonly InstalledPluginRecordStore RecordStore;
        public readonly PluginInventory Inventory;
        public readonly PluginInstaller Installer;

        public Fixture()
        {
            Paths = new ApplicationPathSet(
                Path.Combine(Root, "config"),
                Path.Combine(Root, "data"),
                Path.Combine(Root, "cache"));
            HttpClient = new HttpClient(Handler);
            RecordStore = InstalledPluginRecordStore.ForApplicationPaths(Paths);
            Inventory = new PluginInventory(Paths, RecordStore);
            Installer = new PluginInstaller(Paths, HttpClient, RecordStore, Inventory);
        }

        public void Dispose()
        {
            HttpClient.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        public Release BuildRelease(
            string id,
            string version,
            string? entryDll = null,
            byte[]? icon = null,
            bool manifestByteOrderMark = false,
            IReadOnlyList<(string Name, byte[] Content)>? extraEntries = null)
        {
            entryDll ??= id + ".dll";
            byte[] manifestBytes = Encoding.UTF8.GetBytes(ManifestJson(id, version, entryDll));
            if (manifestByteOrderMark)
            {
                manifestBytes = [.. Encoding.UTF8.Preamble, .. manifestBytes];
            }
            List<(string Name, byte[] Content, int? UnixAttributes)> entries =
            [
                ("plugin.json", manifestBytes, null),
                (entryDll, Encoding.UTF8.GetBytes("binary-" + id), null),
            ];
            if (icon is not null)
            {
                entries.Add((LauncherPluginIcon.FileName, icon, null));
            }

            foreach ((string name, byte[] content) in extraEntries ?? [])
            {
                entries.Add((name, content, null));
            }

            byte[] zipBytes = UpdateTestData.CreateZip(entries);
            string sha256 = UpdateTestData.Sha256(zipBytes);
            return new Release(
                id, version, manifestBytes, zipBytes, sha256, $"{id}-{version}.zip", icon);
        }

        public Release BuildReleaseWithCapabilities(string id, string version, string capabilitiesJson)
        {
            string entryDll = id + ".dll";
            byte[] manifestBytes = Encoding.UTF8.GetBytes(ManifestJson(
                id,
                version,
                entryDll,
                capabilitiesVersion: LauncherPluginCapabilityVocabulary.Current,
                capabilitiesJson: capabilitiesJson));
            byte[] zipBytes = UpdateTestData.CreateZip(
            [
                ("plugin.json", manifestBytes, null),
                (entryDll, Encoding.UTF8.GetBytes("binary-" + id), null),
            ]);
            string sha256 = UpdateTestData.Sha256(zipBytes);
            return new Release(id, version, manifestBytes, zipBytes, sha256, $"{id}-{version}.zip");
        }

        public void RegisterRelease(
            string repo,
            Release release,
            string? shaFileSha256Override = null,
            byte[]? iconAssetBytes = null,
            bool iconAssetRateLimited = false)
        {
            string tag = "v" + release.Version;
            Uri latestManifestUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
            Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(repo, tag, "plugin.json");
            Uri shaUri = GitHubReleaseLocator.TaggedAsset(repo, tag, release.ZipName + ".sha256");
            Uri zipUri = GitHubReleaseLocator.TaggedAsset(repo, tag, release.ZipName);

            Handler.EnqueueRedirect(latestManifestUri, taggedManifestUri);
            Handler.EnqueueOk(taggedManifestUri, release.ManifestBytes);
            Handler.EnqueueOk(
                shaUri,
                Encoding.UTF8.GetBytes(
                    $"{shaFileSha256Override ?? release.Sha256}  {release.ZipName}\n"));
            Handler.EnqueueOk(zipUri, release.ZipBytes);

            Uri iconUri = GitHubReleaseLocator.TaggedAsset(repo, tag, LauncherPluginIcon.FileName);
            if (iconAssetRateLimited)
            {
                Handler.EnqueueRateLimited(iconUri);
            }
            else if (iconAssetBytes is not null)
            {
                Handler.EnqueueOk(iconUri, iconAssetBytes);
            }
        }

        public static string ManifestJson(
            string id,
            string version,
            string? entryDll = null,
            string? displayName = null,
            int? capabilitiesVersion = null,
            string? capabilitiesJson = null)
        {
            string capabilitiesFields = capabilitiesVersion is { } v
                ? $",\n  \"capabilitiesVersion\": {v},\n  \"capabilities\": {capabilitiesJson ?? "[]"}"
                : "";
            return $$"""
            {
              "id": "{{id}}",
              "displayName": "{{displayName ?? id}}",
              "version": "{{version}}",
              "entryDll": "{{entryDll ?? id + ".dll"}}",
              "apiVersion": 1,
              "minHostVersion": "0.1.0",
              "hosts": ["headless"]{{capabilitiesFields}}
            }
            """;
        }

        public sealed record Release(
            string Id,
            string Version,
            byte[] ManifestBytes,
            byte[] ZipBytes,
            string Sha256,
            string ZipName,
            byte[]? IconBytes = null)
        {
            public string Tag => "v" + Version;
        }
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<HttpResponseMessage>> _routes =
            new(StringComparer.Ordinal);

        public readonly List<Uri> Requests = [];

        /// <summary>Runs as each request arrives, so a test can change the disk mid-install.</summary>
        public Action<Uri>? OnRequest;

        public void EnqueueRedirect(Uri from, Uri to) => Route(from).Enqueue(Redirect(to));

        public void EnqueueOk(Uri uri, byte[] body) => Route(uri).Enqueue(Ok(body));

        public void EnqueueRateLimited(Uri uri) =>
            Route(uri).Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri
                ?? throw new InvalidOperationException("Test request has no URI.");
            Requests.Add(uri);
            OnRequest?.Invoke(uri);
            if (_routes.TryGetValue(uri.AbsoluteUri, out Queue<HttpResponseMessage>? queue)
                && queue.Count > 0)
            {
                return Task.FromResult(queue.Dequeue());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private Queue<HttpResponseMessage> Route(Uri uri)
        {
            if (!_routes.TryGetValue(uri.AbsoluteUri, out Queue<HttpResponseMessage>? queue))
            {
                queue = new Queue<HttpResponseMessage>();
                _routes[uri.AbsoluteUri] = queue;
            }

            return queue;
        }

        private static HttpResponseMessage Redirect(Uri location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = location;
            return response;
        }

        private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };
    }
}
