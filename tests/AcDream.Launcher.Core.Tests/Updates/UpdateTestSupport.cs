using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Updates;

internal sealed class LocalHttpFixture : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Response> _responses =
        new(StringComparer.Ordinal);
    private readonly Task _server;

    public LocalHttpFixture()
    {
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        _server = ServeAsync();
    }

    public Uri BaseUri { get; }

    public Uri UriFor(string relative) => new(BaseUri, relative.TrimStart('/'));

    public void Add(
        string path,
        byte[] body,
        long? declaredLength = null,
        int chunkSize = int.MaxValue,
        TimeSpan? chunkDelay = null,
        string contentType = "application/octet-stream")
    {
        _responses[NormalizePath(path)] = new Response(
            body,
            declaredLength ?? body.LongLength,
            chunkSize,
            chunkDelay ?? TimeSpan.Zero,
            contentType);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _server.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) when (_stop.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(() => RespondAsync(client), CancellationToken.None);
        }
    }

    private async Task RespondAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(
                    stream,
                    Encoding.ASCII,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true);
                string? request = await reader.ReadLineAsync(_stop.Token);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token)))
                {
                }

                string path = request?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ElementAtOrDefault(1) ?? "/";
                if (!_responses.TryGetValue(NormalizePath(path), out Response? response))
                {
                    await WriteHeaderAsync(stream, 404, 0, "text/plain");
                    return;
                }

                await WriteHeaderAsync(
                    stream,
                    200,
                    response.DeclaredLength,
                    response.ContentType);
                int offset = 0;
                while (offset < response.Body.Length)
                {
                    int count = Math.Min(response.ChunkSize, response.Body.Length - offset);
                    await stream.WriteAsync(
                        response.Body.AsMemory(offset, count),
                        _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                    offset += count;
                    if (offset < response.Body.Length && response.ChunkDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(response.ChunkDelay, _stop.Token);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or OperationCanceledException
                                       or ObjectDisposedException
                                       or SocketException)
            {
            }
        }
    }

    private static async Task WriteHeaderAsync(
        Stream stream,
        int status,
        long length,
        string contentType)
    {
        string reason = status == 200 ? "OK" : "Not Found";
        byte[] header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n"
            + $"Content-Length: {length}\r\n"
            + $"Content-Type: {contentType}\r\n"
            + "Connection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.FlushAsync();
    }

    private static string NormalizePath(string path)
    {
        int query = path.IndexOf('?', StringComparison.Ordinal);
        string withoutQuery = query < 0 ? path : path[..query];
        return "/" + withoutQuery.TrimStart('/');
    }

    private sealed record Response(
        byte[] Body,
        long DeclaredLength,
        int ChunkSize,
        TimeSpan ChunkDelay,
        string ContentType);
}

internal static class UpdateTestData
{
    public static ApplicationPathSet Paths(string root) => new(
        Path.Combine(root, "config"),
        Path.Combine(root, "data"),
        Path.Combine(root, "cache"),
        null);

    public static byte[] CreateZip(
        IEnumerable<(string Name, byte[] Content, int? UnixAttributes)> entries,
        CompressionLevel compression = CompressionLevel.NoCompression)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content, int? unixAttributes) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, compression);
                if (unixAttributes is int attributes)
                {
                    entry.ExternalAttributes = attributes << 16;
                }

                if (!name.EndsWith("/", StringComparison.Ordinal))
                {
                    using Stream stream = entry.Open();
                    stream.Write(content);
                }
            }
        }

        return output.ToArray();
    }

    public static byte[] ClientZip(string rid, string marker = "client")
    {
        string suffix = rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
        const int executable = 0x81ED;
        return CreateZip(
        [
            ($"AcDream.App{suffix}", Encoding.UTF8.GetBytes(marker + "-gui"), executable),
            ($"acdream-headless{suffix}", Encoding.UTF8.GetBytes(marker + "-headless"), executable),
            ("assets/readme.txt", Encoding.UTF8.GetBytes(marker), 0x81A4),
        ]);
    }

    public static byte[] LauncherZip(string rid, string marker = "launcher")
    {
        string suffix = rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
        return CreateZip(
        [
            ($"acdream-launcher{suffix}", Encoding.UTF8.GetBytes(marker), 0x81ED),
            ("support.dat", Encoding.UTF8.GetBytes("support-" + marker), 0x81A4),
        ]);
    }

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static byte[] Manifest(
        string version,
        string minimum,
        string rid,
        Uri clientUri,
        byte[] client,
        Uri launcherUri,
        byte[] launcher)
    {
        var value = new
        {
            schemaVersion = 1,
            version,
            minimumLauncherVersion = minimum,
            clients = new Dictionary<string, object>
            {
                [rid] = new
                {
                    url = clientUri.AbsoluteUri,
                    sha256 = Sha256(client),
                    size = client.LongLength,
                },
            },
            launchers = new Dictionary<string, object>
            {
                [rid] = new
                {
                    url = launcherUri.AbsoluteUri,
                    sha256 = Sha256(launcher),
                    size = launcher.LongLength,
                },
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(value);
    }
}
