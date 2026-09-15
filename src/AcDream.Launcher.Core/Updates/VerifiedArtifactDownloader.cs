using System.Buffers;
using System.Net;
using System.Security.Cryptography;

namespace AcDream.Launcher.Core.Updates;

public sealed record ArtifactDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Percent => TotalBytes <= 0
        ? 0
        : Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100);
}

public sealed record VerifiedArtifactDownload(
    string FilePath,
    long Size,
    string Sha256);

public sealed class VerifiedArtifactDownloader
{
    private const int BufferSize = 128 * 1024;
    private readonly HttpClient _httpClient;

    public VerifiedArtifactDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<VerifiedArtifactDownload> DownloadAsync(
        ReleaseArtifact artifact,
        string destinationPath,
        IProgress<ArtifactDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ReleaseManifestClient.RequireSecureOrLoopback(artifact.Url, "artifact");
        if (artifact.Size <= 0
            || artifact.Size > ReleaseManifestClient.MaximumArtifactBytes
            || !ReleaseManifestClient.IsSha256(artifact.Sha256))
        {
            throw new LauncherUpdateException("The requested artifact metadata is invalid.");
        }

        return await DownloadCoreAsync(
                artifact.Url,
                artifact.Sha256,
                maximumBytes: artifact.Size,
                exactSize: artifact.Size,
                destinationPath,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Downloads an artifact whose exact size isn't known up front, rejecting anything past
    /// <paramref name="maximumBytes"/>. The SHA-256 check is the integrity guarantee (L-307).</summary>
    public async Task<VerifiedArtifactDownload> DownloadAsync(
        Uri url,
        string sha256,
        long maximumBytes,
        string destinationPath,
        IProgress<ArtifactDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ReleaseManifestClient.RequireSecureOrLoopback(url, "artifact");
        if (maximumBytes <= 0
            || maximumBytes > ReleaseManifestClient.MaximumArtifactBytes
            || !ReleaseManifestClient.IsSha256(sha256))
        {
            throw new LauncherUpdateException("The requested artifact metadata is invalid.");
        }

        return await DownloadCoreAsync(
                url,
                sha256,
                maximumBytes,
                exactSize: null,
                destinationPath,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The stream/hash/write/cleanup core both download modes share: an exact
    /// <paramref name="exactSize"/> enforces itself as a hard byte count and a mismatched
    /// <c>Content-Length</c> header fails fast; its absence falls back to
    /// <paramref name="maximumBytes"/> as a ceiling only, the L-307 mode the plugin client
    /// uses.</summary>
    private async Task<VerifiedArtifactDownload> DownloadCoreAsync(
        Uri url,
        string sha256,
        long maximumBytes,
        long? exactSize,
        string destinationPath,
        IProgress<ArtifactDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                "The artifact staging path has no parent directory."));

        bool ownsDestination = false;
        try
        {
            using HttpResponseMessage response = await SendWithValidatedRedirectsAsync(
                    _httpClient,
                    url,
                    "release artifact",
                    onRedirect: null,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long? declaredLength = response.Content.Headers.ContentLength;
            if (exactSize is long expectedSize)
            {
                if (declaredLength is long length && length != expectedSize)
                {
                    throw new LauncherUpdateException(
                        $"Artifact size header mismatch: expected {expectedSize}, "
                        + $"received {length}.");
                }
            }
            else if (declaredLength is long length && length > maximumBytes)
            {
                throw new LauncherUpdateException(
                    $"Artifact size header {length} exceeds the maximum of "
                    + $"{maximumBytes} bytes.");
            }

            if (response.Content.Headers.ContentEncoding.Count != 0)
            {
                throw new LauncherUpdateException(
                    "Release artifact content encoding is not allowed.");
            }

            long progressTotal = exactSize ?? declaredLength ?? maximumBytes;
            long limit = exactSize ?? maximumBytes;
            await using Stream input = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous
                | FileOptions.SequentialScan
                | FileOptions.WriteThrough);
            ownsDestination = true;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            long received = 0;
            try
            {
                progress?.Report(new ArtifactDownloadProgress(0, progressTotal));
                while (true)
                {
                    int read = await input.ReadAsync(
                            buffer.AsMemory(0, BufferSize),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    received = checked(received + read);
                    if (received > limit)
                    {
                        throw new LauncherUpdateException(exactSize is long declared
                            ? $"Artifact exceeded its declared size of {declared} bytes."
                            : $"Artifact exceeded the maximum of {maximumBytes} bytes.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken)
                        .ConfigureAwait(false);
                    progress?.Report(new ArtifactDownloadProgress(received, progressTotal));
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            if (exactSize is long finalExpectedSize && received != finalExpectedSize)
            {
                throw new LauncherUpdateException(
                    $"Artifact ended at {received} bytes; expected {finalExpectedSize}.");
            }

            string actualSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actualSha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new LauncherUpdateException(exactSize is not null
                    ? "Artifact SHA-256 does not match the release manifest."
                    : "Artifact SHA-256 does not match the expected value.");
            }

            return new VerifiedArtifactDownload(fullPath, received, actualSha256);
        }
        catch (OperationCanceledException)
        {
            if (ownsDestination)
            {
                TryDelete(fullPath);
            }

            throw;
        }
        catch (LauncherUpdateException)
        {
            if (ownsDestination)
            {
                TryDelete(fullPath);
            }

            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or CryptographicException)
        {
            if (ownsDestination)
            {
                TryDelete(fullPath);
            }

            throw new LauncherUpdateException(
                $"The release artifact could not be downloaded: {ex.Message}",
                ex);
        }
    }

    /// <summary>Follows redirects with the same transport checks the manifest client uses, shared by
    /// both download methods and the plugin release client.</summary>
    internal static async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
        HttpClient httpClient,
        Uri initialUri,
        string subject,
        Action<Uri>? onRedirect,
        CancellationToken cancellationToken)
    {
        bool allowLoopbackHttp = initialUri.Scheme == Uri.UriSchemeHttp
            && initialUri.IsLoopback;
        Uri current = initialUri;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int redirectCount = 0;;)
        {
            ReleaseManifestClient.RequireTransport(
                current,
                $"{subject} redirect",
                allowLoopbackHttp);
            if (!visited.Add(current.AbsoluteUri))
            {
                throw new LauncherUpdateException(
                    $"The {subject} redirect chain contains a loop.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            Uri effectiveUri = response.RequestMessage?.RequestUri ?? current;
            if (!Uri.Equals(effectiveUri, current))
            {
                response.Dispose();
                throw new LauncherUpdateException(
                    $"The {subject} HTTP transport followed an automatic redirect; "
                    + "every redirect must be validated before it is requested.");
            }

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            try
            {
                if (redirectCount >= ReleaseManifestClient.MaximumRedirects)
                {
                    throw new LauncherUpdateException(
                        $"The {subject} exceeded "
                        + $"{ReleaseManifestClient.MaximumRedirects} redirects.");
                }

                Uri? location = response.Headers.Location;
                if (location is null)
                {
                    throw new LauncherUpdateException(
                        $"The {subject} redirect has no Location header.");
                }

                Uri next = location.IsAbsoluteUri
                    ? location
                    : new Uri(current, location);
                ReleaseManifestClient.RequireTransport(
                    next,
                    $"{subject} redirect",
                    allowLoopbackHttp);
                onRedirect?.Invoke(next);
                current = next;
                redirectCount++;
            }
            finally
            {
                response.Dispose();
            }
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The exact random staging name is reclaimed by startup recovery.
        }
    }
}
