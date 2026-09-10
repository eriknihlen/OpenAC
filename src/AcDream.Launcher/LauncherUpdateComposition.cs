using System.Net;
using System.Security;
using System.Text.Json;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Updates;
using AcDream.Launcher.ViewModels;
using AcDream.Platform;

namespace AcDream.Launcher;

internal sealed class LauncherUpdateComposition : IDisposable
{
    private readonly HttpClient? _artifactClient;
    private readonly ReleaseManifestClient? _manifestClient;

    private LauncherUpdateComposition(
        ClientVersionStore versions,
        LauncherExecutableSet executables,
        ILauncherUpdater updater,
        Uri updateManifestUri,
        HttpClient? artifactClient,
        ReleaseManifestClient? manifestClient,
        LauncherSelfUpdateManager? selfUpdates)
    {
        Versions = versions;
        Executables = executables;
        Updater = updater;
        UpdateManifestUri = updateManifestUri;
        _artifactClient = artifactClient;
        _manifestClient = manifestClient;
        SelfUpdates = selfUpdates;
    }

    public LauncherSelfUpdateManager? SelfUpdates { get; }

    public ClientVersionStore Versions { get; }

    public LauncherExecutableSet Executables { get; }

    public ILauncherUpdater Updater { get; }

    internal Uri UpdateManifestUri { get; }

    public static LauncherUpdateComposition Create(
        ApplicationPathSet paths,
        string rid,
        LauncherVersion launcherVersion,
        string launcherTargetDirectory,
        Func<bool> hasRunningSessions,
        Func<ClientVersionStore, string, ClientVersionResolution>? initialize = null,
        Uri? updateManifestUri = null,
        LauncherInstallationLayout? installationLayout = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(launcherVersion);
        ArgumentNullException.ThrowIfNull(hasRunningSessions);
        Uri manifestUri = updateManifestUri
            ?? ReleaseManifestClient.ProductionManifestUri;
        var versions = new ClientVersionStore(paths);
        HttpClient? artifactClient = null;
        ReleaseManifestClient? manifestClient = null;
        try
        {
            if (initialize is not null)
            {
                _ = initialize(versions, rid);
            }
            artifactClient = new HttpClient(
                new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    AutomaticDecompression = DecompressionMethods.None,
                },
                disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            artifactClient.DefaultRequestHeaders.UserAgent.ParseAdd("OpenAC-launcher/1");
            manifestClient = CreateManifestClient(manifestUri);
            var selfUpdates = new LauncherSelfUpdateManager(paths, artifactClient);
            var updater = new LauncherUpdater(
                manifestClient,
                artifactClient,
                versions,
                selfUpdates,
                launcherVersion,
                rid,
                installationLayout ?? LauncherInstallationLayout.Flat(launcherTargetDirectory, rid),
                hasRunningSessions);
            return new LauncherUpdateComposition(
                versions,
                LauncherExecutableSet.FromCurrentVersionStore(versions),
                updater,
                manifestUri,
                artifactClient,
                manifestClient,
                selfUpdates);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            manifestClient?.Dispose();
            artifactClient?.Dispose();
            string status = "Versioned client update storage is unavailable: "
                + (string.IsNullOrWhiteSpace(ex.Message)
                    ? "the storage operation failed."
                    : ex.Message);
            var resolution = new ClientVersionResolution(
                ClientVersionState.Invalid,
                status,
                null,
                null,
                null,
                null);
            return new LauncherUpdateComposition(
                versions,
                LauncherExecutableSet.Unavailable(status),
                new UnavailableLauncherUpdater(status, resolution),
                manifestUri,
                artifactClient: null,
                manifestClient: null,
                selfUpdates: null);
        }
    }

    public void Dispose()
    {
        _manifestClient?.Dispose();
        _artifactClient?.Dispose();
    }

    private static ReleaseManifestClient CreateManifestClient(Uri manifestUri)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        return manifestUri == ReleaseManifestClient.ProductionManifestUri
            ? new ReleaseManifestClient(TimeSpan.FromSeconds(15))
            : ReleaseManifestClient.CreateLocalUpdateFeedOverride(
                manifestUri,
                TimeSpan.FromSeconds(15));
    }

    private static bool IsStorageFailure(Exception exception) => exception is
        IOException
        or UnauthorizedAccessException
        or SecurityException
        or JsonException
        or FormatException
        or NotSupportedException
        or LauncherUpdateException;
}
