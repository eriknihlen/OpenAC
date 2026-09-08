namespace AcDream.Launcher.Core.Updates;

public sealed record ReleaseArtifact(Uri Url, string Sha256, long Size);

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
