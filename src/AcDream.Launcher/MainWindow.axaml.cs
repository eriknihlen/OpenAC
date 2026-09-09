using System.ComponentModel;
using AcDream.Launcher.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

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
            viewModel.PollServerHealth();
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
        }

        _observedViewModel = viewModel;
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        _wasModalOpen = viewModel?.IsModalOpen == true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
            ProfileTextBox.Focus();
        }
        else if (viewModel.IsCharacterOptionsOpen)
        {
            CharacterPluginsTextBox.Focus();
        }
        else if (viewModel.IsSessionLogOpen)
        {
            SessionLogCloseButton.Focus();
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
