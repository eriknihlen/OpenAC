using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Updates;

public interface IReleaseManifestClient
{
    Task<ReleaseManifest> FetchAsync(CancellationToken cancellationToken = default);
}

public sealed class ReleaseManifestClient : IReleaseManifestClient, IDisposable
{
    public const int MaximumManifestBytes = 1024 * 1024;
    public const long MaximumArtifactBytes = 4L * 1024 * 1024 * 1024;
    public const int MaximumRedirects = 5;

    public static Uri ProductionManifestUri { get; } = new(
        "https://github.com/eriknihlen/OpenAC/releases/latest/download/manifest.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _manifestUri;
    private readonly bool _allowLoopbackHttp;

    public ReleaseManifestClient(TimeSpan? timeout = null)
        : this(
            ProductionManifestUri,
            allowLoopbackHttp: false,
            CreateRedirectDisabledHandler(),
            timeout)
    {
    }

    private ReleaseManifestClient(
        Uri manifestUri,
        bool allowLoopbackHttp,
        HttpMessageHandler handler,
        TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        ArgumentNullException.ThrowIfNull(handler);
        _manifestUri = manifestUri;
        _allowLoopbackHttp = allowLoopbackHttp;
        RequireTransport(_manifestUri, "manifest", _allowLoopbackHttp);
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OpenAC-launcher/1");
    }

    internal static ReleaseManifestClient CreateLoopbackFixture(
        Uri manifestUri,
        TimeSpan? timeout = null) => new(
            manifestUri,
            allowLoopbackHttp: true,
            CreateRedirectDisabledHandler(),
            timeout);

    public static ReleaseManifestClient CreateLocalUpdateFeedOverride(
        Uri manifestUri,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        if (!string.IsNullOrEmpty(manifestUri.UserInfo)
            || !string.IsNullOrEmpty(manifestUri.Query)
            || !string.IsNullOrEmpty(manifestUri.Fragment))
        {
            throw new LauncherUpdateException(
                "A process-local manifest URI cannot contain user information, "
                + "a query, or a fragment.");
        }

        bool allowLoopbackHttp = string.Equals(
                manifestUri.Scheme,
                Uri.UriSchemeHttp,
                StringComparison.Ordinal)
            && manifestUri.IsLoopback;
        if (!string.Equals(
                manifestUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.Ordinal)
            && !allowLoopbackHttp)
        {
            throw new LauncherUpdateException(
                "A process-local manifest URI must use HTTPS "
                + "(loopback HTTP is fixture-only).");
        }

        return new ReleaseManifestClient(
            manifestUri,
            allowLoopbackHttp,
            CreateRedirectDisabledHandler(),
            timeout);
    }

    internal static ReleaseManifestClient CreateForTransportTest(
        Uri manifestUri,
        bool allowLoopbackHttp,
        HttpMessageHandler handler) => new(
            manifestUri,
            allowLoopbackHttp,
            handler,
            TimeSpan.FromSeconds(15));

    public async Task<ReleaseManifest> FetchAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            Uri current = _manifestUri;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (int redirectCount = 0;;)
            {
                RequireTransport(current, "manifest redirect", _allowLoopbackHttp);
                if (!visited.Add(current.AbsoluteUri))
                {
                    throw new LauncherUpdateException(
                        "The release manifest redirect chain contains a loop.");
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using HttpResponseMessage response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount >= MaximumRedirects)
                    {
                        throw new LauncherUpdateException(
                            $"The release manifest exceeded {MaximumRedirects} redirects.");
                    }

                    Uri? location = response.Headers.Location;
                    if (location is null)
                    {
                        throw new LauncherUpdateException(
                            "The release manifest redirect has no Location header.");
                    }

                    Uri next = location.IsAbsoluteUri
                        ? location
                        : new Uri(current, location);
                    RequireTransport(next, "manifest redirect", _allowLoopbackHttp);
                    current = next;
                    redirectCount++;
                    continue;
                }

                response.EnsureSuccessStatusCode();
                return await ReadAndParseAsync(response, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LauncherUpdateException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or IOException
                                   or JsonException
                                   or NotSupportedException)
        {
            throw new LauncherUpdateException(
                $"The release manifest could not be loaded: {ex.Message}",
                ex);
        }
    }

