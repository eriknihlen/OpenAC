namespace AcDream.Launcher.Core.Updates;

public enum LauncherInstallationKind
{
    Flat,
    MacBundle,
}

public sealed record LauncherInstallationLayout(
    LauncherInstallationKind Kind,
    string InstalledRoot,
    string PayloadRoot,
    string LauncherRelativePath)
{
    public string ContainerDirectory => Kind == LauncherInstallationKind.MacBundle
        ? Path.GetDirectoryName(InstalledRoot)
            ?? throw new LauncherUpdateException("The app bundle has no containing directory.")
        : InstalledRoot;

    public string LauncherPath => ClientVersionStore.ResolveContained(
        InstalledRoot,
        LauncherRelativePath);

    public string PayloadLauncherPath => Path.Combine(
        PayloadRoot,
        LauncherRelativePath).Replace('\\', '/');

    public static LauncherInstallationLayout Flat(string installedRoot, string rid) =>
        new(
            LauncherInstallationKind.Flat,
            NormalizeDirectory(installedRoot),
            string.Empty,
            PayloadExecutableNames.Launcher + PayloadExecutableNames.SuffixForRid(rid));

    public static LauncherInstallationLayout MacBundle(string installedRoot, string rid)
    {
        string root = NormalizeDirectory(installedRoot);
        if (!string.Equals(
                Path.GetFileName(root),
                "OpenAC.app",
                StringComparison.Ordinal))
        {
            throw new LauncherUpdateException(
                "The macOS launcher must run from OpenAC.app.");
        }

        return new LauncherInstallationLayout(
            LauncherInstallationKind.MacBundle,
            root,
            "OpenAC.app",
            Path.Combine(
                "Contents",
                "MacOS",
                PayloadExecutableNames.Launcher + PayloadExecutableNames.SuffixForRid(rid))
                .Replace('\\', '/'));
    }

    public static LauncherInstallationLayout Detect(string appBaseDirectory, string rid)
    {
        string baseDirectory = NormalizeDirectory(appBaseDirectory);
        if (!OperatingSystem.IsMacOS())
        {
            return Flat(baseDirectory, rid);
        }

        DirectoryInfo? current = new(baseDirectory);
        if (!string.Equals(current.Name, "MacOS", StringComparison.Ordinal)
            || !string.Equals(current.Parent?.Name, "Contents", StringComparison.Ordinal)
            || !string.Equals(current.Parent?.Parent?.Name, "OpenAC.app", StringComparison.Ordinal))
        {
            throw new LauncherUpdateException(
                "The macOS launcher must run from OpenAC.app/Contents/MacOS.");
        }

        return MacBundle(current.Parent?.Parent?.FullName
            ?? throw new LauncherUpdateException("The macOS app bundle path is incomplete."), rid);
    }

    internal string ResolveInstalledPayloadFile(string payloadPath)
    {
        string relative = payloadPath;
        if (Kind == LauncherInstallationKind.MacBundle)
        {
            string prefix = PayloadRoot + "/";
            if (!relative.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new LauncherUpdateException(
                    "The macOS update payload escapes the app bundle root.");
            }

            relative = relative[prefix.Length..];
        }

        return ClientVersionStore.ResolveContained(InstalledRoot, relative);
    }

    internal string GetSiblingTransactionDirectory(string transactionId) =>
        Path.Combine(ContainerDirectory, ".acdream-self-update-" + transactionId);

    private static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new LauncherUpdateException("The launcher installation directory must be absolute.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
