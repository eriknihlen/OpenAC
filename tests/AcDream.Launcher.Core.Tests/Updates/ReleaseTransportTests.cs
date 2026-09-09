using System.Net;
using System.Text;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Tests.Updates;

public sealed class LauncherVersionTests
{
    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.9.999999999999999999999", "1.10.0")]
    [InlineData("999999999999999999999.0.0", "1000000000000000000000.0.0")]
    public void StrictSemVerOrdersWithoutNumericOverflow(string lower, string higher)
    {
        LauncherVersion left = LauncherVersion.Parse(lower);
        LauncherVersion right = LauncherVersion.Parse(higher);

        Assert.True(left < right);
        Assert.True(right > left);
        Assert.Equal(0, LauncherVersion.Parse(higher + "+build.7").CompareTo(right));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 1.0.0")]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0+")]
    [InlineData("v1.0.0")]
    public void StrictSemVerRejectsAmbiguousVersions(string value) =>
        Assert.False(LauncherVersion.TryParse(value, out _));

    [Fact]
    public void StrictSemVerIsBoundedAgainstManifestPathAmplification() =>
        Assert.False(LauncherVersion.TryParse(
            "1.0.0+" + new string('a', 129),
            out _));
}

public sealed class ReleaseManifestClientTests
{
    [Theory]
    [InlineData("https://updates.example.test/manifest.json")]
    [InlineData("http://127.0.0.1:43119/manifest.json")]
    [InlineData("http://localhost:43119/manifest.json")]
    public void LocalUpdateFeedOverrideAcceptsOnlySecureOrLoopbackFeeds(string value)
    {
        using ReleaseManifestClient source =
            ReleaseManifestClient.CreateLocalUpdateFeedOverride(new Uri(value));
    }

    [Theory]
    [InlineData("http://updates.example.test/manifest.json")]
    [InlineData("file:///tmp/manifest.json")]
    [InlineData("https://user:secret@updates.example.test/manifest.json")]
    [InlineData("https://updates.example.test/manifest.json?token=secret")]
    [InlineData("https://updates.example.test/manifest.json#fragment")]
    public void LocalUpdateFeedOverrideRejectsRemoteHttpAndCredentialLikeUris(string value) =>
        Assert.Throws<LauncherUpdateException>(() =>
            ReleaseManifestClient.CreateLocalUpdateFeedOverride(new Uri(value)));

    [Fact]
    public async Task FetchesStrictManifestFromLoopbackAndPinsProductionFeed()
    {
        using var server = new LocalHttpFixture();
        byte[] client = UpdateTestData.ClientZip("win-x64");
        byte[] launcher = UpdateTestData.LauncherZip("win-x64");
        server.Add("client.zip", client);
        server.Add("launcher.zip", launcher);
        server.Add(
            "manifest.json",
            UpdateTestData.Manifest(
                "2.1.0",
                "1.5.0",
                "win-x64",
                server.UriFor("client.zip"),
                client,
                server.UriFor("launcher.zip"),
                launcher),
            contentType: "application/json");
        using var http = new HttpClient();
        using var source = ReleaseManifestClient.CreateLoopbackFixture(
            server.UriFor("manifest.json"));

        ReleaseManifest manifest = await source.FetchAsync();

        Assert.Equal("2.1.0", manifest.Version.Value);
        Assert.Equal(client.LongLength, manifest.RequireClient("win-x64").Size);
        // Alpha distribution feed. Nothing about distribution lives in git; the
        // CI release job re-points this URL after each versioned release, so
        // the workflow and this literal must agree.
        Assert.Equal(
            "https://github.com/eriknihlen/OpenAC/releases/latest/download/manifest.json",
            ReleaseManifestClient.ProductionManifestUri.AbsoluteUri);
        Assert.Equal(Uri.UriSchemeHttps, ReleaseManifestClient.ProductionManifestUri.Scheme);
    }

    [Theory]
    [MemberData(nameof(InvalidManifests))]
    public void RejectsWrongVersionRidHashSizeMinimumAndUnknownOrDuplicateFields(
        string json)
    {
        Assert.Throws<LauncherUpdateException>(() =>
            ReleaseManifestClient.Parse(Encoding.UTF8.GetBytes(json)));
    }

