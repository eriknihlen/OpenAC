using System.Reflection;
using System.Xml.Linq;
using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AcDream.Launcher.Tests;

public sealed class MainWindowViewTests
{
    private static readonly (string Name, Type Type)[] ExpectedNamedControls =
    [
        ("AccountsScroll", typeof(ScrollViewer)),
        ("PlayCheckedButton", typeof(Button)),
        ("ProfileTextBox", typeof(TextBox)),
        ("CharacterPluginsTextBox", typeof(TextBox)),
        ("SessionLogCloseButton", typeof(Button)),
        ("ServerNameTextBox", typeof(TextBox)),
        ("AccountNameTextBox", typeof(TextBox)),
        ("CharacterNameTextBox", typeof(TextBox)),
        ("EditorSubmitButton", typeof(Button)),
        ("FirstRunDatDirectoryTextBox", typeof(TextBox)),
        ("FirstRunCloseButton", typeof(Button)),
        ("UpdateCloseButton", typeof(Button)),
    ];

    [AvaloniaFact]
    [Trait("Lane", "Manual")]
    public async Task CompiledMarkupAndEveryModalFocusPathRunInOneOwnedAvaloniaSession()
    {
        EveryExplicitlyNamedControlIsAssignedAfterConstruction();
        ReflectionSweepOfEveryXNameInMarkupFindsANonNullBackingField();
        OpeningEveryEditorKindFocusesItsPrimaryFieldAndClosingRunsTheFallbackWithoutThrowing();
        OpeningAndClosingTheFirstRunWizardFocusesAndRunsTheCloseFallbackWithoutThrowing();
        await OpeningAndClosingTheUpdatePromptFocusesAndRunsTheCloseFallbackWithoutThrowing();
        ClosingAModalRestoresThePreviouslyFocusedControlWithoutThrowing();
        ResizingKeepsBatchActionsVisibleWhileManyAccountsScroll();
    }

    private static void EveryExplicitlyNamedControlIsAssignedAfterConstruction()
    {
        var window = new MainWindow();

        foreach ((string name, Type type) in ExpectedNamedControls)
        {
            object? value = GetNamedField(window, name);
            Assert.True(
                value is not null,
                $"x:Name '{name}' was null after construction. Only the "
                    + "generated InitializeComponent() assigns x:Name backing "
                    + "fields; AvaloniaXamlLoader.Load(this) alone leaves them "
                    + "null (this is the #398 defect class).");
            Assert.IsAssignableFrom(type, value);
        }
    }

    private static void ReflectionSweepOfEveryXNameInMarkupFindsANonNullBackingField()
    {
        string markupPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AcDream.Launcher",
            "MainWindow.axaml");
        XDocument document = XDocument.Load(markupPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        List<string> names = document
            .Descendants()
            .Where(element => element.Attribute(x + "Name") is not null)
            .Where(element => !element
                .Ancestors()
                .Any(ancestor => ancestor.Name.LocalName.EndsWith(
                    "Template",
                    StringComparison.Ordinal)))
            .Select(element => element.Attribute(x + "Name")!.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(names.Count >= ExpectedNamedControls.Length);

        var window = new MainWindow();
        foreach (string name in names)
        {
            FieldInfo? field = typeof(MainWindow).GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.True(field is not null, $"No backing field found for x:Name '{name}'.");
            object? value = field!.GetValue(window);
            Assert.True(
                value is not null,
                $"x:Name '{name}' resolved to a field but its value was null "
                    + "after construction.");
        }
    }

    private static void OpeningEveryEditorKindFocusesItsPrimaryFieldAndClosingRunsTheFallbackWithoutThrowing()
    {
        (ProfileEditorKind Kind, string FocusFieldName)[] cases =
        [
            (ProfileEditorKind.AddServer, "ServerNameTextBox"),
            (ProfileEditorKind.EditServer, "ServerNameTextBox"),
            (ProfileEditorKind.AddAccount, "AccountNameTextBox"),
            (ProfileEditorKind.EditAccount, "AccountNameTextBox"),
            (ProfileEditorKind.AddCharacter, "CharacterNameTextBox"),
            (ProfileEditorKind.EditCharacter, "CharacterNameTextBox"),
            (ProfileEditorKind.Remove, "EditorSubmitButton"),
        ];

        foreach ((ProfileEditorKind kind, string expectedFocusFieldName) in cases)
        {
            using LauncherWindowViewModel viewModel = CreateViewModel();
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                window.Show();

                viewModel.EditorDialog.Open(kind, "Fixture title", _ => { });
                Assert.True(viewModel.EditorDialog.IsOpen);
                Dispatcher.UIThread.RunJobs();

                Control expectedFocus = (Control)GetNamedField(window, expectedFocusFieldName)!;
                Assert.Same(expectedFocus, CurrentFocus(window));

                viewModel.EditorDialog.Close();
                Assert.False(viewModel.EditorDialog.IsOpen);

                Dispatcher.UIThread.RunJobs();
                Assert.NotSame(expectedFocus, CurrentFocus(window));
            }
            finally
            {
                CloseTestWindow(window);
            }
        }
    }

