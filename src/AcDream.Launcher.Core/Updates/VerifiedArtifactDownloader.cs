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

        string fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                "The artifact staging path has no parent directory."));

        bool ownsDestination = false;
        try
        {
            using HttpResponseMessage response = await SendWithValidatedRedirectsAsync(
                    artifact.Url,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength
                && contentLength != artifact.Size)
            {
                throw new LauncherUpdateException(
                    $"Artifact size header mismatch: expected {artifact.Size}, "
                    + $"received {contentLength}.");
            }

            if (response.Content.Headers.ContentEncoding.Count != 0)
            {
                throw new LauncherUpdateException(
                    "Release artifact content encoding is not allowed.");
            }

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
                progress?.Report(new ArtifactDownloadProgress(0, artifact.Size));
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
                    if (received > artifact.Size)
                    {
                        throw new LauncherUpdateException(
                            $"Artifact exceeded its declared size of {artifact.Size} bytes.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken)
                        .ConfigureAwait(false);
                    progress?.Report(new ArtifactDownloadProgress(received, artifact.Size));
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            if (received != artifact.Size)
            {
                throw new LauncherUpdateException(
                    $"Artifact ended at {received} bytes; expected {artifact.Size}.");
            }

            string actualSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(
                    actualSha256,
                    artifact.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new LauncherUpdateException(
                    "Artifact SHA-256 does not match the release manifest.");
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

    private async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
        Uri initialUri,
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
                "artifact redirect",
                allowLoopbackHttp);
            if (!visited.Add(current.AbsoluteUri))
            {
                throw new LauncherUpdateException(
                    "The release artifact redirect chain contains a loop.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            Uri effectiveUri = response.RequestMessage?.RequestUri ?? current;
            if (!Uri.Equals(effectiveUri, current))
            {
                response.Dispose();
                throw new LauncherUpdateException(
                    "The artifact HTTP transport followed an automatic redirect; "
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
                        $"The release artifact exceeded "
                        + $"{ReleaseManifestClient.MaximumRedirects} redirects.");
                }

                Uri? location = response.Headers.Location;
                if (location is null)
                {
                    throw new LauncherUpdateException(
                        "The release artifact redirect has no Location header.");
                }

                Uri next = location.IsAbsoluteUri
                    ? location
                    : new Uri(current, location);
                ReleaseManifestClient.RequireTransport(
                    next,
                    "artifact redirect",
                    allowLoopbackHttp);
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
