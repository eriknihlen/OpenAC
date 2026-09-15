using System.Net;
using System.Text;
using AcDream.DrakBot.Tests.Fakes;

namespace AcDream.DrakBot.Remote.Tests;

public sealed class RemoteStreamTests
{
    /// <summary>Hands out numbered "JPEGs" and counts how often it was asked.</summary>
    private sealed class FakeFrames : IRemoteFrameSource
    {
        public bool IsAvailable { get; set; } = true;
        public bool IsMinimized { get; set; }
        public int Captures;
        public (int Quality, int MaxWidth) LastAsk;

        public Task<byte[]?> CaptureJpegAsync(int quality, int maxWidth, CancellationToken cancellation)
        {
            int n = Interlocked.Increment(ref Captures);
            LastAsk = (quality, maxWidth);
            if (IsMinimized)
                return Task.FromResult<byte[]?>(null);
            return Task.FromResult<byte[]?>(Encoding.ASCII.GetBytes("JPEG#" + n));
        }
    }

    private static (RemoteHttpServer Server, FakeFrames Frames) Start(FakeFrames? frames = null)
    {
        frames ??= new FakeFrames();
        var options = new RemoteOptions { Enabled = true, Port = RemoteHttpServerTests.FreePort() };
        var server = new RemoteHttpServer(options, new FakeLogger(), _ => true, null, frames);
        Assert.Null(server.TryStart());
        return (server, frames);
    }

    [Fact]
    public async Task AFrameIsOneJpegAtTheAskedQualityAndWidth()
    {
        (RemoteHttpServer server, FakeFrames frames) = Start();
        using (server)
        using (var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") })
        {
            using HttpResponseMessage frame = await client.GetAsync("frame?pid=" + Environment.ProcessId + "&q=70&w=640");
            Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
            Assert.Equal("image/jpeg", frame.Content.Headers.ContentType?.MediaType);
            Assert.Equal("JPEG#1", await frame.Content.ReadAsStringAsync());
            Assert.Equal((70, 640), frames.LastAsk);

            using HttpResponseMessage otherClient = await client.GetAsync("frame?pid=1");
            Assert.Equal(HttpStatusCode.NotFound, otherClient.StatusCode);

            frames.IsMinimized = true;
            using HttpResponseMessage minimized = await client.GetAsync("frame");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, minimized.StatusCode);
            Assert.Contains("minimized", await minimized.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WithoutFramesVideoIsAbsent()
    {
        var options = new RemoteOptions { Enabled = true, Port = RemoteHttpServerTests.FreePort() };
        using var server = new RemoteHttpServer(options, new FakeLogger(), _ => true, null);
        Assert.Null(server.TryStart());
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        using HttpResponseMessage frame = await client.GetAsync("frame");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, frame.StatusCode);
        using HttpResponseMessage stream = await client.GetAsync("stream?fps=5");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, stream.StatusCode);
        using HttpResponseMessage hd = await client.GetAsync("video?pid=1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, hd.StatusCode);
    }

    [Fact]
    public async Task TheStreamIsMultipartAtTheAskedRateAndStopsWhenTheViewerLeaves()
    {
        (RemoteHttpServer server, FakeFrames frames) = Start();
        using (server)
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using HttpResponseMessage response = await client.GetAsync("stream?fps=20&q=40&w=320", HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("multipart/x-mixed-replace", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("frame", response.Content.Headers.ContentType?.Parameters.Single(p => p.Name == "boundary").Value);

            await using Stream body = await response.Content.ReadAsStreamAsync(cancel.Token);
            var read = new MemoryStream();
            byte[] buffer = new byte[4096];
            string text = string.Empty;
            while (!text.EndsWith("JPEG#3\r\n", StringComparison.Ordinal) && !text.Contains("JPEG#4", StringComparison.Ordinal))
            {
                int n = await body.ReadAsync(buffer, cancel.Token);
                if (n == 0)
                    break;
                read.Write(buffer, 0, n);
                text = Encoding.ASCII.GetString(read.ToArray());
            }
            Assert.Contains("--frame\r\nContent-Type: image/jpeg\r\nContent-Length: 6\r\n\r\nJPEG#1\r\n", text, StringComparison.Ordinal);
            Assert.Contains("JPEG#2", text, StringComparison.Ordinal);
            Assert.Contains("JPEG#3", text, StringComparison.Ordinal);
            Assert.Equal((40, 320), frames.LastAsk);

            // Letting go of the response ends the capture on the server side.
            response.Dispose();
            await Task.Delay(300, cancel.Token);
            int captured = frames.Captures;
            await Task.Delay(400, cancel.Token);
            Assert.Equal(captured, frames.Captures);
        }
    }

    [Fact]
    public void CapabilitiesFollowTheHost()
    {
        Assert.False(RemoteCapabilities.For(RemoteHostServices.None).Video);
        var caps = RemoteCapabilities.For(new RemoteHostServices { Frames = new FakeFrames() });
        Assert.True(caps.Video);
        Assert.False(caps.VideoHd);
        Assert.False(caps.Click);
    }
}
