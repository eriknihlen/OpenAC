using System.Text.Json;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Tests;

public sealed class LauncherUpdateCompositionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-launcher-composition-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("io")]
    [InlineData("permission")]
    [InlineData("corrupt")]
    public async Task StartupStorageFailureComposesUnavailableUpdaterWithoutThrowing(
        string failure)
    {
        Directory.CreateDirectory(_root);
        var paths = new ApplicationPathSet(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            null);
        Exception exception = failure switch
        {
            "io" => new IOException("storage offline"),
            "permission" => new UnauthorizedAccessException("storage denied"),
            "corrupt" => new JsonException("pointer corrupt"),
            _ => throw new InvalidOperationException("Unknown fixture failure."),
        };

        using LauncherUpdateComposition composition = LauncherUpdateComposition.Create(
            paths,
            LauncherRuntimeIdentity.DetectRid(),
            LauncherVersion.Parse("1.0.0"),
            _root,
            () => false,
            (_, _) => throw exception);

        Assert.Equal(ClientVersionState.Invalid, composition.Updater.CurrentClient.State);
        Assert.Contains(
            exception.Message,
            composition.Updater.CurrentClient.Status,
            StringComparison.Ordinal);
        LauncherCapability capability = composition.Executables.GetAvailability(LaunchMode.Gui);
        Assert.False(capability.IsAvailable);
        Assert.Contains(exception.Message, capability.Reason, StringComparison.Ordinal);
        LauncherUpdateException updateError = await Assert.ThrowsAsync<LauncherUpdateException>(
            () => composition.Updater.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Contains(exception.Message, updateError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://updates.example.test/manifest.json")]
    [InlineData("http://127.0.0.1:43119/manifest.json")]
    public void ProcessLocalManifestOverrideReachesOnlyUpdateComposition(string value)
    {
        Directory.CreateDirectory(_root);
        var paths = new ApplicationPathSet(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            null);
        var manifestUri = new Uri(value);

        using LauncherUpdateComposition composition = LauncherUpdateComposition.Create(
            paths,
            LauncherRuntimeIdentity.DetectRid(),
            LauncherVersion.Parse("1.0.0"),
            _root,
            () => false,
            initialize: (_, _) => new ClientVersionResolution(
                ClientVersionState.Missing,
                "No client version is installed.",
                null,
                null,
                null,
                null),
            updateManifestUri: manifestUri);

        Assert.Same(manifestUri, composition.UpdateManifestUri);
        Assert.Equal(
            Path.Combine(paths.DataDirectory, "app"),
            composition.Versions.AppDirectory);
        Assert.False(File.Exists(
            Path.Combine(paths.ConfigDirectory, "launcher-profiles.json")));
        Assert.Empty(Directory.EnumerateFiles(
            _root,
            "*",
            SearchOption.AllDirectories));
    }
}
