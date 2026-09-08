using System.Reflection;
using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;
using AcDream.Launcher.ViewModels;
using AcDream.Platform;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AcDream.Launcher;

public sealed partial class App : Application
{
    private readonly LauncherStartupOptions? _startupOptions;
    private LauncherOrchestrator? _orchestrator;
    private LauncherWindowViewModel? _viewModel;
    private LauncherUpdateComposition? _updateComposition;

    public App()
    {
    }

    internal App(LauncherStartupOptions startupOptions)
    {
        _startupOptions = startupOptions
            ?? throw new ArgumentNullException(nameof(startupOptions));
    }

    internal LauncherStartupOptions StartupOptions => _startupOptions
        ?? throw new InvalidOperationException(
            "Launcher startup options were not supplied by the composition root.");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            LauncherStartupOptions startupOptions = StartupOptions;
            ApplicationPathSet paths = startupOptions.Paths;
            LauncherProfileStore profiles = LauncherProfileStore.ForApplicationPaths(paths);
            string rid = LauncherRuntimeIdentity.DetectRid();
            string executableSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            var installer = new LauncherInstaller(
                paths,
                Path.Combine(
                    AppContext.BaseDirectory,
                    "acdream-bake" + executableSuffix));

            LauncherUpdateComposition updates = LauncherUpdateComposition.Create(
                paths,
                rid,
                GetLauncherVersion(),
                AppContext.BaseDirectory,
                () => _orchestrator?.GetSnapshot().Sessions.Any(session => session.IsActive)
                    == true,
                updateManifestUri: startupOptions.UpdateManifestUri);
            _updateComposition = updates;

            _orchestrator = new LauncherOrchestrator(
                profiles,
                paths,
                updates.Executables,
                installRecord: null,
                installationStatus: "Checking installed game content…",
                updateSessionBarrier: updates.Versions.Barrier);
            LauncherSelfUpdateManager? selfUpdates = updates.SelfUpdates;
            Func<CancellationToken, Task<bool>>? applyLauncherUpdate =
                selfUpdates is null
                    ? null
                    : token => LauncherSelfUpdateBootstrap.TryApplyStagedUpdateNowAsync(
                        selfUpdates,
                        AppContext.BaseDirectory,
                        Environment.ProcessPath
                            ?? throw new InvalidOperationException(
                                "The launcher executable path is unavailable."),
                        startupOptions.PublicArguments,
                        token);

            _viewModel = new LauncherWindowViewModel(
                _orchestrator,
                new AvaloniaUiDispatcher(),
                installer,
                updates.Updater,
                applyLauncherUpdate,
                () => desktop.Shutdown());
            var mainWindow = new MainWindow
            {
                DataContext = _viewModel,
            };
            desktop.MainWindow = mainWindow;
            _viewModel.Initialize();
            mainWindow.Opened += OnMainWindowOpened;
            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Opened -= OnMainWindowOpened;
        }

        if (_viewModel is not null)
        {
            _ = _viewModel.StartBackgroundInitializationAsync();
        }
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _viewModel?.Dispose();
        _orchestrator?.Dispose();
        _updateComposition?.Dispose();
        _viewModel = null;
        _orchestrator = null;
        _updateComposition = null;
    }

    private static LauncherVersion GetLauncherVersion()
    {
        string? informationalVersion = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!LauncherVersion.TryParse(informationalVersion, out LauncherVersion? version))
        {
            throw new InvalidOperationException(
                $"Launcher informational version '{informationalVersion}' is not SemVer 2.0.");
        }

        return version;
    }
}
