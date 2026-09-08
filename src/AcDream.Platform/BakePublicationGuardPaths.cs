namespace AcDream.Platform;

public static class BakePublicationGuardPaths
{
    public const string NonceEnvironmentVariable =
        "ACDREAM_BAKE_PUBLISH_NONCE_V1";
    public const string PublishLockSuffix = ".publish.lock";
    public const string AuthorizationSuffix = ".publish-token";

    public static string CreateNonce() => Guid.NewGuid().ToString("N");

    public static bool IsValidNonce(string? nonce) =>
        nonce is not null
        && nonce.Length == 32
        && Guid.TryParseExact(nonce, "N", out Guid parsed)
        && string.Equals(parsed.ToString("N"), nonce, StringComparison.Ordinal);

    public static string GetPublishLockPath(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        return Path.GetFullPath(outputPath) + PublishLockSuffix;
    }

    public static string GetAuthorizationPath(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        return Path.GetFullPath(outputPath) + AuthorizationSuffix;
    }
}
