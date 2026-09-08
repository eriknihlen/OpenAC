using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher;

internal enum LauncherStartupMode
{
    Desktop,
    VerifyPublish,
    SelfUpdateHelper,
    SelfUpdateConfirmation,
}

internal sealed class LauncherStartupOptions
{
    private readonly IReadOnlyList<string> _publicArguments;

    private LauncherStartupOptions(
        LauncherStartupMode mode,
        ApplicationPathSet paths,
        Uri updateManifestUri,
        IReadOnlyList<string> publicArguments)
    {
        Mode = mode;
        Paths = paths;
        UpdateManifestUri = updateManifestUri;
        _publicArguments = Array.AsReadOnly(publicArguments.ToArray());
    }

    internal LauncherStartupMode Mode { get; }

    internal ApplicationPathSet Paths { get; }

    internal Uri UpdateManifestUri { get; }

    internal IReadOnlyList<string> PublicArguments => _publicArguments;

    internal static LauncherStartupOptions Parse(
        IReadOnlyList<string> arguments,
        Func<ApplicationPathSet>? resolveDefaultPaths = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        resolveDefaultPaths ??= () => ApplicationPathSet.Resolve();

        (LauncherStartupMode mode, int publicStart) = ReadMode(arguments);
        string[] publicArguments = arguments.Skip(publicStart).ToArray();

        if (publicArguments.Contains("--verify-publish", StringComparer.Ordinal))
        {
            if (mode != LauncherStartupMode.Desktop
                || publicArguments.Length != 1
                || !string.Equals(
                    publicArguments[0],
                    "--verify-publish",
                    StringComparison.Ordinal))
            {
                throw new LauncherStartupOptionsException(
                    "--verify-publish must be the only launcher argument.");
            }

            return new LauncherStartupOptions(
                LauncherStartupMode.VerifyPublish,
                // The publish probe returns before this value is observed. A
                // non-resolving sentinel keeps the probe display- and
                // user-profile-free even under a deliberately broken runtime.
                new ApplicationPathSet(string.Empty, string.Empty, string.Empty, null),
                ReleaseManifestClient.ProductionManifestUri,
                publicArguments);
        }

        string? configDirectory = null;
        string? dataDirectory = null;
        string? cacheDirectory = null;
        Uri? updateManifestUri = null;

        for (int index = 0; index < publicArguments.Length; index += 2)
        {
            string name = publicArguments[index];
            if (index + 1 >= publicArguments.Length
                || publicArguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new LauncherStartupOptionsException(
                    $"Launcher option '{name}' requires a value.");
            }

            string value = publicArguments[index + 1];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new LauncherStartupOptionsException(
                    $"Launcher option '{name}' requires a non-empty value.");
            }

            switch (name)
            {
                case "--config-dir":
                    SetDirectoryOnce(ref configDirectory, value, name);
                    break;
                case "--data-dir":
                    SetDirectoryOnce(ref dataDirectory, value, name);
                    break;
                case "--cache-dir":
                    SetDirectoryOnce(ref cacheDirectory, value, name);
                    break;
                case "--update-manifest-uri":
                    if (updateManifestUri is not null)
                    {
                        throw new LauncherStartupOptionsException(
                            "Launcher options cannot be repeated.");
                    }

                    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed))
                    {
                        throw new LauncherStartupOptionsException(
                            "--update-manifest-uri must be an absolute URI.");
                    }

                    if (parsed.Scheme != Uri.UriSchemeHttps
                        && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback))
                    {
                        throw new LauncherStartupOptionsException(
                            "The update manifest URI must use HTTPS "
                            + "(loopback HTTP is test-only).");
                    }

                    if (!string.IsNullOrEmpty(parsed.UserInfo))
                    {
                        throw new LauncherStartupOptionsException(
                            "The update manifest URI cannot contain user information.");
                    }

                    if (!string.IsNullOrEmpty(parsed.Query)
                        || !string.IsNullOrEmpty(parsed.Fragment))
                    {
                        throw new LauncherStartupOptionsException(
                            "The update manifest URI cannot contain a query or fragment.");
                    }

                    updateManifestUri = parsed;
                    break;
                default:
                    throw new LauncherStartupOptionsException(
                        $"Unknown launcher option '{name}'.");
            }
        }

        int suppliedRoots = new[] { configDirectory, dataDirectory, cacheDirectory }
            .Count(path => path is not null);
        if (suppliedRoots is > 0 and < 3)
        {
            throw new LauncherStartupOptionsException(
                "--config-dir, --data-dir, and --cache-dir must be supplied together.");
        }

        ApplicationPathSet paths = suppliedRoots == 3
            ? new ApplicationPathSet(
                configDirectory!,
                dataDirectory!,
                cacheDirectory!,
                LegacyConfigDirectory: null)
            : resolveDefaultPaths();
        return new LauncherStartupOptions(
            mode,
            paths,
            updateManifestUri ?? ReleaseManifestClient.ProductionManifestUri,
            publicArguments);
    }

    private static (LauncherStartupMode Mode, int PublicStart) ReadMode(
        IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return (LauncherStartupMode.Desktop, 0);
        }

        if (string.Equals(
                arguments[0],
                LauncherSelfUpdateBootstrap.HelperArgument,
                StringComparison.Ordinal))
        {
            // Malformed internal invocations are rejected by the bootstrap
            // with EX_USAGE. Do not reinterpret their operands as public
            // options while resolving the manager they need to report that.
            return (
                LauncherStartupMode.SelfUpdateHelper,
                arguments.Count >= 4 ? 4 : arguments.Count);
        }

        if (string.Equals(
                arguments[0],
                LauncherSelfUpdateBootstrap.ConfirmArgument,
                StringComparison.Ordinal))
        {
            return (
                LauncherStartupMode.SelfUpdateConfirmation,
                arguments.Count >= 2 ? 2 : arguments.Count);
        }

        return (LauncherStartupMode.Desktop, 0);
    }

    private static void SetDirectoryOnce(
        ref string? destination,
        string value,
        string option)
    {
        if (destination is not null)
        {
            throw new LauncherStartupOptionsException(
                "Launcher options cannot be repeated.");
        }

        if (!Path.IsPathFullyQualified(value))
        {
            throw new LauncherStartupOptionsException(
                $"Launcher option '{option}' must be an absolute path.");
        }

        try
        {
            destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException)
        {
            throw new LauncherStartupOptionsException(
                $"Launcher option '{option}' is not a valid absolute path.",
                ex);
        }
    }
}

internal sealed class LauncherStartupOptionsException : Exception
{
    internal LauncherStartupOptionsException(string message)
        : base(message)
    {
    }

    internal LauncherStartupOptionsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