    private static void OpeningAndClosingTheFirstRunWizardFocusesAndRunsTheCloseFallbackWithoutThrowing()
    {
        using LauncherWindowViewModel viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();

            viewModel.FirstRunWizardShell.OpenCommand.Execute(null);
            Assert.True(viewModel.FirstRunWizardShell.IsOpen);
            Dispatcher.UIThread.RunJobs();

            Control datDirectoryBox = (Control)GetNamedField(window, "FirstRunDatDirectoryTextBox")!;
            Assert.Same(datDirectoryBox, CurrentFocus(window));

            viewModel.FirstRunWizardShell.CloseCommand.Execute(null);
            Assert.False(viewModel.FirstRunWizardShell.IsOpen);

            Dispatcher.UIThread.RunJobs();
            Assert.NotSame(datDirectoryBox, CurrentFocus(window));
        }
        finally
        {
            CloseTestWindow(window);
        }
    }

    private static async Task OpeningAndClosingTheUpdatePromptFocusesAndRunsTheCloseFallbackWithoutThrowing()
    {
        using LauncherWindowViewModel viewModel = CreateViewModel(
            new UpdateAvailableUpdater());
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();

            await viewModel.UpdatePrompt.StartupCheckAsync();
            Assert.True(viewModel.UpdatePrompt.IsOpen);
            Dispatcher.UIThread.RunJobs();

            Control closeButton = (Control)GetNamedField(window, "UpdateCloseButton")!;
            Assert.Same(closeButton, CurrentFocus(window));

            viewModel.UpdatePrompt.NotNowCommand.Execute(null);
            Assert.False(viewModel.UpdatePrompt.IsOpen);

            Dispatcher.UIThread.RunJobs();
            Assert.NotSame(closeButton, CurrentFocus(window));
        }
        finally
        {
            CloseTestWindow(window);
        }
    }

    private static void ClosingAModalRestoresThePreviouslyFocusedControlWithoutThrowing()
    {
        using LauncherWindowViewModel viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();

            Control addServerButton = window
                .GetVisualDescendants()
                .OfType<Button>()
                .First(button => Equals(button.Content, "Edit Servers"));
            addServerButton.Focus();
            Assert.Same(addServerButton, CurrentFocus(window));

            viewModel.EditorDialog.Open(ProfileEditorKind.AddServer, "Fixture title", _ => { });
            Dispatcher.UIThread.RunJobs();
            Control serverName = (Control)GetNamedField(window, "ServerNameTextBox")!;
            Assert.Same(serverName, CurrentFocus(window));

            viewModel.EditorDialog.Close();
            Dispatcher.UIThread.RunJobs();

            // addServerButton was focused before the dialog opened, so the
            // restore branch (focusToRestore.Focus()) is what ran here, not
            // the account list focus fallback exercised by the tests above.
            Assert.Same(addServerButton, CurrentFocus(window));
        }
        finally
        {
            CloseTestWindow(window);
        }
    }

    private static void ResizingKeepsBatchActionsVisibleWhileManyAccountsScroll()
    {
        var source = new StubOrchestrator
        {
            ServerRows = [new LauncherServerSnapshot("Local", "127.0.0.1", 9000,
                Enumerable.Range(1, 30).Select(i => new LauncherAccountSnapshot("Local", $"account{i}",
                    [new LauncherCharacterSnapshot("Local", $"account{i}", $"Character{i}", "0x50000001",
                        LaunchMode.Gui, [], [], false, "Ready")], false, "Ready")).ToArray())],
        };
        using var model = new LauncherWindowViewModel(source, new ImmediateUiDispatcher());
        model.Initialize();
        var window = new MainWindow { DataContext = model };
        try
        {
            window.Show();
            Assert.True(window.CanResize);
            foreach (var size in new[] { new Avalonia.Size(760, 460), new Avalonia.Size(1120, 740), new Avalonia.Size(1600, 1000) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var scroll = window.FindControl<ScrollViewer>("AccountsScroll")!;
                var play = window.FindControl<Button>("PlayCheckedButton")!;
                Assert.True(scroll.Bounds.Height > 50);
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
                Avalonia.Point origin = play.TranslatePoint(default, window)!.Value;
                Assert.InRange(origin.X, 0, window.Bounds.Width - play.Bounds.Width + 1);
                Assert.InRange(origin.Y, 0, window.Bounds.Height - play.Bounds.Height + 1);
                Assert.Equal(30, model.Accounts.Count);
                string artifacts = Path.Combine(FindRepositoryRoot(), "artifacts", "launcher-redesign");
                Directory.CreateDirectory(artifacts);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(artifacts, $"launcher-{size.Width:0}x{size.Height:0}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            model.OpenSessionLogCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(window.FindControl<Button>("SessionLogCloseButton"), CurrentFocus(window));
            model.CloseActiveModal();
        }
        finally { CloseTestWindow(window); }
    }

    private static void CloseTestWindow(MainWindow window)
    {
        if (window.IsVisible)
        {
            window.Close();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static Control? CurrentFocus(MainWindow window) =>
        Avalonia.Controls.TopLevel.GetTopLevel(window)?.FocusManager?.GetFocusedElement()
            as Control;

    private static object? GetNamedField(MainWindow window, string name) =>
        typeof(MainWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(window);

    private static LauncherWindowViewModel CreateViewModel(
        AcDream.Launcher.Core.Updates.ILauncherUpdater? updater = null)
    {
        var viewModel = new LauncherWindowViewModel(
            new StubOrchestrator(),
            new ImmediateUiDispatcher(),
            installer: null,
            updater: updater);
        viewModel.Initialize();
        return viewModel;
    }

    private sealed class UpdateAvailableUpdater
        : AcDream.Launcher.Core.Updates.ILauncherUpdater
    {
        private static readonly AcDream.Launcher.Core.Updates.LauncherVersion One =
            AcDream.Launcher.Core.Updates.LauncherVersion.Parse("1.0.0");
        private static readonly AcDream.Launcher.Core.Updates.LauncherVersion Two =
            AcDream.Launcher.Core.Updates.LauncherVersion.Parse("2.0.0");

        public AcDream.Launcher.Core.Updates.ClientVersionResolution CurrentClient =>
            new(
                AcDream.Launcher.Core.Updates.ClientVersionState.Verified,
                "Fixture client verified.",
                One,
                Path.Combine("fixture", One.Value),
                null,
                null);

        public Task<AcDream.Launcher.Core.Updates.ClientVersionResolution>
            InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CurrentClient);

        public Task<AcDream.Launcher.Core.Updates.LauncherUpdateCheckResult> CheckAsync(
            CancellationToken cancellationToken = default)
        {
            var artifact = new AcDream.Launcher.Core.Updates.ReleaseArtifact(
                new Uri("https://example.test/release.zip"),
                new string('a', 64),
                100);
            var manifest = new AcDream.Launcher.Core.Updates.ReleaseManifest(
                Two,
                One,
                new Dictionary<string, AcDream.Launcher.Core.Updates.ReleaseArtifact>
                {
                    ["win-x64"] = artifact,
                },
                new Dictionary<string, AcDream.Launcher.Core.Updates.ReleaseArtifact>
                {
                    ["win-x64"] = artifact,
                });
            return Task.FromResult(
                new AcDream.Launcher.Core.Updates.LauncherUpdateCheckResult(
                    manifest,
                    "win-x64",
                    One,
                    One,
                    IsClientUpdateAvailable: true,
                    IsLauncherUpdateAvailable: false,
                    IsLauncherMinimumSatisfied: true,
                    "Fixture update available."));
        }

        public Task<AcDream.Launcher.Core.Updates.ClientVersionResolution>
            InstallClientAsync(
                AcDream.Launcher.Core.Updates.LauncherUpdateCheckResult check,
                IProgress<AcDream.Launcher.Core.Updates.LauncherUpdateProgress>? progress = null,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(CurrentClient);

        public Task<AcDream.Launcher.Core.Updates.SelfUpdateStageResult>
            StageLauncherAsync(
                AcDream.Launcher.Core.Updates.LauncherUpdateCheckResult check,
                IProgress<AcDream.Launcher.Core.Updates.LauncherUpdateProgress>? progress = null,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(new AcDream.Launcher.Core.Updates.SelfUpdateStageResult(
                Two,
                "pending.json",
                "Launcher staged."));

        public Task<AcDream.Launcher.Core.Updates.ClientVersionResolution>
            RollbackClientAsync(
                IProgress<AcDream.Launcher.Core.Updates.LauncherUpdateProgress>? progress = null,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(CurrentClient);
    }

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not find AcDream.slnx.");
    }

    [Fact]
    public void CrashReportNeverContainsAStoredPassword()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "acdream-tests",
            Path.GetRandomFileName());
        string dataDirectory = Path.Combine(root, "data");
        const string password = "hunter2-gate-round-1-secret";
        string corruptProfiles =
            "{ \"version\": 1, \"servers\": [ { \"name\": \"s\", \"host\": \"h\", "
            + "\"port\": 9000, \"accounts\": [ { \"account\": \"a\", \"password\": \""
            + password
            + "\", \"characters\": [ } ] } ] }";

        Exception failure;
        try
        {
            _ = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                corruptProfiles);
            throw new InvalidOperationException(
                "The corrupt fixture unexpectedly parsed; the test premise is broken.");
        }
        catch (System.Text.Json.JsonException jsonFailure)
        {
            failure = new InvalidOperationException(
                "Profile load failed during startup.",
                jsonFailure);
        }

        try
        {
            string? report = Program.TryWriteCrashReport(
                ["--data-dir", dataDirectory],
                failure);

            Assert.NotNull(report);
            Assert.StartsWith(dataDirectory, report, StringComparison.OrdinalIgnoreCase);
            string content = File.ReadAllText(report);
            Assert.Contains("JsonException", content);
            Assert.Contains("   at ", content);
            Assert.DoesNotContain(password, content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class StubOrchestrator : ILauncherOrchestrator
    {
        public IReadOnlyList<LauncherServerSnapshot> ServerRows { get; set; } = [];
        // Never raised: these tests exercise MainWindow's dispatcher/focus
        // wiring directly and never trigger an orchestrator-side refresh.
#pragma warning disable CS0067
        public event EventHandler? StateChanged;
#pragma warning restore CS0067

        public void LoadProfiles()
        {
        }

        public LauncherStateSnapshot GetSnapshot() => new(
            Servers: ServerRows,
            Sessions: [],
            Platform: LauncherPlatformCapabilities.Detect(),
            IsInstallationReady: false,
            InstallationStatus: "Fixture: installation not ready.");

        public LauncherCapability GetLaunchCapability(LaunchMode mode) =>
            LauncherCapability.Available;

        public LauncherCapability GetAccountLaunchCapability(
            string serverName,
            string accountName,
            LaunchMode mode) => LauncherCapability.Available;

        public LauncherCapability GetProbeCapability(string serverName, string accountName) =>
            LauncherCapability.Available;

        public void SetInstallRecord(LauncherInstallRecord? installRecord)
        {
        }

        public void AddServer(string name, string host, int port)
        {
        }

        public void EditServer(string name, string newName, string newHost, int newPort)
        {
        }

        public void RemoveServer(string name)
        {
        }

        public void AddAccount(string serverName, string accountName, string password)
        {
        }

        public void EditAccount(
            string serverName,
            string accountName,
            string newAccountName,
            string? newPassword)
        {
        }

        public void RemoveAccount(string serverName, string accountName)
        {
        }

        public void AddCharacter(
            string serverName,
            string accountName,
            string characterName,
            string? characterId)
        {
        }

        public void EditCharacterIdentity(
            string serverName,
            string accountName,
            string characterName,
            string newCharacterName,
            string? newCharacterId)
        {
        }

        public void UpdateCharacterSettings(
            string serverName,
            string accountName,
            string characterName,
            LaunchMode launchMode,
            IReadOnlyList<string> plugins,
            IReadOnlyList<string> loginCommands)
        {
        }

        public void RemoveCharacter(string serverName, string accountName, string characterName)
        {
        }

        public Task<LauncherSessionSnapshot> LaunchAsync(
            string serverName,
            string accountName,
            string? characterName,
            LaunchMode mode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateSession());

        public Task<LauncherSessionSnapshot> ProbeAsync(
            string serverName,
            string accountName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateSession());

        public Task StopSessionAsync(
            string sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void PollStatus()
        {
        }

        public void ClearFinishedSessions()
        {
        }

        public void Dispose()
        {
        }

        private static LauncherSessionSnapshot CreateSession() => new(
            "fixture-session",
            LauncherActivityKind.Play,
            "Fixture server",
            "fixture-account",
            "+Fixture",
            LaunchMode.Gui,
            LauncherActivityState.Connected,
            "Connected.",
            ExitCode: null,
            Error: null,
            CreatedAt: DateTimeOffset.UnixEpoch);
    }
}
