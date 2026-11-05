using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.ViewModels;

/// <summary>
/// This client is retired: the Rust client replaces it. The window says so at all times, and the
/// launcher no longer follows the latest release, which will soon be the Rust client's.
/// </summary>
public static class LauncherRetirement
{
    /// <summary>The documentation site's page for the Rust client, with its downloads.</summary>
    public static Uri RustClientPage { get; } =
        new("https://eriknihlen.github.io/OpenAC/docs/rust-client.html");

    /// <summary>The last release of this client: the only one a retired launcher installs.</summary>
    public static LauncherVersion LastRelease { get; } = LauncherVersion.Parse("0.1.33");

    public const string NoticeTitle = "OpenAC (C#) is retired.";

    public static string NoticeText { get; } =
        $"The Rust client, OpenAC for Rust, replaces it: download it from {RustClientPage}.";

    public const string GetRustClientLabel = "Get the Rust client";

    /// <summary>What the update check says once the last release's client is installed.</summary>
    public const string NoUpdatesStatus = "This client is retired; it gets no more updates.";
}

/// <summary>
/// The updater of a retired launcher. It reads only the last release's own manifest (see
/// <see cref="ReleaseManifestClient.RetiredManifestUri"/>), so it can still install that release's
/// client where none or an older one is installed, but it accepts no other version and never
/// replaces the launcher.
/// </summary>
internal sealed class RetiredLauncherUpdater : ILauncherUpdater
{
    private readonly ILauncherUpdater _lastRelease;

    /// <param name="lastRelease">An updater that reads the last release's manifest.</param>
    public RetiredLauncherUpdater(ILauncherUpdater lastRelease) =>
        _lastRelease = lastRelease ?? throw new ArgumentNullException(nameof(lastRelease));

    public ClientVersionResolution CurrentClient => _lastRelease.CurrentClient;

    public Task<ClientVersionResolution> InitializeAsync(
        CancellationToken cancellationToken = default) =>
        _lastRelease.InitializeAsync(cancellationToken);

    public async Task<LauncherUpdateCheckResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        LauncherUpdateCheckResult check = await _lastRelease.CheckAsync(cancellationToken)
            .ConfigureAwait(false);
        RequireLastRelease(check);
        return check with { IsLauncherUpdateAvailable = false };
    }

    public Task<ClientVersionResolution> InstallClientAsync(
        LauncherUpdateCheckResult check,
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        RequireLastRelease(check);
        return _lastRelease.InstallClientAsync(check, progress, cancellationToken);
    }

    public Task<SelfUpdateStageResult> StageLauncherAsync(
        LauncherUpdateCheckResult check,
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SelfUpdateStageResult>(
            new LauncherUpdateException(LauncherRetirement.NoUpdatesStatus));

    public Task<ClientVersionResolution> RollbackClientAsync(
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _lastRelease.RollbackClientAsync(progress, cancellationToken);

    private static void RequireLastRelease(LauncherUpdateCheckResult check)
    {
        if (!check.Manifest.Version.Equals(LauncherRetirement.LastRelease))
        {
            throw new LauncherUpdateException(
                $"The release manifest is for {check.Manifest.Version}, not for this client's "
                + $"last release, {LauncherRetirement.LastRelease}.");
        }
    }
}
