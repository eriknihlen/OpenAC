using System.ComponentModel;
using AcDream.Launcher.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AcDream.Launcher;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _statusTimer;
    private LauncherWindowViewModel? _observedViewModel;
    private Control? _focusBeforeModal;
    private bool _wasModalOpen;

    public MainWindow()
    {
        InitializeComponent();
        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _statusTimer.Tick += OnStatusTimerTick;
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e) => _statusTimer.Start();

    private void OnClosed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        DataContextChanged -= OnDataContextChanged;
        ObserveViewModel(null);
        Opened -= OnOpened;
        Closed -= OnClosed;
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (DataContext is LauncherWindowViewModel viewModel)
        {
            viewModel.PollStatus();
            viewModel.PollServerHealth(IsActive);
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e) =>
        ObserveViewModel(DataContext as LauncherWindowViewModel);

    private void ObserveViewModel(LauncherWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_observedViewModel, viewModel))
        {
            return;
        }

        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _observedViewModel.ConsoleRequested -= OnConsoleRequested;
        }

        _observedViewModel = viewModel;
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _observedViewModel.ConsoleRequested += OnConsoleRequested;
            _observedViewModel.InstallFolder?.AttachShell(new InstallFolderShell(this));
            _observedViewModel.TextEditor.UseClipboard(new ProfileEditorClipboard(this));
        }

        _wasModalOpen = viewModel?.IsModalOpen == true;
    }

    /// <summary>
    /// A console is a window of its own, not a panel over this one: a session
    /// is watched while the launcher goes on being used, and several sessions
    /// can each have theirs open.
    /// </summary>
    private void OnConsoleRequested(SessionConsoleViewModel console)
    {
        var window = new SessionConsoleWindow { DataContext = console };
        window.Show(this);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LauncherWindowViewModel.InstallFolder)
            && sender is LauncherWindowViewModel configured)
        {
            configured.InstallFolder?.AttachShell(new InstallFolderShell(this));
            return;
        }

        if (e.PropertyName != nameof(LauncherWindowViewModel.IsModalOpen)
            || sender is not LauncherWindowViewModel viewModel)
        {
            return;
        }

        bool isModalOpen = viewModel.IsModalOpen;
        if (isModalOpen && !_wasModalOpen)
        {
            _focusBeforeModal = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
            Dispatcher.UIThread.Post(() => FocusActiveModal(viewModel));
        }
        else if (!isModalOpen && _wasModalOpen)
        {
            Control? focusToRestore = _focusBeforeModal;
            _focusBeforeModal = null;
            Dispatcher.UIThread.Post(() =>
            {
                if (focusToRestore?.Focus() != true)
                {
                    AccountsScroll.Focus();
                }
            });
        }

        _wasModalOpen = isModalOpen;
    }

    private void FocusActiveModal(LauncherWindowViewModel viewModel)
    {
        if (viewModel.TextEditor.IsOpen)
        {
            if (viewModel.TextEditor.IsTextEditor)
                ProfileTextBox.Focus();
            else
                (ProfileRows.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() as Control
                    ?? AddProfileRowButton).Focus();
        }
        else if (viewModel.IsCharacterOptionsOpen)
        {
            CharacterPluginsPanel.Focus();
        }
        else if (viewModel.HasProfileMigrationNotice)
        {
            MigrationNoticeCloseButton.Focus();
        }
        else if (viewModel.IsSessionLogOpen)
        {
            SessionLogCloseButton.Focus();
        }
        else if (viewModel.IsSettingsOpen)
        {
            SettingsCloseButton.Focus();
        }
        else if (viewModel.EditorDialog.IsOpen)
        {
            Control target = viewModel.EditorDialog.Kind switch
            {
                ProfileEditorKind.AddServer or ProfileEditorKind.EditServer => ServerNameTextBox,
                ProfileEditorKind.AddAccount or ProfileEditorKind.EditAccount => AccountNameTextBox,
                ProfileEditorKind.AddCharacter or ProfileEditorKind.EditCharacter => CharacterNameTextBox,
                _ => EditorSubmitButton,
            };
            target.Focus();
        }
        else if (viewModel.FirstRunWizardShell.IsOpen)
        {
            FirstRunDatDirectoryTextBox.Focus();
        }
        else if (viewModel.UpdatePrompt.IsOpen)
        {
            UpdateCloseButton.Focus();
        }
        else if (viewModel.Plugins.InstallDialog.IsOpen)
        {
            InstallCancelButton.Focus();
        }
        else if (viewModel.Plugins.IsRemoveDialogOpen)
        {
            RemoveCancelButton.Focus();
        }
    }

    private void OnPluginsPanelSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is LauncherWindowViewModel viewModel)
        {
            viewModel.Plugins.SetPanelWidth(e.NewSize.Width);
        }
    }

    private void OnModalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not LauncherWindowViewModel viewModel)
        {
            return;
        }

        viewModel.CloseActiveModal();
        e.Handled = true;
    }

    private async void OnBrowseDatDirectory(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LauncherWindowViewModel viewModel)
        {
            return;
        }

        try
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider
                .OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Choose the retail Asheron's Call DAT directory",
                    AllowMultiple = false,
                });
            if (folders.Count > 0)
            {
                viewModel.FirstRunWizardShell.SelectDatDirectory(
                    folders[0].Path.LocalPath);
            }
        }
        catch (Exception ex)
        {
            viewModel.FirstRunWizardShell.ReportPickerError(ex.Message);
        }

        e.Handled = true;
    }
}

/// <summary>The window clipboard, for the text editors' Copy all and Paste all.</summary>
internal sealed class ProfileEditorClipboard(TopLevel window) : IProfileEditorClipboard
{
    public async Task SetTextAsync(string text)
    {
        if (window.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
    }

    public async Task<string?> GetTextAsync() =>
        window.Clipboard is { } clipboard
            ? await clipboard.TryGetTextAsync().ConfigureAwait(true)
            : null;
}

/// <summary>
/// The window's side of the install folder rows: the system file manager,
/// the clipboard and the folder picker.
/// </summary>
internal sealed class InstallFolderShell(TopLevel window) : IInstallFolderShell
{
    public async Task<bool> OpenFolderAsync(string path) =>
        await window.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path))
            .ConfigureAwait(true);

    public async Task CopyTextAsync(string text)
    {
        if (window.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> folders = await window.StorageProvider
            .OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            })
            .ConfigureAwait(true);
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }
}
