namespace AcDream.Launcher.Core.Updates;

public sealed record ReleaseArtifact(Uri Url, string Sha256, long Size);

/// <summary>The fingerprint of a release's launcher: one SHA-256 over what the launcher is built
/// from, published beside the manifest. It changes only when the launcher does, so a launcher
/// carrying the same fingerprint is that release's launcher whatever its version says.</summary>
public sealed record LauncherFingerprintDocument(LauncherVersion Version, string Fingerprint)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The file's name beside manifest.json in a release.</summary>
    public const string FileName = "launcher-fingerprint.json";
}

public sealed record ReleaseManifest(
    LauncherVersion Version,
    LauncherVersion MinimumLauncherVersion,
    IReadOnlyDictionary<string, ReleaseArtifact> Clients,
    IReadOnlyDictionary<string, ReleaseArtifact> Launchers)
{
    public const int CurrentSchemaVersion = 1;

    public ReleaseArtifact RequireClient(string rid) =>
        Clients.TryGetValue(rid, out ReleaseArtifact? artifact)
            ? artifact
            : throw new LauncherUpdateException(
                $"Release {Version} has no client payload for RID '{rid}'.");

    public ReleaseArtifact RequireLauncher(string rid) =>
        Launchers.TryGetValue(rid, out ReleaseArtifact? artifact)
            ? artifact
            : throw new LauncherUpdateException(
                $"Release {Version} has no launcher payload for RID '{rid}'.");
}

public sealed class LauncherUpdateException : Exception
{
    public LauncherUpdateException(string message)
        : base(message)
    {
    }

    public LauncherUpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
