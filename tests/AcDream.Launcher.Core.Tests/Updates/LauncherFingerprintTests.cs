using System.Text;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Updates;

/// <summary>A launcher updates itself only when the release's launcher differs from it; a launcher
/// from before fingerprints, or a release without one, still updates by version.</summary>
public sealed class LauncherFingerprintTests : IDisposable
{
    private static readonly string Same = new('a', 64);
    private static readonly string Other = new('b', 64);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "acdream-launcher-fingerprint-tests", Guid.NewGuid().ToString("N"));
    private readonly ApplicationPathSet _paths;
    private readonly string _rid = LauncherRuntimeIdentity.DetectRid();

    public LauncherFingerprintTests() => _paths = UpdateTestData.Paths(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ReleaseManifest Manifest(string version, string minimum)
    {
        var artifact = new ReleaseArtifact(new Uri("https://example.test/a.zip"), Same, 1);
        return new ReleaseManifest(
            LauncherVersion.Parse(version),
            LauncherVersion.Parse(minimum),
            new Dictionary<string, ReleaseArtifact> { ["win-x64"] = artifact },
            new Dictionary<string, ReleaseArtifact> { ["win-x64"] = artifact });
    }

    private static LauncherFingerprintDocument Published(string version, string fingerprint) =>
        new(LauncherVersion.Parse(version), fingerprint);

    public static TheoryData<string, string?, string?, string?, bool, bool> Decisions => new()
    {
        // running version, running fingerprint, published release, published fingerprint → update?, minimum met?
        { "0.1.20", Same, "0.1.21", Same, false, true },
        { "0.1.20", Same.ToUpperInvariant(), "0.1.21", Same, false, true },
        { "0.1.20", Same, "0.1.21", Other, true, false },
        { "0.1.22", Same, "0.1.21", Other, false, true },
        { "0.1.20", null, "0.1.21", Same, true, false },
        { "0.1.20", Same, null, null, true, false },
        { "0.1.20", Same, "0.1.19", Same, true, false },
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public void TheFingerprintDecidesWhenBothAreKnownForThisRelease(
        string running,
        string? runningFingerprint,
        string? publishedVersion,
        string? publishedFingerprint,
        bool expectUpdate,
        bool expectMinimum)
    {
        (bool update, bool minimum) = LauncherUpdater.DecideLauncher(
            Manifest("0.1.21", "0.1.21"),
            LauncherVersion.Parse(running),
            runningFingerprint,
            publishedVersion is null ? null : Published(publishedVersion, publishedFingerprint!));

        Assert.Equal((expectUpdate, expectMinimum), (update, minimum));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"version":"0.1.21","fingerprint":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""", true)]
    [InlineData("""{"schemaVersion":2,"version":"0.1.21","fingerprint":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""", false)]
    [InlineData("""{"schemaVersion":1,"version":"0.1.21","fingerprint":"not-hex"}""", false)]
    [InlineData("""{"schemaVersion":1,"version":"latest","fingerprint":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""", false)]
    [InlineData("""{"schemaVersion":1,"version":"0.1.21","fingerprint":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","extra":1}""", false)]
    public void OnlyAWellFormedFingerprintIsRead(string json, bool valid)
    {
        if (valid)
        {
            LauncherFingerprintDocument document = ReleaseManifestClient.ParseFingerprint(Encoding.UTF8.GetBytes(json));
            Assert.Equal((LauncherVersion.Parse("0.1.21"), Same), (document.Version, document.Fingerprint));
        }
        else
        {
            Assert.Throws<LauncherUpdateException>(() => ReleaseManifestClient.ParseFingerprint(Encoding.UTF8.GetBytes(json)));
        }
    }

    private sealed class FingerprintHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(cancellationToken);
    }

    public static TheoryData<string> Failures => new() { "timeout", "hang", "reset" };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFingerprintThatCannotBeFetchedIsTreatedAsMissing(string failure)
    {
        using var source = ReleaseManifestClient.CreateForTransportTest(
            new Uri("https://example.test/latest/manifest.json"),
            allowLoopbackHttp: false,
            new FingerprintHandler(async token => failure switch
            {
                "timeout" => throw new TaskCanceledException("The request timed out.", new TimeoutException()),
                "hang" => await Task.Delay(Timeout.Infinite, token).ContinueWith<HttpResponseMessage>(_ => throw new TaskCanceledException()),
                _ => throw new IOException("reset"),
            }));
        source.FingerprintTimeout = TimeSpan.FromMilliseconds(100);

        Assert.Null(await source.FetchLauncherFingerprintAsync());
    }

    [Fact]
    public async Task AskingToStopStillStops()
    {
        using var source = ReleaseManifestClient.CreateForTransportTest(
            new Uri("https://example.test/latest/manifest.json"),
            allowLoopbackHttp: false,
            new FingerprintHandler(async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            }));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.FetchLauncherFingerprintAsync(stop.Token));
    }

    [Fact]
    public async Task AMatchingLauncherStaysAndInstallsTheNewClient()
    {
        using var server = new LocalHttpFixture();
        ConfigureRelease(server, fingerprint: Same);
        using var http = new HttpClient();
        LauncherUpdater updater = CreateUpdater(server, http, Same);
        _ = await updater.InitializeAsync();

        LauncherUpdateCheckResult check = await updater.CheckAsync();

        Assert.False(check.IsLauncherUpdateAvailable);
        Assert.True(check.IsLauncherMinimumSatisfied);
        Assert.True(check.IsClientUpdateAvailable);
        ClientVersionResolution installed = await updater.InstallClientAsync(check);
        Assert.Equal(LauncherVersion.Parse("2.0.0"), installed.Version);
    }

    [Fact]
    public async Task ADifferentLauncherUpdatesFirst()
    {
        using var server = new LocalHttpFixture();
        ConfigureRelease(server, fingerprint: Other);
        using var http = new HttpClient();
        LauncherUpdater updater = CreateUpdater(server, http, Same);
        _ = await updater.InitializeAsync();

        LauncherUpdateCheckResult check = await updater.CheckAsync();

        Assert.True(check.IsLauncherUpdateAvailable);
        Assert.False(check.IsLauncherMinimumSatisfied);
    }

    [Fact]
    public async Task AReleaseWithoutAFingerprintUpdatesByVersion()
    {
        using var server = new LocalHttpFixture();
        ConfigureRelease(server, fingerprint: null);
        using var http = new HttpClient();
        LauncherUpdater updater = CreateUpdater(server, http, Same);
        _ = await updater.InitializeAsync();

        LauncherUpdateCheckResult check = await updater.CheckAsync();

        Assert.True(check.IsLauncherUpdateAvailable);
        Assert.False(check.IsLauncherMinimumSatisfied);
    }

    private LauncherUpdater CreateUpdater(LocalHttpFixture server, HttpClient http, string? fingerprint) =>
        new(
            ReleaseManifestClient.CreateLoopbackFixture(server.UriFor("manifest.json")),
            http,
            new ClientVersionStore(_paths),
            new LauncherSelfUpdateManager(_paths, http),
            LauncherVersion.Parse("1.0.0"),
            _rid,
            Path.Combine(_root, "launcher"),
            launcherFingerprint: fingerprint);

    private void ConfigureRelease(LocalHttpFixture server, string? fingerprint)
    {
        byte[] client = UpdateTestData.ClientZip(_rid, "release-2");
        byte[] launcher = UpdateTestData.LauncherZip(_rid, "release-2");
        server.Add("client.zip", client);
        server.Add("launcher.zip", launcher);
        server.Add(
            "manifest.json",
            UpdateTestData.Manifest("2.0.0", "2.0.0", _rid, server.UriFor("client.zip"), client, server.UriFor("launcher.zip"), launcher),
            contentType: "application/json");
        if (fingerprint is not null)
        {
            server.Add(
                LauncherFingerprintDocument.FileName,
                Encoding.UTF8.GetBytes($$"""{"schemaVersion":1,"version":"2.0.0","fingerprint":"{{fingerprint}}"}"""),
                contentType: "application/json");
        }
    }
}
