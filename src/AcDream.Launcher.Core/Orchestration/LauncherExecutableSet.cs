using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Orchestration;

public sealed class LauncherExecutableSet
{
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _hasUnixExecutePermission;
    private readonly Func<ExecutablePaths> _resolve;

    public LauncherExecutableSet(
        string graphicalHostPath,
        string headlessHostPath,
        string? workingDirectory = null,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? hasUnixExecutePermission = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphicalHostPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(headlessHostPath);
        string graphical = graphicalHostPath;
        string headless = headlessHostPath;
        _resolve = () => new ExecutablePaths(graphical, headless, workingDirectory);
        _fileExists = fileExists ?? File.Exists;
        _hasUnixExecutePermission =
            hasUnixExecutePermission ?? HasUnixExecutePermission;
    }

    private LauncherExecutableSet(
        Func<ExecutablePaths> resolve,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? hasUnixExecutePermission = null)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _fileExists = fileExists ?? File.Exists;
        _hasUnixExecutePermission =
            hasUnixExecutePermission ?? HasUnixExecutePermission;
    }

    public string GraphicalHostPath => _resolve().GraphicalHostPath;

    public string HeadlessHostPath => _resolve().HeadlessHostPath;

    public string? WorkingDirectory => _resolve().WorkingDirectory;

    public LauncherCapability GetAvailability(LaunchMode mode)
    {
        ExecutablePaths paths;
        try
        {
            paths = _resolve();
        }
        catch (Exception ex) when (ex is LauncherUpdateException
                                   or InvalidOperationException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            return LauncherCapability.Unavailable(
                $"The active versioned client is unavailable: {ex.Message}");
        }

        string path = mode == LaunchMode.Headless
            ? paths.HeadlessHostPath
            : paths.GraphicalHostPath;
        string host = mode == LaunchMode.Headless
            ? "headless host"
            : "graphical client";
        if (!_fileExists(path))
        {
            return LauncherCapability.Unavailable(
                $"The co-deployed {host} is missing at '{path}'. Reinstall or update "
                + "the client before launching.");
        }

        if (OperatingSystem.IsLinux() && !_hasUnixExecutePermission(path))
        {
            return LauncherCapability.Unavailable(
                $"The co-deployed {host} at '{path}' exists but is not executable. "
                + "Restore its executable permission (for example, chmod +x) or "
                + "reinstall/update the client before launching.");
        }

        return LauncherCapability.Available;
    }

    public LauncherProcessSpec CreatePlaySpec(
        LaunchMode mode,
        string configFilePath,
        string? stderrLogPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);
        ExecutablePaths paths = RequireAvailable(mode);

        return mode == LaunchMode.Headless
            ? new LauncherProcessSpec(
                paths.HeadlessHostPath,
                ["run", "--config", configFilePath],
                paths.WorkingDirectory,
                StderrLogPath: stderrLogPath)
            : new LauncherProcessSpec(
                paths.GraphicalHostPath,
                ["--session-config", configFilePath],
                paths.WorkingDirectory,
                SupportsConsoleGracefulStop: false,
                StderrLogPath: stderrLogPath);
    }

    public LauncherProcessSpec CreateProbeSpec(
        string configFilePath,
        string? stderrLogPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);
        ExecutablePaths paths = RequireAvailable(LaunchMode.Headless);
        return new LauncherProcessSpec(
            paths.HeadlessHostPath,
            ["run", "--config", configFilePath],
            paths.WorkingDirectory,
            StderrLogPath: stderrLogPath);
    }

    public static LauncherExecutableSet FromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string fullDirectory = Path.GetFullPath(directory);
        string executableSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return new LauncherExecutableSet(
            Path.Combine(fullDirectory, "AcDream.App" + executableSuffix),
            Path.Combine(fullDirectory, "acdream-headless" + executableSuffix),
            fullDirectory);
    }

    public static LauncherExecutableSet FromCurrentVersionStore(
        ClientVersionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new LauncherExecutableSet(() =>
        {
            ClientVersionResolution resolution = store.CachedResolution;
            if (!resolution.IsVerified || resolution.Directory is null)
            {
                throw new LauncherUpdateException(resolution.Status);
            }

            return FromDirectoryPaths(resolution.Directory);
        });
    }

    public static LauncherExecutableSet Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new LauncherExecutableSet(
            () => throw new LauncherUpdateException(reason));
    }

    private ExecutablePaths RequireAvailable(LaunchMode mode)
    {
        LauncherCapability capability = GetAvailability(mode);
        if (!capability.IsAvailable)
        {
            throw new LauncherOperationException(
                capability.Reason ?? "The selected launcher host is unavailable.");
        }

        return _resolve();
    }

    private static ExecutablePaths FromDirectoryPaths(string directory)
    {
        string fullDirectory = Path.GetFullPath(directory);
        string executableSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return new ExecutablePaths(
            Path.Combine(fullDirectory, "AcDream.App" + executableSuffix),
            Path.Combine(fullDirectory, "acdream-headless" + executableSuffix),
            fullDirectory);
    }

    private static bool HasUnixExecutePermission(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        try
        {
            const UnixFileMode executeBits =
                UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & executeBits) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record ExecutablePaths(
        string GraphicalHostPath,
        string HeadlessHostPath,
        string? WorkingDirectory);
}