    public static TheoryData<string> InvalidManifests => new()
    {
        "{}",
        ValidJson().Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
        ValidJson().Replace("\"version\":\"2.0.0\"", "\"version\":\"02.0.0\""),
        ValidJson().Replace("\"minimumLauncherVersion\":\"1.0.0\"", "\"minimumLauncherVersion\":\"3.0.0\""),
        ValidJson().Replace("win-x64", "WIN_X64"),
        ValidJson().Replace(new string('a', 64), "1234"),
        ValidJson().Replace("\"size\":12", "\"size\":0"),
        ValidJson().Replace("\"size\":12", "\"size\":12,\"extra\":true"),
        ValidJson().Replace("\"version\":\"2.0.0\"", "\"version\":\"2.0.0\",\"version\":\"2.0.1\""),
        ValidJson().Replace("https://example.test/client", "http://example.test/client"),
    };

    [Theory]
    [InlineData("clients", "client")]
    [InlineData("launchers", "launcher")]
    public void ProductionManifestRejectsLoopbackHttpArtifacts(
        string section,
        string artifact)
    {
        string json = ValidJson().Replace(
            $"https://example.test/{artifact}",
            $"http://127.0.0.1/{artifact}",
            StringComparison.Ordinal);

        LauncherUpdateException error = Assert.Throws<LauncherUpdateException>(() =>
            ReleaseManifestClient.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.Contains(section, error.Message, StringComparison.Ordinal);
        Assert.Contains("HTTPS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionRedirectToLoopbackIsRejectedBeforePlaintextRequest()
    {
        var handler = new SequenceHandler((request, _) => Redirect(
            HttpStatusCode.Found,
            new Uri("http://127.0.0.1/manifest.json")));
        using var source = ReleaseManifestClient.CreateForTransportTest(
            ReleaseManifestClient.ProductionManifestUri,
            allowLoopbackHttp: false,
            handler);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(
            () => source.FetchAsync());

        Assert.Contains("HTTPS", error.Message, StringComparison.Ordinal);
        Assert.Equal([ReleaseManifestClient.ProductionManifestUri], handler.Requests);
    }

    [Fact]
    public async Task HttpsRedirectDowngradeIsRejectedBeforeIntermediateHop()
    {
        var start = new Uri("https://example.test/start");
        var handler = new SequenceHandler((request, _) => Redirect(
            HttpStatusCode.TemporaryRedirect,
            new Uri("http://example.test/plaintext-hop")));
        using var source = ReleaseManifestClient.CreateForTransportTest(
            start,
            allowLoopbackHttp: false,
            handler);

        await Assert.ThrowsAsync<LauncherUpdateException>(() => source.FetchAsync());

        Assert.Equal([start], handler.Requests);
    }

    [Fact]
    public async Task RedirectLoopIsRejectedWithoutRepeatingARequest()
    {
        var first = new Uri("https://example.test/first");
        var second = new Uri("https://example.test/second");
        var handler = new SequenceHandler((request, _) => Redirect(
            HttpStatusCode.PermanentRedirect,
            request.RequestUri == first ? second : first));
        using var source = ReleaseManifestClient.CreateForTransportTest(
            first,
            allowLoopbackHttp: false,
            handler);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(
            () => source.FetchAsync());

        Assert.Contains("loop", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([first, second], handler.Requests);
    }

    [Fact]
    public async Task RedirectLimitRejectsBeforeRequestingTheSixthHop()
    {
        var start = new Uri("https://example.test/hop-0");
        var handler = new SequenceHandler((_, index) => Redirect(
            HttpStatusCode.Found,
            new Uri($"https://example.test/hop-{index + 1}")));
        using var source = ReleaseManifestClient.CreateForTransportTest(
            start,
            allowLoopbackHttp: false,
            handler);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(
            () => source.FetchAsync());

        Assert.Contains("5 redirects", error.Message, StringComparison.Ordinal);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal(new Uri("https://example.test/hop-5"), handler.Requests[^1]);
    }

    private static string ValidJson() =>
        $$$$"""
        {"schemaVersion":1,"version":"2.0.0","minimumLauncherVersion":"1.0.0","clients":{"win-x64":{"url":"https://example.test/client","sha256":"{{{{new string('a', 64)}}}}","size":12}},"launchers":{"win-x64":{"url":"https://example.test/launcher","sha256":"{{{{new string('b', 64)}}}}","size":12}}}
        """;

    private static HttpResponseMessage Redirect(HttpStatusCode status, Uri location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = location;
        return response;
    }

    private sealed class SequenceHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri
                ?? throw new InvalidOperationException("Test request has no URI.");
            int index = Requests.Count;
            Requests.Add(uri);
            return Task.FromResult(respond(request, index));
        }
    }
}

public sealed class VerifiedArtifactDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-download-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task StreamsToStagingWithProgressAndExactDigest()
    {
        using var server = new LocalHttpFixture();
        byte[] bytes = Enumerable.Range(0, 200_000).Select(value => (byte)value).ToArray();
        server.Add("artifact", bytes, chunkSize: 4096);
        using var http = new HttpClient();
        var downloader = new VerifiedArtifactDownloader(http);
        var progress = new List<ArtifactDownloadProgress>();
        string destination = Path.Combine(_root, "artifact.zip");

        VerifiedArtifactDownload result = await downloader.DownloadAsync(
            new ReleaseArtifact(server.UriFor("artifact"), UpdateTestData.Sha256(bytes), bytes.LongLength),
            destination,
            new ImmediateProgress(progress.Add));

        Assert.Equal(bytes.LongLength, result.Size);
        Assert.Equal(UpdateTestData.Sha256(bytes), result.Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.Equal(0, progress[0].BytesReceived);
        Assert.Equal(bytes.LongLength, progress[^1].BytesReceived);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("header")]
    [InlineData("hash")]
    public async Task WrongSizeHeaderPartialBodyAndHashDeleteStaging(string failure)
    {
        using var server = new LocalHttpFixture();
        byte[] bytes = Encoding.UTF8.GetBytes("verified bytes");
        long expected = failure == "short" ? bytes.Length + 5 : bytes.Length;
        long declared = failure == "header" ? bytes.Length + 1 : expected;
        server.Add("artifact", bytes, declaredLength: declared);
        using var http = new HttpClient();
        var downloader = new VerifiedArtifactDownloader(http);
        string destination = Path.Combine(_root, failure + ".zip");
        string hash = failure == "hash" ? new string('0', 64) : UpdateTestData.Sha256(bytes);

        await Assert.ThrowsAsync<LauncherUpdateException>(() => downloader.DownloadAsync(
            new ReleaseArtifact(server.UriFor("artifact"), hash, expected),
            destination));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task CancellationDeletesPartialStaging()
    {
        using var server = new LocalHttpFixture();
        byte[] bytes = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        server.Add("slow", bytes, chunkSize: 1024, chunkDelay: TimeSpan.FromMilliseconds(3));
        using var http = new HttpClient();
        var downloader = new VerifiedArtifactDownloader(http);
        string destination = Path.Combine(_root, "cancel.zip");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(
            new ReleaseArtifact(server.UriFor("slow"), UpdateTestData.Sha256(bytes), bytes.LongLength),
            destination,
            cancellationToken: cancel.Token));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task RefusesAndPreservesPreExistingCallerFile()
    {
        using var server = new LocalHttpFixture();
        byte[] bytes = Encoding.UTF8.GetBytes("network");
        server.Add("artifact", bytes);
        using var http = new HttpClient();
        var downloader = new VerifiedArtifactDownloader(http);
        string destination = Path.Combine(_root, "already-owned.zip");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(destination, "preserve");

        await Assert.ThrowsAsync<LauncherUpdateException>(() => downloader.DownloadAsync(
            new ReleaseArtifact(
                server.UriFor("artifact"),
                UpdateTestData.Sha256(bytes),
                bytes.LongLength),
            destination));

        Assert.Equal("preserve", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task HttpsArtifactRedirectDowngradeIsRejectedBeforePlaintextHop()
    {
        var handler = new RedirectHandler();
        using var http = new HttpClient(handler);
        var downloader = new VerifiedArtifactDownloader(http);
        string destination = Path.Combine(_root, "redirect.zip");

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            downloader.DownloadAsync(
                new ReleaseArtifact(
                    new Uri("https://example.test/artifact"),
                    new string('a', 64),
                    12),
                destination));

        Assert.Contains("HTTPS", error.Message, StringComparison.Ordinal);
        Assert.Equal([new Uri("https://example.test/artifact")], handler.Requests);
        Assert.False(File.Exists(destination));
    }

    private sealed class ImmediateProgress(Action<ArtifactDownloadProgress> callback)
        : IProgress<ArtifactDownloadProgress>
    {
        public void Report(ArtifactDownloadProgress value) => callback(value);
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://example.test/plaintext");
            return Task.FromResult(response);
        }
    }
}
