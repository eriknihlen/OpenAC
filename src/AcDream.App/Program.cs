using AcDream.App;
using AcDream.App.Configuration;
using AcDream.App.Credentials;
using AcDream.App.Plugins;
using AcDream.App.Platform;
using AcDream.App.Rendering;
using AcDream.Platform;
using Serilog;

GraphicalHostPlatformServices graphicalPlatform =
    GraphicalHostPlatformServices.Resolve();
graphicalPlatform.ConfigureWindowBackend();
ApplicationPathSet applicationPaths = graphicalPlatform.Paths;
IReadOnlyList<string> migratedConfigurationFiles =
    GraphicalLegacyConfigurationMigrator.Migrate(applicationPaths);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateLogger();
foreach (string migratedConfigurationFile in migratedConfigurationFiles)
{
    Log.Information(
        "migrated legacy graphical configuration to {Path}",
        migratedConfigurationFile);
}
Log.Information(
    "graphical platform {RuntimeIdentifier}; native closure: {NativeDependencies}",
    graphicalPlatform.RuntimeIdentifier,
    string.Join(
        ", ",
        graphicalPlatform.NativeDependencies.Select(
            dependency =>
                $"{dependency.Feature}={dependency.PublishedFileName}")));

string? sessionConfigFlagPath = SessionConfigArgumentParsing.ExtractFlagValue(
    args, "--session-config", out bool sessionConfigFlagPresent);
if (sessionConfigFlagPath is null && sessionConfigFlagPresent)
{
    Log.Error(
        "--session-config requires a value (a path to the session-config document).");
    return 2;
}
string[] positionalArgs =
    SessionConfigArgumentParsing.WithoutFlagAndValue(args, "--session-config");

var datDirArg = positionalArgs.FirstOrDefault();
var envDatDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");

RuntimeOptions runtimeOptions;
if (sessionConfigFlagPath is not null)
{
    SessionConfiguration sessionConfig;
    SessionDescriptor session;
    try
    {
        (sessionConfig, session) = SessionConfigurationLoader.Load(sessionConfigFlagPath);
    }
    catch (Exception error)
        when (error is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Text.Json.JsonException
            or SessionConfigurationException)
    {
        Log.Error("--session-config invalid: {Error}", error.Message);
        return 2;
    }

    string? resolvedDatDir =
        NullIfEmpty(sessionConfig.Process?.Content?.DatDirectory)
        ?? NullIfEmpty(datDirArg)
        ?? NullIfEmpty(envDatDir);
    if (resolvedDatDir is null)
    {
        Log.Error(
            "usage: AcDream.App <dat-directory>  (or set ACDREAM_DAT_DIR, "
            + "or supply process.content.datDirectory in --session-config)");
        return 2;
    }

    AppCredentialSecret? secret = null;
    try
    {
        var resolver = new AppCredentialResolver(
            Console.In,
            applicationPaths.ConfigDirectory,
            graphicalPlatform.OperatingSystem
                == GraphicalHostOperatingSystem.Linux);
        secret = resolver.Resolve(session.Id, session.Credential);
        runtimeOptions = RuntimeOptions.FromSessionConfig(
            resolvedDatDir,
            Environment.GetEnvironmentVariable,
            sessionConfigFlagPath,
            sessionConfig,
            session,
            secret.Reveal());
    }
    catch (AppCredentialException error)
    {
        Log.Error("--session-config credential unavailable: {Error}", error.Message);
        return 2;
    }
    finally
    {
        secret?.Dispose();
    }

    // Env-var flow untouched when the flag is absent; when both are present
    // the flag wins — this line makes that explicit rather than silent.
    Log.Information(
        "--session-config {Path} present; overriding ACDREAM_LIVE*/ACDREAM_TEST_* "
        + "env-var live-session settings",
        sessionConfigFlagPath);
}
else
{
    var datDir = datDirArg ?? envDatDir;
    if (string.IsNullOrWhiteSpace(datDir))
    {
        Log.Error("usage: AcDream.App <dat-directory>  (or set ACDREAM_DAT_DIR)");
        return 2;
    }
    runtimeOptions = RuntimeOptions.FromEnvironment(datDir);
}

if (runtimeOptions.DevTools)
{
    Log.Information(
        "ACDREAM_DEVTOOLS=1: enables " +
        "the optional Vulkan validation/debug-utils extensions.");
}

var worldGameState = new AcDream.Core.Plugins.WorldGameState();
var worldEvents = new AcDream.Core.Plugins.WorldEvents();
var uiRegistry = new AcDream.App.Plugins.BufferedUiRegistry();
using var renderPackRegistry = new AcDream.App.Plugins.BufferedRenderPackRegistry();
using IDisposable atmosphericPackRegistration = renderPackRegistry.Register(
    AcDream.App.Rendering.Packs.BuiltInAtmosphericRenderPack.Descriptor,
    AcDream.App.Rendering.Packs.BuiltInAtmosphericRenderPack.CreateAssets(
        Path.Combine(
            AppContext.BaseDirectory,
            "Rendering",
            "Shaders",
            "spv")));
using var automation = new AcDream.App.Plugins.AppAutomationSurface(
    worldEvents,
    new AcDream.App.Plugins.LocalPluginPeerRegistry(Path.Combine(
        applicationPaths.DataDirectory,
        "plugin-peers")),
    runtimeOptions.PluginTags);
var lootClassifiers = new AcDream.Core.Plugins.PluginLootClassifierRegistry();
using var window = new GameWindow(
    runtimeOptions,
    worldGameState,
    worldEvents,
    uiRegistry,
    graphicalPlatform,
    automation,
    renderPackRegistry);
var host = new AppPluginHost(
    new SerilogAdapter(Log.Logger),
    worldGameState,
    worldEvents,
    window.Selection,
    uiRegistry,
    automation,
    new FilePluginStorage(
        Path.Combine(applicationPaths.ConfigDirectory, "plugins")),
    automation.PluginCommands,
    lootClassifiers,
    new FilePluginStorage(
        runtimeOptions.VtankProfileDirectoryOverride
            ?? VtankProfilesDefault.Resolve(applicationPaths.DataDirectory)));
GraphicalPluginSession pluginSession = GraphicalPluginSession.Create(
    applicationPaths,
    runtimeOptions.Plugins,
    runtimeOptions.SessionId ?? "app",
    host,
    window.StatusWriter,
    renderPackRegistry);
window.StartPluginHosting(pluginSession);

try
{
    try
    {
        window.Run();
    }
    catch (NotSupportedException error)
    {
        Log.Error("{GraphicalStartupFailure}", error.Message);
        return 4;
    }
}
finally
{
    Log.CloseAndFlush();
}

return 0;

static string? NullIfEmpty(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value;