    internal static ReleaseManifest Parse(
        ReadOnlySpan<byte> utf8,
        bool allowLoopbackHttpArtifacts = false)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                utf8.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
            RejectDuplicateProperties(document.RootElement, "$" );
            ManifestDocument? value = document.RootElement.Deserialize<ManifestDocument>(
                SerializerOptions);
            return Validate(value, allowLoopbackHttpArtifacts);
        }
        catch (LauncherUpdateException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException
                                   or FormatException
                                   or InvalidOperationException)
        {
            throw new LauncherUpdateException(
                $"The release manifest is invalid: {ex.Message}",
                ex);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    internal static void RequireTransport(
        Uri uri,
        string description,
        bool allowLoopbackHttp)
    {
        if (!uri.IsAbsoluteUri
            || (uri.Scheme != Uri.UriSchemeHttps
                && !(allowLoopbackHttp
                    && uri.Scheme == Uri.UriSchemeHttp
                    && uri.IsLoopback)))
        {
            throw new LauncherUpdateException(
                $"The {description} URI must use HTTPS"
                + (allowLoopbackHttp ? " (or fixture-only loopback HTTP)." : "."));
        }
    }

    internal static void RequireSecureOrLoopback(Uri uri, string description) =>
        RequireTransport(uri, description, allowLoopbackHttp: true);

    private async Task<ReleaseManifest> ReadAndParseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength > MaximumManifestBytes)
        {
            throw new LauncherUpdateException(
                $"The release manifest is larger than {MaximumManifestBytes} bytes.");
        }

        await using Stream input = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumManifestBytes)
            {
                throw new LauncherUpdateException(
                    $"The release manifest is larger than {MaximumManifestBytes} bytes.");
            }

            output.Write(buffer, 0, read);
        }

        return Parse(output.ToArray(), _allowLoopbackHttp);
    }

    private static ReleaseManifest Validate(
        ManifestDocument? document,
        bool allowLoopbackHttpArtifacts)
    {
        if (document is null)
        {
            throw new LauncherUpdateException("The release manifest is empty.");
        }

        if (document.SchemaVersion != ReleaseManifest.CurrentSchemaVersion)
        {
            throw new LauncherUpdateException(
                $"Release manifest schema version {document.SchemaVersion} is not supported.");
        }

        LauncherVersion version = LauncherVersion.Parse(
            document.Version
            ?? throw new LauncherUpdateException("The release version is missing."));
        LauncherVersion minimum = LauncherVersion.Parse(
            document.MinimumLauncherVersion
            ?? throw new LauncherUpdateException(
                "The minimum launcher version is missing."));
        if (minimum > version)
        {
            throw new LauncherUpdateException(
                "The minimum launcher version cannot exceed the release version.");
        }

        IReadOnlyDictionary<string, ReleaseArtifact> clients = ValidateArtifacts(
            document.Clients,
            "clients",
            allowLoopbackHttpArtifacts);
        IReadOnlyDictionary<string, ReleaseArtifact> launchers = ValidateArtifacts(
            document.Launchers,
            "launchers",
            allowLoopbackHttpArtifacts);
        return new ReleaseManifest(version, minimum, clients, launchers);
    }

    private static IReadOnlyDictionary<string, ReleaseArtifact> ValidateArtifacts(
        Dictionary<string, ArtifactDocument>? artifacts,
        string field,
        bool allowLoopbackHttpArtifacts)
    {
        if (artifacts is null || artifacts.Count == 0)
        {
            throw new LauncherUpdateException($"Manifest field '{field}' must not be empty.");
        }

        var result = new Dictionary<string, ReleaseArtifact>(StringComparer.Ordinal);
        foreach ((string rid, ArtifactDocument value) in artifacts)
        {
            if (!LauncherRuntimeIdentity.IsValidRid(rid))
            {
                throw new LauncherUpdateException(
                    $"Manifest field '{field}' contains invalid RID '{rid}'.");
            }

            if (value is null)
            {
                throw new LauncherUpdateException(
                    $"Manifest payload '{field}.{rid}' is null.");
            }

            if (!Uri.TryCreate(value.Url, UriKind.Absolute, out Uri? uri))
            {
                throw new LauncherUpdateException(
                    $"Manifest payload '{field}.{rid}' has an invalid URL.");
            }

            RequireTransport(
                uri,
                $"{field}.{rid} artifact",
                allowLoopbackHttpArtifacts);
            if (!IsSha256(value.Sha256))
            {
                throw new LauncherUpdateException(
                    $"Manifest payload '{field}.{rid}' has an invalid SHA-256 digest.");
            }

            if (value.Size <= 0 || value.Size > MaximumArtifactBytes)
            {
                throw new LauncherUpdateException(
                    $"Manifest payload '{field}.{rid}' has an invalid size.");
            }

            result.Add(
                rid,
                new ReleaseArtifact(uri, value.Sha256!.ToLowerInvariant(), value.Size));
        }

        return result;
    }

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private static HttpMessageHandler CreateRedirectDisabledHandler() =>
        new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        };

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new LauncherUpdateException(
                        $"Duplicate JSON property '{path}.{property.Name}' is not allowed.");
                }

                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index++}]");
            }
        }
    }

    private sealed class ManifestDocument
    {
        public int SchemaVersion { get; init; }

        public string? Version { get; init; }

        public string? MinimumLauncherVersion { get; init; }

        public Dictionary<string, ArtifactDocument>? Clients { get; init; }

        public Dictionary<string, ArtifactDocument>? Launchers { get; init; }
    }

    private sealed class ArtifactDocument
    {
        public string? Url { get; init; }

        public string? Sha256 { get; init; }

        public long Size { get; init; }
    }
}
