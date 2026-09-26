using System.Globalization;
using System.Text.Json;

namespace AcDream.Launcher.Core.Status;

/// <summary>A public server from the TreeStats list, with its latest reported player count.</summary>
public sealed record KnownServer(
    string Name,
    string Host,
    int Port,
    string? Type,
    string? Software,
    string? Description,
    Uri? Website,
    Uri? Discord,
    int? PlayerCount);

/// <summary>The known servers, and whether they came from the network or from the copy saved last
/// time.</summary>
public sealed record KnownServerList(
    IReadOnlyList<KnownServer> Servers,
    bool IsFromCache,
    DateTimeOffset SavedAt);

/// <summary>
/// The server list TreeStats publishes, with the player counts it reports. Each successful fetch
/// is saved, so the list still opens offline; with nothing saved and no network there is no list.
/// </summary>
public sealed class KnownServerCatalog(
    HttpClient httpClient,
    string cacheDirectory,
    TimeProvider? timeProvider = null)
{
    public static readonly Uri ServersUri = new("https://treestats.net/servers.json");
    public static readonly Uri PlayerCountsUri = new("https://treestats.net/player_counts-latest.json");

    private const int MaximumBytes = 1_048_576;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ReuseFor = TimeSpan.FromMinutes(10);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private KnownServerList? _last;

    private string ServersCachePath => Path.Combine(cacheDirectory, "known-servers.json");

    private string CountsCachePath => Path.Combine(cacheDirectory, "known-servers-players.json");

    /// <summary>The list, fetched at most every ten minutes; the saved copy when the network fails;
    /// null when neither is available.</summary>
    public async Task<KnownServerList?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_last is { IsFromCache: false } recent && now - recent.SavedAt < ReuseFor)
            {
                return recent;
            }

            Task<string?> serversTask = TryFetchAsync(ServersUri, IsServerList, cancellationToken);
            Task<string?> countsTask = TryFetchAsync(PlayerCountsUri, IsArray, cancellationToken);
            string? servers = await serversTask.ConfigureAwait(false);
            string? counts = await countsTask.ConfigureAwait(false);
            bool fromCache = servers is null;
            servers ??= TryReadCache(ServersCachePath);
            counts ??= TryReadCache(CountsCachePath);
            if (servers is null || !IsServerList(servers))
            {
                return _last;
            }

            DateTimeOffset savedAt = fromCache ? CacheTime(ServersCachePath) ?? now : now;
            _last = new KnownServerList(Parse(servers, counts), fromCache, savedAt);
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The servers in a servers.json, sorted by name. An entry without a name, a host or a
    /// usable port, or that is not an object, is left out; a field of an unexpected kind is read as
    /// missing. Player counts come from a player_counts document when it names the server, and from
    /// the server's own entry otherwise. A document that is not a JSON array gives no servers.</summary>
    public static IReadOnlyList<KnownServer> Parse(string serversJson, string? playerCountsJson = null)
    {
        Dictionary<string, int> counts = playerCountsJson is null ? [] : ParsePlayerCounts(playerCountsJson);
        var servers = new List<KnownServer>();
        foreach (JsonElement item in ArrayItems(serversJson))
        {
            if (item.ValueKind != JsonValueKind.Object
                || Text(item, "name") is not { } name
                || Text(item, "host") is not { } host
                || Whole(item, "port") is not { } port
                || port is < 1 or > 65535)
            {
                continue;
            }

            int? players = counts.TryGetValue(name, out int count)
                ? count
                : item.TryGetProperty("players", out JsonElement reported) && reported.ValueKind == JsonValueKind.Object
                    ? Whole(reported, "count") is >= 0 and { } value ? value : null
                    : null;
            servers.Add(new KnownServer(
                name,
                host,
                port,
                Text(item, "type"),
                Text(item, "software"),
                Text(item, "description"),
                Link(item, "website_url"),
                Link(item, "discord_url"),
                players));
        }

        return [.. servers.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Server name to player count from a player_counts document, with the same tolerance.</summary>
    private static Dictionary<string, int> ParsePlayerCounts(string json)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in ArrayItems(json))
        {
            if (item.ValueKind == JsonValueKind.Object
                && Text(item, "server") is { } server
                && Whole(item, "count") is >= 0 and { } count)
            {
                counts[server] = count;
            }
        }

        return counts;
    }

    /// <summary>The items of a JSON array, or none when the text is not one.</summary>
    private static JsonElement[] ArrayItems(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? [.. document.RootElement.EnumerateArray().Select(item => item.Clone())]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Only a list with at least one usable server replaces the saved copy.</summary>
    private static bool IsServerList(string json) => Parse(json).Count > 0;

    private static bool IsArray(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    /// <summary>A whole number written as a number or as a string, such as the port, which the list
    /// writes as a string; null for anything else.</summary>
    private static int? Whole(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString()?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number) => number,
            _ => null,
        };
    }

    /// <summary>A website or Discord link, kept only when it is an absolute http or https address.</summary>
    private static Uri? Link(JsonElement item, string property) =>
        Text(item, property) is { } text
        && Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    /// <summary>Downloads a document and saves it for offline use, but only when <paramref name="usable"/>
    /// accepts it; null when the download fails or is refused.</summary>
    private async Task<string?> TryFetchAsync(Uri uri, Func<string, bool> usable, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);
        try
        {
            using HttpResponseMessage response = await httpClient
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(MaximumBytes, timeout.Token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!usable(text))
            {
                return null;
            }

            TryWriteCache(uri == ServersUri ? ServersCachePath : CountsCachePath, text);
            return text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return null;
        }
    }

    private static string? TryReadCache(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTimeOffset? CacheTime(string path)
    {
        try
        {
            return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Saves a fetched document; a list that cannot be saved still shows this time.</summary>
    private static void TryWriteCache(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The list still shows; it is only not there offline next time.
        }
    }
}
