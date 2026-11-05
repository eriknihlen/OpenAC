using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.ViewModels;

/// <summary>
/// This client is retired: the Rust client replaces it. The window says so at all times, and the
/// launcher stops reading the release feed, whose releases will soon be the Rust client's.
/// </summary>
public static class LauncherRetirement
{
    /// <summary>The documentation site's page for the Rust client, with its downloads.</summary>
    public static Uri RustClientPage { get; } =
        new("https://eriknihlen.github.io/OpenAC/docs/rust-client.html");

    public const string NoticeTitle = "OpenAC (C#) is retired.";

    public static string NoticeText { get; } =
        $"The Rust client, OpenAC for Rust, replaces it: download it from {RustClientPage}.";

    public const string GetRustClientLabel = "Get the Rust client";

    /// <summary>What the update check says instead of offering or failing.</summary>
    public const string NoUpdatesStatus = "This client is retired; it gets no more updates.";
}

/// <summary>
/// The updater of a retired launcher. It still recovers and verifies the installed client and can
/// roll it back, all on this machine, but it never reads the release feed: a check, an install or a
/// launcher update refuses before any network request.
/// </summary>
internal sealed class RetiredLauncherUpdater : ILauncherUpdater
{
    private readonly ILauncherUpdater _installed;

    public RetiredLauncherUpdater(ILauncherUpdater installed) =>
        _installed = installed ?? throw new ArgumentNullException(nameof(installed));

    public ClientVersionResolution CurrentClient => _installed.CurrentClient;

    public Task<ClientVersionResolution> InitializeAsync(
        CancellationToken cancellationToken = default) =>
        _installed.InitializeAsync(cancellationToken);

    public Task<LauncherUpdateCheckResult> CheckAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromException<LauncherUpdateCheckResult>(Retired());

    public Task<ClientVersionResolution> InstallClientAsync(
        LauncherUpdateCheckResult check,
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ClientVersionResolution>(Retired());

    public Task<SelfUpdateStageResult> StageLauncherAsync(
        LauncherUpdateCheckResult check,
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SelfUpdateStageResult>(Retired());

    public Task<ClientVersionResolution> RollbackClientAsync(
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _installed.RollbackClientAsync(progress, cancellationToken);

    private static LauncherUpdateException Retired() =>
        new(LauncherRetirement.NoUpdatesStatus);
}
