using System.Net;
using System.Text;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class PluginReleaseClientTests
{
    [Fact]
    public async Task CapturesTheTagFromTheLatestDownloadRedirect()
    {
        Uri repoUri = GitHubReleaseLocator.LatestAsset("shaneedwards/openac-plugin-hello", "plugin.json");
        Uri taggedUri = GitHubReleaseLocator.TaggedAsset(
            "shaneedwards/openac-plugin-hello",
            "v0.1.0",
            "plugin.json");
        byte[] body = Encoding.UTF8.GetBytes("{\"id\":\"edwards.hello\"}");
        var handler = new SequenceHandler((request, index) => index switch
        {
            0 => Redirect(HttpStatusCode.Found, taggedUri),
            1 => Redirect(
                HttpStatusCode.Found,
                new Uri("https://release-assets.githubusercontent.com/plugin.json")),
            _ => Ok(body),
        });
        var client = PluginReleaseClient.CreateForTransportTest(handler);

        PluginReleaseFetchResult result = await client.FetchDocumentAsync(repoUri);

        Assert.Equal(PluginReleaseFetchStatus.Success, result.Status);
        Assert.Equal("v0.1.0", result.Document!.Tag);
        Assert.Equal(body, result.Document.Content);
    }

    [Fact]
    public async Task ARedirectToADifferentRepoIsUnavailable()
    {
        Uri repoUri = GitHubReleaseLocator.LatestAsset("shaneedwards/openac-plugin-hello", "plugin.json");
        Uri impostorUri = GitHubReleaseLocator.TaggedAsset(
            "attacker/evil-repo",
            "v9.9.9",
            "plugin.json");
        var handler = new SequenceHandler((_, index) => index switch
        {
            0 => Redirect(HttpStatusCode.Found, impostorUri),
            _ => Ok(Encoding.UTF8.GetBytes("{}")),
        });
        var client = PluginReleaseClient.CreateForTransportTest(handler);

        PluginReleaseFetchResult result = await client.FetchDocumentAsync(repoUri);

        Assert.Equal(PluginReleaseFetchStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task ATransportFailureIsUnavailableNotAThrow()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection reset"));
        var client = PluginReleaseClient.CreateForTransportTest(handler);

        PluginReleaseFetchResult result = await client.FetchDocumentAsync(
            new Uri("https://example.test/plugin.json"));

        Assert.Equal(PluginReleaseFetchStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task NonHttpsHopIsRejectedBeforeItIsRequested()
    {
        var start = new Uri("https://example.test/plugin.json");
        var handler = new SequenceHandler((_, _) => Redirect(
            HttpStatusCode.Found,
            new Uri("http://example.test/plugin.json")));
        var client = PluginReleaseClient.CreateForTransportTest(handler);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(
            () => client.FetchDocumentAsync(start));

        Assert.Contains("HTTPS", error.Message, StringComparison.Ordinal);
        Assert.Equal([start], handler.Requests);
    }

    [Fact]
    public async Task AnOversizedBodyIsRejectedWhenNoLengthWasDeclared()
    {
        byte[] body = new byte[PluginReleaseClient.MaximumDocumentBytes + 1];
        var handler = new SequenceHandler((_, _) =>
        {
            HttpResponseMessage response = Ok(body);
            response.Content.Headers.ContentLength = null;
            return response;
        });
        var client = PluginReleaseClient.CreateForTransportTest(handler);

        await Assert.ThrowsAsync<LauncherUpdateException>(
            () => client.FetchDocumentAsync(new Uri("https://example.test/plugins.json")));
    }

    [Fact]
    public async Task AnOversizedDeclaredLengthIsRejectedBeforeStreaming()
    {
        var handler = new SequenceHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")),
            };
            response.Content.Headers.ContentLength = PluginReleaseClient.MaximumDocumentBytes + 1;
            return response;
        });
        var client = PluginReleaseClient.CreateForTransportTest(handler);

        await Assert.ThrowsAsync<LauncherUpdateException>(
            () => client.FetchDocumentAsync(new Uri("https://example.test/plugins.json")));
    }

    [Fact]
    public async Task A404FinalResponseIsUnavailable()
    {
        var handler = new SequenceHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = PluginReleaseClient.CreateForTransportTest(handler);

        PluginReleaseFetchResult result = await client.FetchDocumentAsync(
            new Uri("https://example.test/plugin.json"));

        Assert.Equal(PluginReleaseFetchStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task A504FinalResponseIsUnavailable()
    {
        var handler = new SequenceHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
        var client = PluginReleaseClient.CreateForTransportTest(handler);

        PluginReleaseFetchResult result = await client.FetchDocumentAsync(
            new Uri("https://example.test/plugin.json"));

        Assert.Equal(PluginReleaseFetchStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task A429FinalResponseIsRateLimitedNotUnavailable()
    {
        var handler = new SequenceHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var client = PluginReleaseClient.CreateForTransportTest(handler);

        PluginReleaseFetchResult result = await client.FetchDocumentAsync(
            new Uri("https://example.test/plugin.json"));

        Assert.Equal(PluginReleaseFetchStatus.RateLimited, result.Status);
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, Uri location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = location;
        return response;
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body),
    };

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => throw exception;
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
