using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Remote;

/// <summary>
/// The listener the phone talks to: <c>GET /status</c> (and a
/// <c>/statusfeed</c> WebSocket that pushes every new status), <c>POST
/// /command</c>, <c>GET /inventory</c>, <c>/settings</c>, <c>/icon</c>.
/// It is its own small HTTP/1.1 server over a plain socket rather than
/// <see cref="HttpListener"/>: on Windows that one needs an http.sys URL
/// reservation (or an administrator) for any address but localhost, which
/// is exactly the LAN-facing case a phone needs, and this one behaves the
/// same on every platform. Every request is answered and closed; only the
/// feed keeps its socket. Requests never touch the game: they read the
/// documents the plugin tick published and queue commands for it.
/// </summary>
internal sealed class RemoteHttpServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;
    private const int MaxConnections = 64;
    private const int RequestTimeoutMs = 10_000;
    private const int FeedKeepAliveMs = 20_000;
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private static readonly byte[] EmptyStatus =
        Encoding.UTF8.GetBytes("{\"schema\":\"" + RemoteStatusBuilder.Schema + "\",\"clientCount\":0,\"clients\":[]}");
    private static readonly byte[] EmptyInventory =
        Encoding.UTF8.GetBytes("{\"schema\":\"" + RemoteInventoryBuilder.Schema + "\",\"version\":0,\"itemCount\":0,\"containers\":[],\"items\":[]}");
    private static readonly byte[] EmptySettings =
        Encoding.UTF8.GetBytes("{\"schema\":\"" + RemoteSettingsBridge.Schema + "\",\"values\":{}}");
    private static readonly byte[] EmptyRuns = "{\"schema\":\"acdream.drakbot.runs/1\",\"count\":0,\"runs\":[]}"u8.ToArray();
    private static readonly byte[] EmptyMaps = "{\"schema\":\"acdream.drakbot.maps/1\",\"count\":0,\"maps\":[]}"u8.ToArray();

    private readonly RemoteOptions _options;
    private readonly IPluginLogger _log;
    private readonly Func<RemoteCommand, bool> _enqueue;
    private readonly Func<uint, Task<byte[]?>>? _renderIcon;
    private readonly IRemoteFrameSource? _frames;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _feeds = new();
    private readonly SemaphoreSlim _connections = new(MaxConnections, MaxConnections);
    private readonly CancellationTokenSource _stopping = new();
    private readonly byte[]? _token;
    private TcpListener? _listener;
    private Task? _accepting;
    private volatile byte[] _status = EmptyStatus;
    private volatile byte[] _inventory = EmptyInventory;
    private volatile byte[] _settings = EmptySettings;
    private volatile byte[]? _dungeon;
    private volatile string _dungeonLandblock = string.Empty;
    private long _requests;

    /// <param name="enqueue">Takes a command off the request thread; false when the plugin is not taking commands.</param>
    /// <param name="renderIcon">Answers an icon request from the tick thread, or null when the host has no icons.</param>
    /// <param name="frames">The host's presented frames, or null when it does not render.</param>
    public RemoteHttpServer(
        RemoteOptions options,
        IPluginLogger log,
        Func<RemoteCommand, bool> enqueue,
        Func<uint, Task<byte[]?>>? renderIcon,
        IRemoteFrameSource? frames = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
        _renderIcon = renderIcon;
        _frames = frames;
        _token = string.IsNullOrEmpty(options.Token) ? null : Encoding.UTF8.GetBytes(options.Token);
    }

    /// <summary>The port actually bound, once started; zero before.</summary>
    public int Port { get; private set; }

    public bool IsListening => _listener is not null;

    public long Requests => Interlocked.Read(ref _requests);

    public int FeedClients => _feeds.Count;

    /// <summary>
    /// Binds the configured port, or the next free one within
    /// <see cref="RemoteOptions.PortSpan"/> so each client of a multi-box
    /// gets its own. Returns the reason when no port could be bound.
    /// </summary>
    public string? TryStart()
    {
        if (_listener is not null)
            return null;
        IPAddress address = _options.BindsEveryInterface ? IPAddress.Any : IPAddress.Loopback;
        string? failure = null;
        for (int port = _options.Port; port < _options.Port + RemoteOptions.PortSpan && port <= 65535; port++)
        {
            var listener = new TcpListener(address, port);
            try
            {
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                listener.Start(backlog: 16);
            }
            catch (SocketException error)
            {
                failure = $"port {port}: {error.SocketErrorCode}";
                listener.Dispose();
                continue;
            }
            _listener = listener;
            Port = port;
            _accepting = Task.Run(() => AcceptLoopAsync(listener, _stopping.Token));
            return null;
        }
        return failure ?? "no port to bind";
    }

    /// <summary>Publishes a new status document and wakes every feed. Any thread.</summary>
    public void PublishStatus(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        _status = json;
        foreach (SemaphoreSlim signal in _feeds.Values)
        {
            try
            {
                if (signal.CurrentCount == 0)
                    signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another publish already woke it; the feed sends the freshest document when it runs.
            }
            catch (ObjectDisposedException)
            {
                // The feed closed between the enumeration and the wake.
            }
        }
    }

    public void PublishInventory(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        _inventory = json;
    }

    /// <summary>The dungeon document for the landblock the character is in; null when outdoors.</summary>
    public void PublishDungeon(string landblock, byte[]? json)
    {
        _dungeonLandblock = landblock;
        _dungeon = json;
    }

    public void PublishSettings(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        _settings = json;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }
            if (!await _connections.WaitAsync(0, stopping).ConfigureAwait(false))
            {
                client.Dispose(); // over the connection cap; the phone retries
                continue;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await ServeAsync(client, stopping).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // A broken connection must never take the listener down.
                }
                finally
                {
                    client.Dispose();
                    _connections.Release();
                }
            }, CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stopping)
    {
        client.NoDelay = true;
        using NetworkStream stream = client.GetStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(RequestTimeoutMs);
        Request? request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
        if (request is null)
            return;
        Interlocked.Increment(ref _requests);
        Response response = await RouteAsync(request, stopping).ConfigureAwait(false);
        if (response.Feed)
        {
            await WriteAsync(stream, response, timeout.Token).ConfigureAwait(false);
            await RunFeedAsync(stream, stopping).ConfigureAwait(false);
            return;
        }
        if (response.Streamer is { } streamer)
        {
            await WriteAsync(stream, response, timeout.Token).ConfigureAwait(false);
            await streamer(stream, stopping).ConfigureAwait(false);
            return;
        }
        await WriteAsync(stream, response, timeout.Token).ConfigureAwait(false);
    }

    // ── Routing ────────────────────────────────────────────────────────

    private async Task<Response> RouteAsync(Request request, CancellationToken stopping)
    {
        string path = request.Path.TrimEnd('/').ToLowerInvariant();
        if (request.Method == "OPTIONS")
            return Response.Empty(204);
        if (path == "/healthz")
            return Response.Text(200, "ok");
        if (path is "/video" || path.StartsWith("/webrtc/", StringComparison.Ordinal))
            return Response.Error(503, "the H.264 stream is not available on this client");
        if (!Authorized(request))
            return Response.Error(401, "unauthorized");
        if (path is "/frame" or "/stream")
        {
            if (request.Method != "GET")
                return Response.Error(405, "method not allowed");
            if (_frames is null || !_frames.IsAvailable)
                return Response.Error(503, "video is not available on this client");
            if (NotThisClient(request))
                return Response.Error(404, "no such client");
            int quality = request.QueryInt("q", 55, 1, 100);
            int maxWidth = request.QueryInt("w", 0, 0, 4096);
            if (path == "/frame")
                return await HandleFrameAsync(quality, maxWidth, stopping).ConfigureAwait(false);
            int fps = request.QueryInt("fps", 0, 0, 30);
            int intervalMs = fps > 0 ? Math.Clamp(1000 / fps, 33, 2000) : DefaultStreamIntervalMs;
            return Response.Mjpeg((socket, ct) => RunMjpegAsync(socket, quality, maxWidth, intervalMs, ct));
        }

        switch (path)
        {
            case "" or "/status" or "/status.json":
                return request.Method == "GET" ? Response.Json(200, _status) : Response.Error(405, "method not allowed");
            case "/statusfeed":
                return request.IsWebSocketUpgrade(out string? key)
                    ? Response.WebSocketAccept(key)
                    : Response.Error(426, "websocket required");
            case "/command":
                return request.Method == "POST" ? HandleCommand(request) : Response.Error(405, "method not allowed");
            case "/inventory" or "/inventory.json":
                return request.Method == "GET" ? ForThisClient(request, _inventory) : Response.Error(405, "method not allowed");
            case "/settings" or "/settings.json":
                return request.Method == "GET" ? ForThisClient(request, _settings) : Response.Error(405, "method not allowed");
            case "/icon":
                return request.Method == "GET" ? await HandleIconAsync(request, stopping).ConfigureAwait(false) : Response.Error(405, "method not allowed");
            case "/runs" or "/runs.json":
                return Response.Json(200, EmptyRuns);
            case "/maps" or "/maps.json":
                return Response.Json(200, EmptyMaps);
            case "/map":
                return Response.Error(404, "map unavailable");
            case "/dungeon" or "/dungeon.json":
            {
                if (request.Method != "GET")
                    return Response.Error(405, "method not allowed");
                if (NotThisClient(request))
                    return Response.Error(404, "no such client");
                byte[]? dungeon = _dungeon;
                string? wanted = request.Query("lb");
                if (dungeon is null)
                    return Response.Error(404, "not in a dungeon");
                if (wanted is not null && !string.Equals(wanted, _dungeonLandblock, StringComparison.OrdinalIgnoreCase))
                    return Response.Error(404, $"the character is in {_dungeonLandblock}, not {wanted}");
                return Response.Json(200, dungeon);
            }
            default:
                return Response.Error(404, "not found");
        }
    }

    /// <summary>A per-client document is only this client's; another pid is not here.</summary>
    private static Response ForThisClient(Request request, byte[] document) =>
        NotThisClient(request) ? Response.Error(404, "no such client") : Response.Json(200, document);

    private static bool NotThisClient(Request request)
    {
        string? pid = request.Query("pid");
        return pid is not null
            && int.TryParse(pid, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            && value != 0 && value != Environment.ProcessId;
    }

    // ── The live view ──────────────────────────────────────────────────

    private const int DefaultStreamIntervalMs = 400;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

    private async Task<Response> HandleFrameAsync(int quality, int maxWidth, CancellationToken stopping)
    {
        byte[]? jpeg;
        try
        {
            jpeg = await _frames!.CaptureJpegAsync(quality, maxWidth, stopping).WaitAsync(FrameTimeout, stopping).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Response.Error(504, "frame timed out");
        }
        if (jpeg is null)
            return Response.Error(503, _frames.IsMinimized ? "minimized" : "notfound");
        return new Response(200, "image/jpeg", jpeg) { CacheControl = "no-store" };
    }

    /// <summary>
    /// One MJPEG viewer: frames as multipart parts until the phone lets go
    /// (the write fails) or the server stops. Frames are captured only
    /// while someone watches, paced to the asked interval measured from
    /// the start of each capture, so the achieved rate is the asked one
    /// rather than the interval plus the capture. A frame the host cannot
    /// give (minimized) is waited out, not an error.
    /// </summary>
    private async Task RunMjpegAsync(NetworkStream stream, int quality, int maxWidth, int intervalMs, CancellationToken stopping)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                clock.Restart();
                byte[]? jpeg;
                try
                {
                    jpeg = await _frames!.CaptureJpegAsync(quality, maxWidth, stopping).WaitAsync(FrameTimeout, stopping).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    jpeg = null;
                }
                if (jpeg is null)
                {
                    await Task.Delay(500, stopping).ConfigureAwait(false);
                    continue;
                }
                byte[] header = Encoding.ASCII.GetBytes(
                    "--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " + jpeg.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
                await stream.WriteAsync(header, stopping).ConfigureAwait(false);
                await stream.WriteAsync(jpeg, stopping).ConfigureAwait(false);
                await stream.WriteAsync(PartEnd, stopping).ConfigureAwait(false);
                await stream.FlushAsync(stopping).ConfigureAwait(false);
                int left = intervalMs - (int)clock.ElapsedMilliseconds;
                if (left > 1)
                    await Task.Delay(left, stopping).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The viewer went away, or the server is stopping.
        }
    }

    private static readonly byte[] PartEnd = "\r\n"u8.ToArray();

    private Response HandleCommand(Request request)
    {
        int pid;
        string action;
        string value;
        try
        {
            using JsonDocument document = JsonDocument.Parse(request.Body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Response.Error(400, "bad command");
            pid = root.TryGetProperty("pid", out JsonElement pidElement) && pidElement.TryGetInt32(out int pidValue) ? pidValue : 0;
            action = root.TryGetProperty("action", out JsonElement actionElement) && actionElement.ValueKind == JsonValueKind.String
                ? actionElement.GetString() ?? string.Empty
                : string.Empty;
            value = root.TryGetProperty("value", out JsonElement valueElement)
                ? valueElement.ValueKind == JsonValueKind.String ? valueElement.GetString() ?? string.Empty : valueElement.GetRawText()
                : string.Empty;
        }
        catch (JsonException)
        {
            return Response.Error(400, "invalid request");
        }
        if (action.Length == 0 || !RemoteCommandApplier.KnownActions.Contains(action))
            return Response.Error(400, "bad command");
        if (pid != 0 && pid != Environment.ProcessId)
            return Response.Error(404, "no such client");
        if (!_enqueue(new RemoteCommand(action, value)))
            return Response.Error(503, "remote control disabled");
        return Response.Json(202, "{\"ok\":true}"u8.ToArray());
    }

    private async Task<Response> HandleIconAsync(Request request, CancellationToken stopping)
    {
        if (_renderIcon is null)
            return Response.Error(503, "icons are not available on this client");
        if (!uint.TryParse(request.Query("did"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint did) || did == 0u)
            return Response.Error(400, "did required");
        string etag = "\"" + did.ToString("X8", CultureInfo.InvariantCulture) + "\"";
        if (string.Equals(request.Header("If-None-Match"), etag, StringComparison.Ordinal))
            return Response.Empty(304) with { ETag = etag };
        byte[]? png;
        try
        {
            png = await _renderIcon(did).WaitAsync(TimeSpan.FromSeconds(5), stopping).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Response.Error(504, "icon timed out");
        }
        if (png is null)
            return Response.Error(404, "icon unavailable");
        return new Response(200, "image/png", png) { ETag = etag, CacheControl = "public, max-age=31536000, immutable" };
    }

    private bool Authorized(Request request)
    {
        if (_token is null)
            return true;
        string? header = request.Header("Authorization");
        if (header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && TokenMatches(header["Bearer ".Length..].Trim()))
        {
            return true;
        }
        return request.Query("token") is { } query && TokenMatches(query);
    }

    private bool TokenMatches(string candidate)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(bytes, _token);
    }

    // ── The status feed ────────────────────────────────────────────────

    private async Task RunFeedAsync(NetworkStream stream, CancellationToken stopping)
    {
        var id = Guid.NewGuid();
        var signal = new SemaphoreSlim(1, 1); // starts signalled: the current document goes out at once
        _feeds[id] = signal;
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        // Pong replies and status pushes share the socket; frames must not interleave.
        using var writes = new SemaphoreSlim(1, 1);
        Task receiving = DrainFramesAsync(stream, writes, closed);
        try
        {
            while (!closed.IsCancellationRequested)
            {
                await signal.WaitAsync(FeedKeepAliveMs, closed.Token).ConfigureAwait(false);
                if (closed.IsCancellationRequested)
                    break;
                await WriteFrameAsync(stream, writes, 0x1, _status, closed.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping, or the phone went away.
        }
        catch (IOException)
        {
            // The phone went away mid-send.
        }
        finally
        {
            _feeds.TryRemove(id, out _);
            closed.Cancel();
            try
            {
                await receiving.ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The receive side closes with the socket.
            }
            signal.Dispose();
        }
    }

    /// <summary>Reads the phone's frames so a close or a ping is noticed; the feed is one-way otherwise.</summary>
    private static async Task DrainFramesAsync(NetworkStream stream, SemaphoreSlim writes, CancellationTokenSource closed)
    {
        byte[] header = new byte[2];
        try
        {
            while (!closed.IsCancellationRequested)
            {
                await ReadExactlyAsync(stream, header, 2, closed.Token).ConfigureAwait(false);
                int opcode = header[0] & 0x0F;
                bool masked = (header[1] & 0x80) != 0;
                long length = header[1] & 0x7F;
                if (length == 126)
                {
                    byte[] extended = new byte[2];
                    await ReadExactlyAsync(stream, extended, 2, closed.Token).ConfigureAwait(false);
                    length = (extended[0] << 8) | extended[1];
                }
                else if (length == 127)
                {
                    byte[] extended = new byte[8];
                    await ReadExactlyAsync(stream, extended, 8, closed.Token).ConfigureAwait(false);
                    length = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(extended);
                }
                if (length > MaxBodyBytes)
                    break;
                byte[] mask = new byte[4];
                if (masked)
                    await ReadExactlyAsync(stream, mask, 4, closed.Token).ConfigureAwait(false);
                byte[] payload = new byte[length];
                await ReadExactlyAsync(stream, payload, (int)length, closed.Token).ConfigureAwait(false);
                if (masked)
                {
                    for (int index = 0; index < payload.Length; index++)
                        payload[index] ^= mask[index & 3];
                }
                if (opcode == 0x8)
                {
                    try
                    {
                        await WriteFrameAsync(stream, writes, 0x8, payload.Length >= 2 ? payload[..2] : [], closed.Token).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // Closing anyway.
                    }
                    break;
                }
                if (opcode == 0x9)
                    await WriteFrameAsync(stream, writes, 0xA, payload, closed.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or EndOfStreamException or ObjectDisposedException)
        {
            // The socket closed under us.
        }
        finally
        {
            closed.Cancel();
        }
    }

    private static async Task WriteFrameAsync(NetworkStream stream, SemaphoreSlim writes, int opcode, byte[] payload, CancellationToken cancellation)
    {
        int headerLength = payload.Length switch
        {
            < 126 => 2,
            <= ushort.MaxValue => 4,
            _ => 10,
        };
        byte[] frame = new byte[headerLength + payload.Length];
        frame[0] = (byte)(0x80 | opcode);
        if (headerLength == 2)
        {
            frame[1] = (byte)payload.Length;
        }
        else if (headerLength == 4)
        {
            frame[1] = 126;
            frame[2] = (byte)(payload.Length >> 8);
            frame[3] = (byte)payload.Length;
        }
        else
        {
            frame[1] = 127;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2, 8), (ulong)payload.Length);
        }
        payload.CopyTo(frame, headerLength);
        await writes.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, cancellation).ConfigureAwait(false);
            await stream.FlushAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            writes.Release();
        }
    }

    // ── HTTP plumbing ──────────────────────────────────────────────────

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellation)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxHeaderBytes);
        try
        {
            int filled = 0;
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                if (filled == buffer.Length)
                    return null; // header too long
                int read = await stream.ReadAsync(buffer.AsMemory(filled), cancellation).ConfigureAwait(false);
                if (read == 0)
                    return null;
                int searchFrom = Math.Max(0, filled - 3);
                filled += read;
                headerEnd = buffer.AsSpan(searchFrom, filled - searchFrom).IndexOf("\r\n\r\n"u8);
                if (headerEnd >= 0)
                    headerEnd += searchFrom;
            }
            string head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            string[] lines = head.Split("\r\n");
            string[] requestLine = lines[0].Split(' ', 3);
            if (requestLine.Length < 2)
                return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 1; index < lines.Length; index++)
            {
                int colon = lines[index].IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                    headers[lines[index][..colon].Trim()] = lines[index][(colon + 1)..].Trim();
            }
            int bodyStart = headerEnd + 4;
            int bodyLength = headers.TryGetValue("Content-Length", out string? lengthText)
                && int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int declared)
                    ? declared
                    : 0;
            if (bodyLength < 0 || bodyLength > MaxBodyBytes)
                return null;
            byte[] body = new byte[bodyLength];
            int have = Math.Min(bodyLength, filled - bodyStart);
            buffer.AsSpan(bodyStart, have).CopyTo(body);
            if (have < bodyLength)
                await ReadExactlyAsync(stream, body, bodyLength, cancellation, have).ConfigureAwait(false);
            string target = requestLine[1];
            int question = target.IndexOf('?', StringComparison.Ordinal);
            return new Request(
                requestLine[0].ToUpperInvariant(),
                question < 0 ? target : target[..question],
                question < 0 ? string.Empty : target[(question + 1)..],
                headers,
                body);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] into, int count, CancellationToken cancellation, int offset = 0)
    {
        while (offset < count)
        {
            int read = await stream.ReadAsync(into.AsMemory(offset, count - offset), cancellation).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }

    private static async Task WriteAsync(NetworkStream stream, Response response, CancellationToken cancellation)
    {
        var head = new StringBuilder(256);
        head.Append("HTTP/1.1 ").Append(response.Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason(response.Status)).Append("\r\n");
        head.Append("Access-Control-Allow-Origin: *\r\n");
        if (response.Feed)
        {
            head.Append("Upgrade: websocket\r\nConnection: Upgrade\r\n");
            head.Append("Sec-WebSocket-Accept: ").Append(response.WebSocketAcceptKey).Append("\r\n");
        }
        else
        {
            head.Append("Connection: close\r\n");
            head.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            head.Append("Access-Control-Allow-Headers: Authorization, Content-Type, If-None-Match\r\n");
            if (response.ContentType is not null)
                head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
            if (response.Streamer is null)
                head.Append("Content-Length: ").Append(response.Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            if (response.ETag is not null)
                head.Append("ETag: ").Append(response.ETag).Append("\r\n");
            head.Append("Cache-Control: ").Append(response.CacheControl ?? "no-store").Append("\r\n");
        }
        head.Append("\r\n");
        byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
        await stream.WriteAsync(headBytes, cancellation).ConfigureAwait(false);
        if (response.Body.Length > 0)
            await stream.WriteAsync(response.Body, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        101 => "Switching Protocols",
        200 => "OK",
        202 => "Accepted",
        204 => "No Content",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        405 => "Method Not Allowed",
        426 => "Upgrade Required",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "OK",
    };

    private sealed record Request(
        string Method,
        string Path,
        string QueryString,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body)
    {
        public string? Header(string name) =>
            Headers.TryGetValue(name, out string? value) ? value : null;

        public string? Query(string name)
        {
            foreach (string pair in QueryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=', StringComparison.Ordinal);
                string key = equals < 0 ? pair : pair[..equals];
                if (string.Equals(Uri.UnescapeDataString(key), name, StringComparison.OrdinalIgnoreCase))
                    return equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            }
            return null;
        }

        public int QueryInt(string name, int fallback, int minimum, int maximum) =>
            int.TryParse(Query(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? Math.Clamp(value, minimum, maximum)
                : fallback;

        public bool IsWebSocketUpgrade(out string? key)
        {
            key = Header("Sec-WebSocket-Key");
            return Method == "GET"
                && string.Equals(Header("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase)
                && (Header("Connection") ?? string.Empty).Contains("upgrade", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(key);
        }
    }

    private sealed record Response(int Status, string? ContentType, byte[] Body)
    {
        public string? ETag { get; init; }
        public string? CacheControl { get; init; }
        public bool Feed { get; init; }
        public string? WebSocketAcceptKey { get; init; }
        /// <summary>Writes the body after the head for as long as the viewer stays; the connection ends with it.</summary>
        public Func<NetworkStream, CancellationToken, Task>? Streamer { get; init; }

        public static Response Mjpeg(Func<NetworkStream, CancellationToken, Task> streamer) =>
            new(200, "multipart/x-mixed-replace; boundary=frame", []) { Streamer = streamer, CacheControl = "no-cache, no-store, must-revalidate" };

        public static Response Empty(int status) => new(status, null, []);
        public static Response Text(int status, string text) => new(status, "text/plain", Encoding.UTF8.GetBytes(text));
        public static Response Json(int status, byte[] json) => new(status, "application/json", json);
        public static Response Error(int status, string message) =>
            Json(status, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["error"] = message }));

        public static Response WebSocketAccept(string? key)
        {
            byte[] hash = SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid));
            return new Response(101, null, []) { Feed = true, WebSocketAcceptKey = Convert.ToBase64String(hash) };
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // Already down.
        }
        _listener = null;
        try
        {
            _accepting?.Wait(500);
        }
        catch (AggregateException)
        {
            // The accept loop ends with a cancelled or closed socket.
        }
        foreach (SemaphoreSlim signal in _feeds.Values)
        {
            try
            {
                if (signal.CurrentCount == 0)
                    signal.Release();
            }
            catch (Exception error) when (error is SemaphoreFullException or ObjectDisposedException)
            {
                // Waking a feed that is already leaving.
            }
        }
        _stopping.Dispose();
    }
}
