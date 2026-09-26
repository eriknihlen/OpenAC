using System.Reflection;
using System.Net.Http;
using AcDream.Launcher.Core.Status;
using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;
using AcDream.Launcher.ViewModels;
using AcDream.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AcDream.Launcher;

public sealed partial class App : Application
{
    private readonly LauncherStartupOptions? _startupOptions;
    private LauncherOrchestrator? _orchestrator;
    private LauncherWindowViewModel? _viewModel;
    private LauncherUpdateComposition? _updateComposition;
    private LauncherPluginComposition? _pluginComposition;
    private readonly HttpClient _serverStatusClient = new();

    public App()
    {
    }

    internal App(LauncherStartupOptions startupOptions, string? refusal = null)
    {
        _startupOptions = startupOptions
            ?? throw new ArgumentNullException(nameof(startupOptions));
        _refusal = refusal;
    }

    private readonly string? _refusal;

    internal LauncherStartupOptions StartupOptions => _startupOptions
        ?? throw new InvalidOperationException(
            "Launcher startup options were not supplied by the composition root.");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime refusedDesktop
            && _refusal is { } refusal)
        {
            // The start was refused before any store opened a file: the
            // window only says why.
            refusedDesktop.MainWindow = CreateRefusalWindow(refusal);
            base.OnFrameworkInitializationCompleted();
            return;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            LauncherStartupOptions startupOptions = StartupOptions;
            ApplicationPathSet paths = startupOptions.Paths;
            LauncherProfileStore profiles = LauncherProfileStore.ForApplicationPaths(paths);
            string rid = LauncherRuntimeIdentity.DetectRid();
            LauncherInstallationLayout layout = LauncherInstallationLayout.Detect(
                AppContext.BaseDirectory, rid);
            string executableSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            var installer = new LauncherInstaller(
                paths,
                Path.Combine(
                    AppContext.BaseDirectory,
                    "acdream-bake" + executableSuffix));

            LauncherVersion launcherVersion = GetLauncherVersion();
            LauncherUpdateComposition updates = LauncherUpdateComposition.Create(
                paths,
                rid,
                launcherVersion,
                layout.InstalledRoot,
                () => _orchestrator?.GetSnapshot().Sessions.Any(session => session.IsActive)
                    == true,
                updateManifestUri: startupOptions.UpdateManifestUri,
                installationLayout: layout);
            _updateComposition = updates;

            LauncherPluginComposition plugins = LauncherPluginComposition.Create(
                paths,
                startupOptions.PluginListUri);
            plugins.Recover();
            _pluginComposition = plugins;

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
                        layout,
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
            LauncherWindowViewModel windowViewModel = _viewModel;
            _viewModel.ConfigureInstallFolder(new InstallFolderViewModel(
                paths,
                new InstallRootMover(paths),
                new AvaloniaUiDispatcher(),
                () => windowViewModel.CanMoveInstallFolder,
                () => RestartLauncher(startupOptions, desktop),
                LegacyApplicationLayout.ExistingFolders(
                    paths,
                    launcherDirectory: AppContext.BaseDirectory)));
            var mainWindow = new MainWindow
            {
                DataContext = _viewModel,
            };
            desktop.MainWindow = mainWindow;
            _viewModel.UpdatePrompt.AutoOpenDiscoveredUpdates = false;
            _viewModel.ConfigureVersions(launcherVersion.Value, () => updates.Versions.CachedResolution);
            _viewModel.ConfigureServerHealth(new ServerHealthService(_serverStatusClient, new UdpServerReachabilityProbe()));
            _viewModel.ConfigureKnownServers(new KnownServerCatalog(_serverStatusClient, paths.CacheDirectory));
            _viewModel.ConfigurePlugins(plugins, () => updates.Versions.CachedResolution);
            _viewModel.Initialize();
            mainWindow.Opened += OnMainWindowOpened;
            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Opened -= OnMainWindowOpened;
        }

        if (_viewModel is { } viewModel)
        {
            await viewModel.StartBackgroundInitializationAsync();

            // Never awaited: the plugin Check pass (a list fetch, plus one request per
            // launcher-managed plugin) must not hold up the window the update check already
            // finished opening.
            _ = viewModel.Plugins.CheckNowCommand.ExecuteAsync();

            if (ReferenceEquals(_viewModel, viewModel) && viewModel.IsFirstRunRequired)
            {
                viewModel.FirstRunWizardShell.OpenCommand.Execute(null);
            }
        }
    }

    /// <summary>A plain window with the reason the launcher cannot start, selectable for copying.</summary>
    internal static Window CreateRefusalWindow(string refusal)
    {
        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 96,
        };
        var window = new Window
        {
            Title = "OpenAC",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "OpenAC cannot start",
                        FontSize = 20,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                    },
                    new SelectableTextBlock
                    {
                        Text = refusal,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    close,
                },
            },
        };
        close.Click += (_, _) => window.Close();
        return window;
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _viewModel?.Dispose();
        _orchestrator?.Dispose();
        _updateComposition?.Dispose();
        _pluginComposition?.Dispose();
        _serverStatusClient.Dispose();
        _viewModel = null;
        _orchestrator = null;
        _updateComposition = null;
        _pluginComposition = null;
    }

    /// <summary>
    /// Starts this launcher again with the same arguments and closes this one:
    /// every store it opened still names the folder the install just left.
    /// </summary>
    private static void RestartLauncher(
        LauncherStartupOptions options,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var start = new System.Diagnostics.ProcessStartInfo(
            Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The launcher executable path is unavailable."))
        {
            UseShellExecute = false,
        };
        foreach (string argument in options.PublicArguments)
        {
            start.ArgumentList.Add(argument);
        }

        System.Diagnostics.Process.Start(start)?.Dispose();
        desktop.Shutdown();
    }

    internal static LauncherVersion GetLauncherVersion()
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
